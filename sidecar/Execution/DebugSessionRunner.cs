using System.Data;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Dapper;
using Microsoft.Data.SqlClient;
using SqlDebugger.Sidecar.Parsing;

namespace SqlDebugger.Sidecar.Execution;

public record SidecarEvent(string Name, string JsonData);
public record LocalVar(string Name, string TypeName, string? Value);

/// <summary>Breakpoint med ev. villkor, träffräkning och logpoint-meddelande
/// (utvärderas av sidecaren mot fångade locals - ingen ominstrumentering).</summary>
public record BreakpointSpec(int StmtId, string? Condition, string? HitCondition, string? LogMessage);

public record DebugSessionOptions(
    string Mode,
    string Transaction,           // none | rollback | commit
    string DebugDatabase);        // databasen där __dbg-schemat ligger

public class DebugSessionRunner
{
    /// <summary>Felnummer som __dbg.Pause kastar vid abort - skiljer avbrott från riktiga fel.</summary>
    public const int AbortErrorNumber = 50099;
    public const int HeartbeatLostErrorNumber = 50098;
    private const int MaxConsoleRows = 100;
    private const int MaxStoredRows = 5000;
    private const int MaxCellWidth = 40;
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);

    public Guid SessionId { get; } = Guid.NewGuid();
    public ChannelReader<SidecarEvent> Events => _events.Reader;

    private readonly Channel<SidecarEvent> _events = Channel.CreateUnbounded<SidecarEvent>();
    private readonly string _connectionString;
    private readonly InstrumentedScript _script;
    private readonly DebugSessionOptions _options;
    private readonly Dictionary<string, object?> _parameters;
    private readonly string _dbg; // "[Db].__dbg"
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _resumeAfterFault =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Dictionary<int, BreakpointSpec> _breakpoints = [];
    private readonly Dictionary<int, int> _hitCounts = [];
    private bool _started;
    private volatile bool _faulted;
    private int _currentBatch = -1;
    private int? _lastPausedStmt;
    private int _lastPauseSeq;

    public DebugSessionRunner(
        string connectionString, InstrumentedScript script,
        DebugSessionOptions options, Dictionary<string, object?> parameters)
    {
        _connectionString = connectionString;
        _script = script;
        _options = options;
        _parameters = parameters;
        _dbg = $"[{options.DebugDatabase.Replace("]", "]]")}].__dbg";
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
        SqlConnection? execConn = null;
        try
        {
            // Connection 1: exekverar batcharna (blockerar i __dbg.Pause)
            execConn = new SqlConnection(_connectionString);
            execConn.InfoMessage += (_, e) => EmitOutput(e.Message, "stdout"); // PRINT/RAISERROR < 11
            await execConn.OpenAsync(_cts.Token);

            await EnsureDebugSchemaAsync();
            await execConn.ExecuteAsync(
                "EXEC sp_set_session_context @key = N'__dbg_session', @value = @sid",
                new { sid = SessionId });
            await execConn.ExecuteAsync(
                $"INSERT INTO {_dbg}.Control (SessionId, Command, SignalSeq, ActiveBreakpoints) VALUES (@sid, @cmd, 0, @bp)",
                new { sid = SessionId, cmd = stopOnEntry ? "entry" : "continue", bp = EffectiveBreakpointsJson() });

            // Övervakningsloop på separat connection: upptäcker paus, pushar events, heartbeat
            _ = MonitorPauseStateAsync();

            if (_options.Transaction is "rollback" or "commit")
            {
                await execConn.ExecuteAsync("BEGIN TRANSACTION");
                EmitOutput($"-- transaction: {_options.Transaction} (all changes are {(_options.Transaction == "rollback" ? "rolled back" : "committed")} when the session ends)", "console");
            }

            // Kör batcharna i ordning på samma connection (SESSION_CONTEXT följer med).
            for (var i = 0; i < _script.Batches.Count; i++)
            {
                _currentBatch = i;
                await ExecuteBatchAsync(execConn, _script.Batches[i].Sql);
            }

            await ReportResultVariablesAsync(execConn);
            await FinishTransactionAsync(execConn, commit: _options.Transaction == "commit");
            await EmitAsync("terminated", "null");
        }
        catch (Exception ex) when (_cts.IsCancellationRequested || IsAbort(ex))
        {
            await FinishTransactionAsync(execConn, commit: false);
            await EmitAsync("terminated", "null");
        }
        catch (SqlException ex)
        {
            await StopOnExceptionAsync(ex);
            await FinishTransactionAsync(execConn, commit: false);
            await EmitAsync("terminated", "null");
        }
        catch (Exception ex)
        {
            await FinishTransactionAsync(execConn, commit: false);
            await EmitAsync("error", JsonSerializer.Serialize(ex.Message));
        }
        finally
        {
            if (execConn is not null) await execConn.DisposeAsync();
            await CleanupAsync();
            _events.Writer.TryComplete();
        }
    }

    private async Task FinishTransactionAsync(SqlConnection? conn, bool commit)
    {
        if (conn is null || _options.Transaction is not ("rollback" or "commit")) return;
        try
        {
            if (conn.State != ConnectionState.Open) return;
            var sql = commit ? "IF @@TRANCOUNT > 0 COMMIT TRANSACTION" : "IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION";
            await conn.ExecuteAsync(sql);
            EmitOutput(commit ? "-- transaction committed" : "-- transaction rolled back", "console");
        }
        catch (Exception ex)
        {
            EmitOutput($"-- could not finish transaction: {ex.Message}", "stderr");
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
        var rows = new List<string?[]>();
        var total = 0;

        while (await reader.ReadAsync(_cts.Token))
        {
            total++;
            if (rows.Count >= MaxStoredRows) continue; // töm resten men spara inte
            var row = new string?[columns.Length];
            for (var i = 0; i < columns.Length; i++)
                row[i] = reader.IsDBNull(i) ? null : FormatCell(reader.GetValue(i));
            rows.Add(row);
        }

        // Fullständig (cappad) resultatmängd till klienten för "Open result set"
        await EmitAsync("resultset", JsonSerializer.Serialize(new { columns, rows, total }));

        // Kompakt texttabell i Debug Console
        var shown = rows.Take(MaxConsoleRows).Select(r => r.Select(c => Truncate(c ?? "NULL")).ToArray()).ToList();
        var widths = columns.Select((c, i) =>
            Math.Max(c.Length, shown.Count == 0 ? 0 : shown.Max(r => r[i].Length))).ToArray();
        var sb = new StringBuilder();
        sb.AppendLine(string.Join("  ", columns.Select((c, i) => c.PadRight(widths[i]))));
        sb.AppendLine(string.Join("  ", widths.Select(w => new string('-', w))));
        foreach (var row in shown)
            sb.AppendLine(string.Join("  ", row.Select((c, i) => c.PadRight(widths[i]))));
        sb.AppendLine(total > shown.Count
            ? $"({total} rows, showing the first {shown.Count} - use 'sqldbgr: Open last result set' for all)"
            : $"({total} rows)");
        EmitOutput(sb.ToString(), "stdout");
    }

    private static string FormatCell(object value) => value switch
    {
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss.fff"),
        DateTimeOffset d => d.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"),
        byte[] b => "0x" + Convert.ToHexString(b),
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? ""
    };

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
            $"SELECT Name, TypeName, Value FROM {_dbg}.Locals WITH (NOLOCK) WHERE SessionId = @sid AND Name IN @names",
            new { sid = SessionId, names = _script.ResultVariables });
        var byName = locals.ToDictionary(l => l.Name);

        var sb = new StringBuilder("-- Result:\n");
        foreach (var name in _script.ResultVariables)
        {
            var label = name == "@__dbg_return" ? "return value" : name;
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
        var message = $"Error {ex.Number} (line {(span?.Line ?? line)}): {ex.Message}";

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

    public async Task SetBreakpointsAsync(IEnumerable<BreakpointSpec> specs)
    {
        _breakpoints = specs.ToDictionary(b => b.StmtId);
        if (!_started) return; // skrivs in när Control-raden skapas vid start
        await using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync(
            $"UPDATE {_dbg}.Control SET ActiveBreakpoints = @bp WHERE SessionId = @sid",
            new { bp = EffectiveBreakpointsJson(), sid = SessionId });
    }

    // I modulläge stannar vi alltid på slutläget (RETURN/slut på kroppen) så
    // returvärdet och OUTPUT-parametrarna går att se även utan breakpoints.
    private string EffectiveBreakpointsJson() => JsonSerializer.Serialize(
        _options.Mode == "module"
            ? _breakpoints.Keys.Concat(_script.FinalStmtIds).Distinct()
            : _breakpoints.Keys);

    public async Task SignalAsync(string command)
    {
        if (_faulted) { _resumeAfterFault.TrySetResult(); return; }
        await using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync(
            $"UPDATE {_dbg}.Control SET Command = @cmd, SignalSeq = SignalSeq + 1 WHERE SessionId = @sid",
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

    /// <summary>Locals läses med NOLOCK: batchen skriver dem inne i en ev. transaktion.</summary>
    public async Task<List<LocalVar>> GetLocalsAsync()
    {
        await using var conn = new SqlConnection(_connectionString);
        var rows = await conn.QueryAsync<LocalVar>(
            $"SELECT Name, TypeName, Value FROM {_dbg}.Locals WITH (NOLOCK) WHERE SessionId = @sid ORDER BY Ordinal, Name",
            new { sid = SessionId });
        return rows.ToList();
    }

    /// <summary>setVariable: värdet läses in av batchen efter nästa resume.
    /// Locals uppdateras direkt så panelen speglar ändringen.</summary>
    public async Task SetVariableAsync(string name, string? value)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync($"""
            DELETE FROM {_dbg}.Overrides WHERE SessionId = @sid AND Name = @name;
            INSERT INTO {_dbg}.Overrides (SessionId, Name, Value) VALUES (@sid, @name, @value);
            UPDATE {_dbg}.Locals SET Value = @value WHERE SessionId = @sid AND Name = @name;
            """, new { sid = SessionId, name, value });
    }

    private async Task MonitorPauseStateAsync()
    {
        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(_cts.Token);
            var lastHeartbeat = DateTime.UtcNow;

            while (!_cts.IsCancellationRequested)
            {
                if (DateTime.UtcNow - lastHeartbeat > HeartbeatInterval)
                {
                    await conn.ExecuteAsync(
                        $"UPDATE {_dbg}.Control SET LastHeartbeatUtc = SYSUTCDATETIME() WHERE SessionId = @sid",
                        new { sid = SessionId });
                    lastHeartbeat = DateTime.UtcNow;
                }

                var state = await conn.QuerySingleOrDefaultAsync<(int? PausedAtStmt, int PauseSeq, string? Command)>($"""
                    SELECT p.PausedAtStmt, p.PauseSeq, c.Command
                    FROM {_dbg}.PauseState p WITH (NOLOCK)
                    JOIN {_dbg}.Control c WITH (NOLOCK) ON c.SessionId = p.SessionId
                    WHERE p.SessionId = @sid
                    """, new { sid = SessionId });

                // PauseSeq (inte statement-id) avgör om det är en NY paus: en loop
                // pausar på samma id varje varv, snabbare än pollintervallet.
                if (state.PausedAtStmt is int stmt && state.PauseSeq != _lastPauseSeq)
                {
                    _lastPauseSeq = state.PauseSeq;
                    _lastPausedStmt = stmt;

                    var isBreakpointHit = state.Command is "continue";
                    if (isBreakpointHit && !await ShouldStopAtBreakpointAsync(stmt))
                    {
                        await SignalAsync("continue");
                        continue;
                    }

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
                        stack = new[] { StackFrame(_script.StmtToSpan.GetValueOrDefault(stmt), 1) }
                    }));
                }

                await Task.Delay(50, _cts.Token);
            }
        }
        catch (OperationCanceledException) { /* sessionen stoppad */ }
        catch (Exception ex)
        {
            EmitOutput($"[sidecar] monitor stopped: {ex.Message}", "stderr");
        }
    }

    /// <summary>Villkor, träffräkning och logpoints utvärderas här mot de fångade
    /// locals (skalärer deklareras med sina typer på en egen connection).
    /// Villkor som inte går att utvärdera räknas som sanna och rapporteras.</summary>
    private async Task<bool> ShouldStopAtBreakpointAsync(int stmtId)
    {
        if (!_breakpoints.TryGetValue(stmtId, out var bp)) return true;
        if (bp.Condition is null && bp.HitCondition is null && bp.LogMessage is null) return true;

        if (bp.Condition is not null)
        {
            var result = await EvaluateAsync($"CASE WHEN ({bp.Condition}) THEN 1 ELSE 0 END", stmtId);
            if (result.error is not null)
                EmitOutput($"[breakpoint] condition '{bp.Condition}' could not be evaluated: {result.error}", "stderr");
            else if (result.value != "1")
                return false;
        }

        if (bp.HitCondition is not null)
        {
            var hits = _hitCounts.GetValueOrDefault(stmtId) + 1;
            _hitCounts[stmtId] = hits;
            if (!HitConditionMet(bp.HitCondition, hits)) return false;
        }

        if (bp.LogMessage is not null)
        {
            var parts = Regex.Split(bp.LogMessage, @"\{([^}]+)\}");
            var expr = "CONCAT(" + string.Join(", ", parts.Select((p, i) => i % 2 == 0
                ? $"N'{p.Replace("'", "''")}'"
                : $"CAST(({p}) AS NVARCHAR(MAX))")) + ")";
            var result = await EvaluateAsync(expr, stmtId);
            EmitOutput(result.error is null ? result.value ?? "" : $"[logpoint] {bp.LogMessage}: {result.error}", result.error is null ? "console" : "stderr");
            return false; // logpoints stannar inte
        }
        return true;
    }

    private static bool HitConditionMet(string condition, int hits)
    {
        var m = Regex.Match(condition.Trim(), @"^(==|=|>=|>|<=|<|%)?\s*(\d+)$");
        if (!m.Success) return true;
        var n = int.Parse(m.Groups[2].Value);
        return m.Groups[1].Value switch
        {
            ">" => hits > n,
            ">=" => hits >= n,
            "<" => hits < n,
            "<=" => hits <= n,
            "%" => n > 0 && hits % n == 0,
            _ => hits == n
        };
    }

    /// <summary>Utvärderar ett T-SQL-uttryck med de fångade skalära locals som
    /// deklarerade variabler (används för villkor, logpoints och hover/watch).</summary>
    public async Task<(string? value, string? error)> EvaluateAsync(string expression, int? stmtId = null)
    {
        var locals = await GetLocalsAsync();
        var types = stmtId is int id && _script.ScopeMap.TryGetValue(id, out var scope)
            ? scope.Where(v => !v.IsTable).ToDictionary(v => v.Name, v => v.TypeName)
            : locals.Where(l => !l.TypeName.StartsWith("TABLE")).ToDictionary(l => l.Name, l => l.TypeName);

        var sb = new StringBuilder("SET DATEFORMAT ymd;\n");
        foreach (var l in locals.Where(l => !l.TypeName.StartsWith("TABLE") && types.ContainsKey(l.Name)))
        {
            var type = types[l.Name];
            var t = type.ToLowerInvariant();
            var literal = l.Value is null ? "NULL" : $"N'{l.Value.Replace("'", "''")}'";
            var init = l.Value is not null && (t.StartsWith("binary") || t.StartsWith("varbinary"))
                ? $"CONVERT({type}, {literal}, 1)" : literal;
            sb.AppendLine($"DECLARE {l.Name} {type} = {init};");
        }
        sb.AppendLine($"SELECT CAST(({expression}) AS NVARCHAR(MAX));");

        try
        {
            await using var conn = new SqlConnection(_connectionString);
            var value = await conn.ExecuteScalarAsync<string?>(sb.ToString());
            return (value, null);
        }
        catch (SqlException ex)
        {
            return (null, ex.Message);
        }
    }

    private async Task EnsureDebugSchemaAsync()
    {
        var builder = new SqlConnectionStringBuilder(_connectionString) { InitialCatalog = _options.DebugDatabase };
        await using var conn = new SqlConnection(builder.ConnectionString);
        await conn.OpenAsync(_cts.Token);

        var schemaSql = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Db", "DebugSchema.sql"), _cts.Token);
        foreach (var batch in schemaSql.Split("\nGO", StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = batch.Trim();
            if (trimmed.Length > 0)
                await conn.ExecuteAsync(trimmed);
        }

        // Föräldralösa sessioner (sidecar som dött): heartbeaten har tystnat.
        await conn.ExecuteAsync("""
            DECLARE @dead TABLE (SessionId UNIQUEIDENTIFIER);
            INSERT INTO @dead SELECT SessionId FROM __dbg.Control WHERE LastHeartbeatUtc < DATEADD(MINUTE, -2, SYSUTCDATETIME());
            DELETE FROM __dbg.Locals WHERE SessionId IN (SELECT SessionId FROM @dead);
            DELETE FROM __dbg.Overrides WHERE SessionId IN (SELECT SessionId FROM @dead);
            DELETE FROM __dbg.PauseState WHERE SessionId IN (SELECT SessionId FROM @dead);
            DELETE FROM __dbg.Control WHERE SessionId IN (SELECT SessionId FROM @dead);
            """);
    }

    private async Task CleanupAsync()
    {
        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.ExecuteAsync($"""
                DELETE FROM {_dbg}.Control WHERE SessionId = @sid;
                DELETE FROM {_dbg}.PauseState WHERE SessionId = @sid;
                DELETE FROM {_dbg}.Locals WHERE SessionId = @sid;
                DELETE FROM {_dbg}.Overrides WHERE SessionId = @sid;
                """, new { sid = SessionId });
        }
        catch { /* best effort */ }
    }

    private static bool IsAbort(Exception ex)
        => ex is SqlException sql && sql.Errors.Cast<SqlError>().Any(e => e.Number is AbortErrorNumber or HeartbeatLostErrorNumber);

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
