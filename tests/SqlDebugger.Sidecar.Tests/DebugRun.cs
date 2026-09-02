using System.Text.Json;
using Microsoft.Data.SqlClient;
using SqlDebugger.Sidecar.Execution;
using SqlDebugger.Sidecar.Parsing;
using Xunit;

namespace SqlDebugger.Sidecar.Tests;

/// <summary>Kör ett script genom analysator + runner precis som Program.cs gör,
/// och läser sidecar-events med timeout.</summary>
public sealed class DebugRun
{
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(40);

    public DebugSessionRunner Runner { get; }
    public InstrumentedScript Script { get; }
    public List<string> Outputs { get; } = [];

    private DebugRun(DebugSessionRunner runner, InstrumentedScript script)
    {
        Runner = runner;
        Script = script;
    }

    public static async Task<DebugRun> StartAsync(
        string connectionString, string sql, int[] breakpointLines,
        bool stopOnEntry = false, string mode = "invoke",
        Dictionary<string, object?>? parameters = null)
    {
        var database = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        var debugSchema = $"[{database}].__dbg";
        var analyzer = new ScriptDomAnalyzer();
        var script = mode == "module"
            ? analyzer.InstrumentModuleBody(sql, "/test.sql", debugSchema)
            : analyzer.Instrument(sql, "/test.sql", debugSchema);
        Assert.True(script.Errors.Count == 0, string.Join("; ", script.Errors));

        var runner = new DebugSessionRunner(connectionString, script, mode, parameters ?? []);
        await runner.SetBreakpointsAsync(breakpointLines.Select(l => script.LineMap[l]).ToArray());
        Assert.True(runner.TryStart(stopOnEntry));
        return new DebugRun(runner, script);
    }

    /// <summary>Väntar på nästa event med angivet namn; output-events samlas i Outputs.
    /// Ett annat "stort" event (paused/terminated/error) än det väntade är ett testfel.</summary>
    public async Task<JsonElement> ExpectAsync(string name)
    {
        using var cts = new CancellationTokenSource(EventTimeout);
        while (true)
        {
            SidecarEvent evt;
            try { evt = await Runner.Events.ReadAsync(cts.Token); }
            catch (OperationCanceledException) { throw new Xunit.Sdk.XunitException($"Fick inget '{name}'-event inom {EventTimeout}. Output hittills:\n{string.Join("\n", Outputs)}"); }
            catch (System.Threading.Channels.ChannelClosedException) { throw new Xunit.Sdk.XunitException($"Eventströmmen stängdes innan '{name}'. Output:\n{string.Join("\n", Outputs)}"); }

            var data = JsonDocument.Parse(evt.JsonData).RootElement;
            if (evt.Name == "output")
            {
                Outputs.Add(data.GetProperty("text").GetString() ?? "");
                if (name == "output") return data;
                continue;
            }
            if (evt.Name == name) return data;
            throw new Xunit.Sdk.XunitException($"Väntade '{name}' men fick '{evt.Name}': {evt.JsonData}\nOutput:\n{string.Join("\n", Outputs)}");
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
