using System.Text.Json;
using Microsoft.Data.SqlClient;
using SqlDebugger.Sidecar.Execution;
using SqlDebugger.Sidecar.Parsing;
using Xunit;

namespace SqlDebugger.Sidecar.Tests;

/// <summary>Runs a script through the analyzer and the runner exactly as
/// Program.cs does, and reads sidecar events with a timeout.</summary>
public sealed class DebugRun
{
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(40);

    public DebugSessionRunner Runner { get; }
    public InstrumentedScript Script { get; }
    public List<string> Outputs { get; } = [];
    public List<JsonElement> ResultSets { get; } = [];

    private DebugRun(DebugSessionRunner runner, InstrumentedScript script)
    {
        Runner = runner;
        Script = script;
    }

    public record Bp(int Line, string? Condition = null, string? HitCondition = null, string? LogMessage = null);

    public static async Task<DebugRun> StartAsync(
        string connectionString, string sql, int[] breakpointLines,
        bool stopOnEntry = false, string mode = "invoke",
        Dictionary<string, object?>? parameters = null,
        string transaction = "none",
        IReadOnlyList<Bp>? breakpoints = null,
        string? debugDatabase = null)
    {
        var database = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        // __dbg can live in another database, for environments without DDL
        // rights in the target; the sidecar qualifies every call with it.
        var schemaDatabase = debugDatabase ?? database;
        var debugSchema = $"[{schemaDatabase}].__dbg";
        var analyzer = new ScriptDomAnalyzer();
        var script = mode == "module"
            ? analyzer.InstrumentModuleBody(sql, "/test.sql", debugSchema)
            : analyzer.Instrument(sql, "/test.sql", debugSchema);
        Assert.True(script.Errors.Count == 0, string.Join("; ", script.Errors));

        var options = new DebugSessionOptions(mode, transaction, schemaDatabase);
        var runner = new DebugSessionRunner(connectionString, script, options, parameters ?? []);
        var specs = breakpoints is not null
            ? breakpoints.Select(b => new BreakpointSpec(script.LineMap[b.Line], b.Condition, b.HitCondition, b.LogMessage))
            : breakpointLines.Select(l => new BreakpointSpec(script.LineMap[l], null, null, null));
        await runner.SetBreakpointsAsync(specs);
        Assert.True(runner.TryStart(stopOnEntry));
        return new DebugRun(runner, script);
    }

    /// <summary>Waits for the next event with the given name; output events are
    /// collected in Outputs. Any other significant event - paused, terminated,
    /// error - is a test failure.</summary>
    public async Task<JsonElement> ExpectAsync(string name)
    {
        using var cts = new CancellationTokenSource(EventTimeout);
        while (true)
        {
            SidecarEvent evt;
            try { evt = await Runner.Events.ReadAsync(cts.Token); }
            catch (OperationCanceledException) { throw new Xunit.Sdk.XunitException($"No '{name}' event within {EventTimeout}. Output so far:\n{string.Join("\n", Outputs)}"); }
            catch (System.Threading.Channels.ChannelClosedException) { throw new Xunit.Sdk.XunitException($"The event stream closed before '{name}'. Output:\n{string.Join("\n", Outputs)}"); }

            var data = JsonDocument.Parse(evt.JsonData).RootElement;
            if (evt.Name == "output")
            {
                Outputs.Add(data.GetProperty("text").GetString() ?? "");
                if (name == "output") return data;
                continue;
            }
            if (evt.Name == "resultset")
            {
                ResultSets.Add(data);
                if (name == "resultset") return data;
                continue;
            }
            if (evt.Name == name) return data;
            throw new Xunit.Sdk.XunitException($"Expected '{name}' but got '{evt.Name}': {evt.JsonData}\nOutput:\n{string.Join("\n", Outputs)}");
        }
    }

    public async Task<(int line, string reason)> ExpectPausedAsync()
    {
        var e = await ExpectAsync("paused");
        return (e.GetProperty("stack")[0].GetProperty("line").GetInt32(), e.GetProperty("reason").GetString()!);
    }

    public async Task<Dictionary<string, string?>> LocalsAsync()
        => (await Runner.GetLocalsAsync()).ToDictionary(l => l.Name, l => l.Value);
}
