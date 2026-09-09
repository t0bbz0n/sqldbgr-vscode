using System.Text.Json;
using Xunit;

namespace SqlDebugger.Sidecar.Tests;

/// <summary>
/// Drives the sidecar over HTTP exactly as the extension does: inspect the
/// file, start a session, set breakpoints, open the SSE stream, run, read
/// locals, evaluate, change a variable and step on. The other integration tests
/// call DebugSessionRunner directly, so none of this - routing, JSON shapes, the
/// SSE format, auth - is covered by them.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class SidecarApiTests(SqlServerFixture fixture)
{
    private string Cs => fixture.ConnectionString ?? throw new InvalidOperationException();
    private void RequireSqlServer() => Skip.If(fixture.ConnectionString is null, "SQLDBGR_TEST_CONNECTION is not set");

    private record StatementSpan(int StmtId, int Line, int EndLine);
    private record StartResponse(Guid SessionId, StatementSpan[] Statements);
    private record Local(string Name, string TypeName, string? Value);
    private record EvaluateResponse(string? Value, string? Error);
    private record HealthResponse(string Status, string Service, string Version);
    private record ModuleParameter(string Name, string TypeName, string? DefaultValue, bool IsOutput);
    private record Module(string Kind, string Name, ModuleParameter[] Parameters, bool CanScriptify, string? Reason);
    private record ParseIssue(int Line, int Column, string Message);
    private record InspectResponse(Module? Module, ParseIssue[] ParseErrors);

    private const string Script = """
        DECLARE @x INT = 1;
        SET @x = @x + 1;
        PRINT 'hello';
        SELECT @x AS X;
        """;

    /// <summary>Waits for an event, but lets the pump's exception win. If the
    /// pump falls over quietly, the symptom is otherwise just "timed out", which
    /// says nothing about why.
    ///
    /// If the pump ends without the event arriving, the wait is lost: that
    /// TaskCompletionSource is never set. Simply awaiting it here hangs forever,
    /// and that is exactly what stalled the test run - the whole job died on
    /// blame-hang with no line naming the cause.</summary>
    private static async Task WaitAsync(
        Task awaited, Task pump, string what, SidecarProcess sidecar,
        List<(string Name, string Data)> events)
    {
        var timeout = Task.Delay(TimeSpan.FromSeconds(60));
        var finished = await Task.WhenAny(awaited, pump, timeout);
        if (finished == pump && pump.IsFaulted) await pump;      // throws the real cause
        if (finished == timeout) throw Failed($"waited 60s for {what} with no result");
        if (!awaited.IsCompleted)
            throw Failed($"the event stream ended before {what} arrived");
        await awaited;

        Xunit.Sdk.XunitException Failed(string why)
        {
            string seen;
            lock (events) seen = events.Count == 0
                ? "(no events at all)"
                : string.Join("\n", events.Select(e => $"{e.Name}: {e.Data}"));
            return new Xunit.Sdk.XunitException(
                $"{why}.\n\n--- events ---\n{seen}\n\n--- sidecar log ---\n{sidecar.RecentOutput()}");
        }
    }

    /// <summary>Prints where the test is. A hang here has so far only shown up
    /// as silence in the job log; with markers, the last step that actually
    /// completed is visible.</summary>
    private static void Step(string what) =>
        Console.WriteLine($"[api-test {DateTime.UtcNow:HH:mm:ss}] {what}");

    private static async Task<string> WriteScriptAsync(string sql)
    {
        var path = Path.Combine(Path.GetTempPath(), $"sqldbgr-api-{Guid.NewGuid():N}.sql");
        await File.WriteAllTextAsync(path, sql);
        return path;
    }

    [SkippableFact]
    public async Task TheExtensionsWholeFlow_OverHttp()
    {
        RequireSqlServer();
        var program = await WriteScriptAsync(Script);
        Step("starting the sidecar");
        await using var sidecar = await SidecarProcess.StartAsync();
        Step("sidecar up: " + sidecar.Url);

        // sidecarManager.ts probes /health before anything else, and it is the
        // only route that has to answer without a token.
        var health = await sidecar.GetAsync<HealthResponse>("/health");
        Assert.Equal("ok", health.Status);
        Assert.Equal(System.Net.HttpStatusCode.OK, await sidecar.GetUnauthenticatedAsync("/health"));
        // Everything else must require the token.
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized,
            await sidecar.GetUnauthenticatedAsync("/session/" + Guid.NewGuid()));

        Step("health ok");

        // /inspect drives the CodeLens and the offer of module mode.
        var inspected = await sidecar.PostAsync<InspectResponse>("/inspect", new { programPath = program });
        Assert.Empty(inspected.ParseErrors);
        Assert.Null(inspected.Module);   // an ordinary script, not a module

        Step("inspect ok");
        var started = await sidecar.PostAsync<StartResponse>("/session/start", new
        {
            connectionString = Cs,
            programPath = program,
            mode = "invoke"
        });
        var session = started.SessionId;
        Step("session " + session);

        // Breakpoints are sent as a stmtId, not a line number: the sidecar
        // returns spans from the parse and the extension snaps the line to a
        // statement itself (breakpointMapper.ts). Sending a line here just
        // quietly degrades into stmtId 0.
        var onLineTwo = Assert.Single(started.Statements, st => st.Line == 2);
        await sidecar.PostAsync($"/session/{session}/breakpoints", new
        {
            breakpoints = new[]
            {
                new
                {
                    stmtId = onLineTwo.StmtId,
                    condition = (string?)null,
                    hitCondition = (string?)null,
                    logMessage = (string?)null
                }
            }
        });

        Step("breakpoint set");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var events = new List<(string Name, string Data)>();
        var pausedOnce = new TaskCompletionSource();
        var terminated = new TaskCompletionSource();
        var pump = Task.Run(async () =>
        {
            await foreach (var evt in sidecar.ReadEventsAsync(session, cts.Token))
            {
                lock (events) events.Add(evt);
                if (evt.Name == "paused") pausedOnce.TrySetResult();
                if (evt.Name == "terminated") { terminated.TrySetResult(); break; }
            }
        }, cts.Token);

        Step("running");
        await sidecar.PostAsync($"/session/{session}/run", new { stopOnEntry = false });
        Step("run sent");

        await WaitAsync(pausedOnce.Task, pump, "a pause", sidecar, events);
        Step("paused");

        // Paused before the statement: @x is still 1 on line 2.
        var locals = (await sidecar.GetAsync<Local[]>($"/session/{session}/locals"))
            .ToDictionary(l => l.Name, l => l.Value);
        Assert.Equal("1", locals["@x"]);

        var evaluated = await sidecar.PostAsync<EvaluateResponse>(
            $"/session/{session}/evaluate", new { expression = "@x * 10" });
        Assert.Null(evaluated.Error);
        Assert.Equal("10", evaluated.Value);

        // setVariable takes effect when the batch continues.
        await sidecar.PostAsync($"/session/{session}/variables", new { name = "@x", value = "41" });
        await sidecar.PostAsync($"/session/{session}/signal", new { command = "continue" });

        Step("continue sent");
        await WaitAsync(terminated.Task, pump, "terminated", sidecar, events);
        await pump.WaitAsync(TimeSpan.FromSeconds(10));

        Step("terminated");
        cts.Cancel();

        lock (events)
        {
            // PRINT reaches the Debug Console as an output event...
            Assert.Contains(events, e => e.Name == "output" && e.Data.Contains("hello"));
            // ...and SELECT as a result set carrying the new value.
            var resultset = Assert.Single(events, e => e.Name == "resultset").Data;
            using var doc = JsonDocument.Parse(resultset);
            Assert.Equal("X", doc.RootElement.GetProperty("columns")[0].GetString());
            Assert.Equal("42", doc.RootElement.GetProperty("rows")[0][0].GetString());
        }

        File.Delete(program);
    }

    /// <summary>What makes the CodeLens appear, and tells the parameter panel
    /// what to ask for.</summary>
    [SkippableFact]
    public async Task Inspect_DescribesAModuleAndItsParameters()
    {
        RequireSqlServer();
        var program = await WriteScriptAsync("""
            CREATE PROCEDURE dbo.Inspected @a INT, @b NVARCHAR(50) = N'x', @out INT OUTPUT
            AS
            BEGIN
                SET @out = @a;
            END
            """);
        await using var sidecar = await SidecarProcess.StartAsync();

        var inspected = await sidecar.PostAsync<InspectResponse>("/inspect", new { programPath = program });
        Assert.Empty(inspected.ParseErrors);
        Assert.NotNull(inspected.Module);
        Assert.Equal("procedure", inspected.Module!.Kind);
        Assert.Contains("Inspected", inspected.Module.Name);
        Assert.True(inspected.Module.CanScriptify);

        var parameters = inspected.Module.Parameters.ToDictionary(p => p.Name);
        Assert.Equal(3, parameters.Count);
        Assert.Null(parameters["@a"].DefaultValue);
        Assert.NotNull(parameters["@b"].DefaultValue);
        Assert.True(parameters["@out"].IsOutput);

        File.Delete(program);
    }

    [SkippableFact]
    public async Task ParseErrors_AreReportedRatherThanStartingASession()
    {
        RequireSqlServer();
        var program = await WriteScriptAsync("SELECT FROM WHERE;");
        await using var sidecar = await SidecarProcess.StartAsync();

        var inspected = await sidecar.PostAsync<InspectResponse>("/inspect", new { programPath = program });
        Assert.NotEmpty(inspected.ParseErrors);

        // The extension sends parse errors to the Problems panel; the session
        // must never start, or broken T-SQL runs against the server.
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sidecar.PostAsync<StartResponse>("/session/start", new
            {
                connectionString = Cs,
                programPath = program,
                mode = "invoke"
            }));
        Assert.Contains("400", failure.Message);

        File.Delete(program);
    }
}
