using System.Data;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Dapper;
using Microsoft.Data.SqlClient;
using SqlDebugger.Sidecar.Parsing;

namespace SqlDebugger.Sidecar.Execution;

public record SidecarEvent(string Name, string JsonData);
public record LocalVar(string Name, string TypeName, string? Value);

public class DebugSessionRunner
{
    /// <summary>Felnummer som __dbg.Pause kastar vid abort - skiljer avbrott från riktiga fel.</summary>
    public const int AbortErrorNumber = 50099;
    private const int MaxResultRows = 100;
    private const int MaxCellWidth = 40;

    public Guid SessionId { get; } = Guid.NewGuid();
    public ChannelReader<SidecarEvent> Events => _events.Reader;

    private readonly Channel<SidecarEvent> _events = Channel.CreateUnbounded<SidecarEvent>();
    private readonly string _connectionString;
    private readonly InstrumentedScript _script;
    private readonly string _mode;
    private readonly Dictionary<string, object?> _parameters;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _resumeAfterFault =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int[] _breakpoints = [];
    private bool _started;
    private volatile bool _faulted;
    private int _currentBatch = -1;
    private int? _lastPausedStmt;
    private int _lastPauseSeq;

    public DebugSessionRunner(
        string connectionString, InstrumentedScript script,
        string mode, Dictionary<string, object?> parameters)
    {
        _connectionString = connectionString;
        _script = script;
        _mode = mode;
        _parameters = parameters;
    }

    /// <summary>Startar körningen. Anropas först när klienten satt sina breakpoints
    /// (DAP configurationDone) - annars tappas breakpoints satta före F5.</summary>
    public bool TryStart(bool stopOnEntry)
    {
        if (_started) return false;
        _started = true;
        _ = RunAsync(stopOnEntry);
        return true;
    }

    private async Task RunAsync(bool stopOnEntry)
    {
        try
        {
            // Connection 1: exekverar batcharna (blockerar i __dbg.Pause)
            await using var execConn = new SqlConnection(_connectionString);
            execConn.InfoMessage += (_, e) => EmitOutput(e.Message, "stdout"); // PRINT/RAISERROR < 11
            await execConn.OpenAsync(_cts.Token);

            await EnsureDebugSchemaAsync(execConn);
            await execConn.ExecuteAsync(
                "EXEC sp_set_session_context @key = N'__dbg_session', @value = @sid",
                new { sid = SessionId });
            await execConn.ExecuteAsync(
                "INSERT INTO __dbg.Control (SessionId, Command, Signaled, ActiveBreakpoints) VALUES (@sid, @cmd, 0, @bp)",
                new { sid = SessionId, cmd = stopOnEntry ? "entry" : "continue", bp = EffectiveBreakpointsJson() });

            // Övervakningsloop på separat connection: upptäcker paus och pushar events
            _ = MonitorPauseStateAsync();

            // Kör batcharna i ordning på samma connection (SESSION_CONTEXT följer med).
            // I modulläge binds funktions-/procedurparametrarna som riktiga
            // query-parametrar - inga literaler, ingen quoting.
            for (var i = 0; i < _script.Batches.Count; i++)
            {
                _currentBatch = i;
                await ExecuteBatchAsync(execConn, _script.Batches[i].Sql);
            }

            await ReportResultVariablesAsync(execConn);
            await EmitAsync("terminated", "null");
        }
        catch (Exception ex) when (_cts.IsCancellationRequested || IsAbort(ex))
        {
            await EmitAsync("terminated", "null");
        }
        catch (SqlException ex)
        {
            await StopOnExceptionAsync(ex);
        }
        catch (Exception ex)
        {
            await EmitAsync("error", JsonSerializer.Serialize(ex.Message));
        }
        finally
        {
            await CleanupAsync();
            _events.Writer.TryComplete();
        }
    }

    private async Task ExecuteBatchAsync(SqlConnection conn, string sql)
    {
        // CommandTimeout 0 = vänta hur länge som helst (användaren kan stå pausad i minuter).
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 0 };
        // Binds som @__p_<namn>; modulpreludet deklarerar om dem med signaturens typ.
        foreach (var (name, value) in _parameters)
            cmd.Parameters.AddWithValue(ScriptDomAnalyzer.BoundParameterName(name),
                NormalizeParamValue(value) ?? DBNull.Value);

        // Reader i stället för Execute så resultatmängder (SELECT) kan visas
        // i Debug Console i stället för att kastas bort.
        await using var reader = await cmd.ExecuteReaderAsync(_cts.Token);
        do
        {
            if (reader.FieldCount > 0)
                await EmitResultSetAsync(reader);
        } while (await reader.NextResultAsync(_cts.Token));
    }

    private async Task EmitResultSetAsync(SqlDataReader reader)
    {
        var columns = Enumerable.Range(0, reader.FieldCount)
            .Select(i => string.IsNullOrEmpty(reader.GetName(i)) ? $"(col{i + 1})" : reader.GetName(i))
            .ToArray();
        var rows = new List<string[]>();
        var total = 0;

        while (await reader.ReadAsync(_cts.Token))
        {
            total++;
            if (rows.Count >= MaxResultRows) continue; // töm resten men visa inte
            var row = new string[columns.Length];
            for (var i = 0; i < columns.Length; i++)
                row[i] = reader.IsDBNull(i) ? "NULL" : Truncate(Convert.ToString(reader.GetValue(i)) ?? "");
            rows.Add(row);
        }

        var widths = columns.Select((c, i) =>
            Math.Max(c.Length, rows.Count == 0 ? 0 : rows.Max(r => r[i].Length))).ToArray();
        var sb = new StringBuilder();
        sb.AppendLine(string.Join("  ", columns.Select((c, i) => c.PadRight(widths[i]))));
        sb.AppendLine(string.Join("  ", widths.Select(w => new string('-', w))));
        foreach (var row in rows)
            sb.AppendLine(string.Join("  ", row.Select((c, i) => c.PadRight(widths[i]))));
        sb.AppendLine(total > rows.Count
            ? $"({total} rader, visar de första {rows.Count})"
            : $"({total} rader)");
        EmitOutput(sb.ToString(), "stdout");
    }

    private static string Truncate(string s)
    {
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length <= MaxCellWidth ? s : s[..(MaxCellWidth - 1)] + "…";
    }

    /// <summary>Modulläge: skriv returvärde och OUTPUT-parametrar till Debug Console
    /// vid avslut - Locals försvinner med sessionen annars.</summary>
    private async Task ReportResultVariablesAsync(SqlConnection conn)
    {
        if (_script.ResultVariables.Count == 0) return;
        var locals = await conn.QueryAsync<LocalVar>(
            "SELECT Name, TypeName, Value FROM __dbg.Locals WHERE SessionId = @sid AND Name IN @names",
            new { sid = SessionId, names = _script.ResultVariables });
        var byName = locals.ToDictionary(l => l.Name);

        var sb = new StringBuilder("-- Resultat:\n");
        foreach (var name in _script.ResultVariables)
        {
            var label = name == "@__dbg_return" ? "returvärde" : name;
            var value = byName.TryGetValue(name, out var l) ? l.Value ?? "NULL" : "?";
            sb.AppendLine($"   {label} = {value}");
        }
        EmitOutput(sb.ToString(), "stdout");
    }

    /// <summary>SQL-fel: mappa raden i den instrumenterade batchen till originalfilen
    /// och stanna där ("stopped on exception") så Locals (fångade före det
    /// fallerande statementet) kan inspekteras. Continue/stop avslutar.</summary>
    private async Task StopOnExceptionAsync(SqlException ex)
    {
        var line = _currentBatch >= 0 ? _script.Batches[_currentBatch].MapLine(ex.LineNumber) : 0;
        var span = FindSpanForLine(line)
            ?? (_lastPausedStmt is int last ? _script.StmtToSpan.GetValueOrDefault(last) : null);
        var message = $"Fel {ex.Number} (rad {(span?.Line ?? line)}): {ex.Message}";

        EmitOutput(message, "stderr");
        _faulted = true;
        await EmitAsync("paused", JsonSerializer.Serialize(new
        {
            reason = "exception",
            text = ex.Message,
            stack = new[] { StackFrame(span, line) }
        }));

        using var reg = _cts.Token.Register(() => _resumeAfterFault.TrySetResult());
        await _resumeAfterFault.Task;
        await EmitAsync("terminated", "null");
    }

    private StatementSpan? FindSpanForLine(int line)
    {
        if (line <= 0) return null;
        return _script.StmtToSpan.Values
            .Where(s => s.Line <= line && line <= s.EndLine)
            .OrderByDescending(s => s.Line)
            .FirstOrDefault();
    }

    private object StackFrame(StatementSpan? span, int fallbackLine) => new
    {
        frameName = Path.GetFileName(_script.SourcePath),
        sourcePath = (string?)_script.SourcePath,
        line = span?.Line ?? Math.Max(fallbackLine, 1),
        column = span?.Column ?? 1,
        endLine = span?.EndLine,
        endColumn = span?.EndColumn
    };

    public async Task SetBreakpointsAsync(int[] stmtIds)
    {
        _breakpoints = stmtIds;
        if (!_started) return; // skrivs in när Control-raden skapas vid start
        await using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync(
            "UPDATE __dbg.Control SET ActiveBreakpoints = @bp WHERE SessionId = @sid",
            new { bp = EffectiveBreakpointsJson(), sid = SessionId });
    }

    // I modulläge stannar vi alltid på slutläget (RETURN/slut på kroppen) så
    // returvärdet och OUTPUT-parametrarna går att se även utan breakpoints.
    private string EffectiveBreakpointsJson() => JsonSerializer.Serialize(
        _mode == "module" ? _breakpoints.Concat(_script.FinalStmtIds).Distinct() : _breakpoints);

    public async Task SignalAsync(string command)
    {
        if (_faulted) { _resumeAfterFault.TrySetResult(); return; }
        await using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync(
            "UPDATE __dbg.Control SET Command = @cmd, Signaled = 1 WHERE SessionId = @sid",
            new { cmd = command, sid = SessionId });
    }

    /// <summary>Avbryter direkt: 'abort' får __dbg.Pause att THROW vid pauspunkten,
    /// och cancel skickar attention till en batch som kör. Inga fler statements körs.</summary>
    public async Task StopAsync()
    {
        try { if (_started && !_faulted) await SignalAsync("abort"); }
        catch { /* best effort - cancel nedan tar resten */ }
        _resumeAfterFault.TrySetResult();
        _cts.Cancel();
    }

    public async Task<List<LocalVar>> GetLocalsAsync()
    {
        await using var conn = new SqlConnection(_connectionString);
        var rows = await conn.QueryAsync<LocalVar>(
            "SELECT Name, TypeName, Value FROM __dbg.Locals WHERE SessionId = @sid ORDER BY Name",
            new { sid = SessionId });
        return rows.ToList();
    }

    private async Task MonitorPauseStateAsync()
    {
        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(_cts.Token);

            while (!_cts.IsCancellationRequested)
            {
                var state = await conn.QuerySingleOrDefaultAsync<(int? PausedAtStmt, string? Command, int PauseSeq)>(
                    "SELECT PausedAtStmt, Command, PauseSeq FROM __dbg.Control WHERE SessionId = @sid",
                    new { sid = SessionId });

                // PauseSeq (inte statement-id) avgör om det är en NY paus: en loop
                // pausar på samma id varje varv, snabbare än pollintervallet.
                if (state.PausedAtStmt is int stmt && state.PauseSeq != _lastPauseSeq)
                {
                    _lastPauseSeq = state.PauseSeq;
                    _lastPausedStmt = stmt;
                    var span = _script.StmtToSpan.GetValueOrDefault(stmt);
                    var reason = state.Command switch
                    {
                        "entry" => "entry",
                        "stepOver" or "stepIn" => "step",
                        _ => "breakpoint"
                    };
                    await EmitAsync("paused", JsonSerializer.Serialize(new
                    {
                        reason,
                        text = (string?)null,
                        stack = new[] { StackFrame(span, 1) }
                    }));
                }

                await Task.Delay(50, _cts.Token);
            }
        }
        catch (OperationCanceledException) { /* sessionen stoppad */ }
        catch (Exception ex)
        {
            EmitOutput($"[sidecar] övervakningen avbröts: {ex.Message}", "stderr");
        }
    }

    private async Task EnsureDebugSchemaAsync(SqlConnection conn)
    {
        var schemaSql = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Db", "DebugSchema.sql"), _cts.Token);
        foreach (var batch in schemaSql.Split("\nGO", StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = batch.Trim();
            if (trimmed.Length > 0)
                await conn.ExecuteAsync(trimmed);
        }
    }

    private async Task CleanupAsync()
    {
        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.ExecuteAsync(
                "DELETE FROM __dbg.Control WHERE SessionId = @sid; DELETE FROM __dbg.Locals WHERE SessionId = @sid;",
                new { sid = SessionId });
        }
        catch { /* best effort */ }
    }

    private static bool IsAbort(Exception ex)
        => ex is SqlException sql && sql.Errors.Cast<SqlError>().Any(e => e.Number == AbortErrorNumber);

    // Värden från extensionen kommer som JsonElement via System.Text.Json.
    private static object? NormalizeParamValue(object? value) => value is JsonElement je
        ? je.ValueKind switch
        {
            JsonValueKind.String => je.GetString(),
            JsonValueKind.Number => je.TryGetInt64(out var l) ? l : je.GetDecimal(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => je.GetRawText()
        }
        : value;

    private void EmitOutput(string text, string category)
        => _events.Writer.TryWrite(new SidecarEvent("output",
            JsonSerializer.Serialize(new { category, text })));

    private async Task EmitAsync(string name, string jsonData)
        => await _events.Writer.WriteAsync(new SidecarEvent(name, jsonData));
}
