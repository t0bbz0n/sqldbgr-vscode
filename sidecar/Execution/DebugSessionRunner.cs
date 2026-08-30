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
    public Guid SessionId { get; } = Guid.NewGuid();
    public ChannelReader<SidecarEvent> Events => _events.Reader;

    private readonly Channel<SidecarEvent> _events = Channel.CreateUnbounded<SidecarEvent>();
    private readonly string _connectionString;
    private readonly InstrumentedScript _script;
    private readonly string _mode;
    private readonly CancellationTokenSource _cts = new();

    public DebugSessionRunner(
        string connectionString, InstrumentedScript script,
        string mode, Dictionary<string, object?> parameters)
    {
        _connectionString = connectionString;
        _script = script;
        _mode = mode;
    }

    public async Task RunAsync()
    {
        try
        {
            // Connection 1: exekverar batchen (blockerar i __dbg.Pause)
            await using var execConn = new SqlConnection(_connectionString);
            await execConn.OpenAsync(_cts.Token);

            await EnsureDebugSchemaAsync(execConn);
            await execConn.ExecuteAsync(
                "EXEC sp_set_session_context @key = N'__dbg_session', @value = @sid",
                new { sid = SessionId });
            await execConn.ExecuteAsync(
                "INSERT INTO __dbg.Control (SessionId, Command, Signaled) VALUES (@sid, 'entry', 0)",
                new { sid = SessionId });

            // Övervakningsloop på separat connection: upptäcker paus och pushar events
            _ = MonitorPauseStateAsync();

            // Kör hela den instrumenterade batchen. CommandTimeout 0 = vänta hur länge som helst
            // (användaren kan stå pausad i minuter).
            await execConn.ExecuteAsync(
                new CommandDefinition(_script.Sql, commandTimeout: 0, cancellationToken: _cts.Token));

            await EmitAsync("terminated", "null");
        }
        catch (OperationCanceledException)
        {
            await EmitAsync("terminated", "null");
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

    public async Task SetBreakpointsAsync(int[] stmtIds)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync(
            "UPDATE __dbg.Control SET ActiveBreakpoints = @bp WHERE SessionId = @sid",
            new { bp = JsonSerializer.Serialize(stmtIds), sid = SessionId });
    }

    public async Task SignalAsync(string command)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync(
            "UPDATE __dbg.Control SET Command = @cmd, Signaled = 1 WHERE SessionId = @sid",
            new { cmd = command, sid = SessionId });
    }

    public async Task<List<LocalVar>> GetLocalsAsync()
    {
        await using var conn = new SqlConnection(_connectionString);
        var rows = await conn.QueryAsync<LocalVar>(
            "SELECT Name, TypeName, Value FROM __dbg.Locals WHERE SessionId = @sid ORDER BY Name",
            new { sid = SessionId });
        return rows.ToList();
    }

    public async Task StopAsync()
    {
        // Släpp ev. pågående paus så batchen kan avslutas, avbryt sedan
        await SignalAsync("continue").ConfigureAwait(false);
        _cts.Cancel();
    }

    private async Task MonitorPauseStateAsync()
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(_cts.Token);
        int? lastPausedStmt = null;

        while (!_cts.IsCancellationRequested)
        {
            var state = await conn.QuerySingleOrDefaultAsync<(int? PausedAtStmt, string? Command)>(
                "SELECT PausedAtStmt, Command FROM __dbg.Control WHERE SessionId = @sid",
                new { sid = SessionId });

            if (state.PausedAtStmt is int stmt && stmt != lastPausedStmt)
            {
                lastPausedStmt = stmt;
                var span = _script.StmtToSpan.GetValueOrDefault(stmt);
                var reason = state.Command is "stepOver" or "stepIn" ? "step" : "breakpoint";
                await EmitAsync("paused", JsonSerializer.Serialize(new
                {
                    reason,
                    stack = new[]
                    {
                        new
                        {
                            frameName = Path.GetFileName(_script.SourcePath),
                            sourcePath = (string?)_script.SourcePath,
                            line = span?.Line ?? 1,
                            column = span?.Column ?? 1,
                            endLine = span?.EndLine,
                            endColumn = span?.EndColumn
                        }
                    }
                }));
            }
            else if (state.PausedAtStmt is null)
            {
                lastPausedStmt = null;
            }

            await Task.Delay(50, _cts.Token);
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

    private async Task EmitAsync(string name, string jsonData)
        => await _events.Writer.WriteAsync(new SidecarEvent(name, jsonData));
}
