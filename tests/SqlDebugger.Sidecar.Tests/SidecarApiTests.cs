using System.Text.Json;
using Xunit;

namespace SqlDebugger.Sidecar.Tests;

/// <summary>
/// Driver sidecaren över HTTP precis som extensionen: inspektera filen, starta
/// en session, sätta breakpoints, öppna SSE-strömmen, köra, läsa locals,
/// evaluera, ändra en variabel och stega vidare. Övriga integrationstester
/// anropar DebugSessionRunner direkt, så ingenting av det här - routing,
/// JSON-former, SSE-formatet, auth - täcks av dem.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class SidecarApiTests(SqlServerFixture fixture)
{
    private string Cs => fixture.ConnectionString ?? throw new InvalidOperationException();
    private void RequireSqlServer() => Skip.If(fixture.ConnectionString is null, "SQLDBGR_TEST_CONNECTION är inte satt");

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

    /// <summary>Väntar på ett event, men låter pumpens undantag vinna. Faller
    /// pumpen tyst blir symptomet annars bara "timed out", vilket inte säger
    /// något om varför.</summary>
    private static async Task WaitAsync(Task awaited, Task pump, string what, SidecarProcess sidecar)
    {
        var timeout = Task.Delay(TimeSpan.FromSeconds(60));
        var finished = await Task.WhenAny(awaited, pump, timeout);
        if (finished == pump && pump.IsFaulted) await pump;      // kastar den riktiga orsaken
        if (finished == timeout)
            throw new Xunit.Sdk.XunitException(
                $"väntade på {what} i 60s utan resultat.\n\n--- sidecar-logg ---\n{sidecar.RecentOutput()}");
        await awaited;
    }

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
        await using var sidecar = await SidecarProcess.StartAsync();

        // sidecarManager.ts probar /health innan allt annat, och det är den enda
        // rutt som måste svara utan token.
        var health = await sidecar.GetAsync<HealthResponse>("/health");
        Assert.Equal("ok", health.Status);
        Assert.Equal(System.Net.HttpStatusCode.OK, await sidecar.GetUnauthenticatedAsync("/health"));
        // Allt annat måste kräva token.
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized,
            await sidecar.GetUnauthenticatedAsync("/session/" + Guid.NewGuid()));

        // /inspect driver CodeLens och erbjudandet om modulläge.
        var inspected = await sidecar.PostAsync<InspectResponse>("/inspect", new { programPath = program });
        Assert.Empty(inspected.ParseErrors);
        Assert.Null(inspected.Module);   // ett vanligt skript, ingen modul

        var started = await sidecar.PostAsync<StartResponse>("/session/start", new
        {
            connectionString = Cs,
            programPath = program,
            mode = "invoke"
        });
        var session = started.SessionId;

        // Breakpoints skickas som stmtId, inte radnummer: sidecaren returnerar
        // spans från parsen och extensionen snappar raden till ett statement
        // själv (breakpointMapper.ts). Att skicka en rad hit tystar bara ner
        // sig till stmtId 0.
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

        await sidecar.PostAsync($"/session/{session}/run", new { stopOnEntry = false });

        await WaitAsync(pausedOnce.Task, pump, "en paus", sidecar);

        // Paus före satsen: @x är fortfarande 1 på rad 2.
        var locals = (await sidecar.GetAsync<Local[]>($"/session/{session}/locals"))
            .ToDictionary(l => l.Name, l => l.Value);
        Assert.Equal("1", locals["@x"]);

        var evaluated = await sidecar.PostAsync<EvaluateResponse>(
            $"/session/{session}/evaluate", new { expression = "@x * 10" });
        Assert.Null(evaluated.Error);
        Assert.Equal("10", evaluated.Value);

        // setVariable slår igenom när batchen fortsätter.
        await sidecar.PostAsync($"/session/{session}/variables", new { name = "@x", value = "41" });
        await sidecar.PostAsync($"/session/{session}/signal", new { command = "continue" });

        await WaitAsync(terminated.Task, pump, "terminated", sidecar);
        await pump.WaitAsync(TimeSpan.FromSeconds(10));

        lock (events)
        {
            // PRINT når Debug Console som ett output-event...
            Assert.Contains(events, e => e.Name == "output" && e.Data.Contains("hello"));
            // ...och SELECT som en resultatmängd med det nya värdet.
            var resultset = Assert.Single(events, e => e.Name == "resultset").Data;
            using var doc = JsonDocument.Parse(resultset);
            Assert.Equal("X", doc.RootElement.GetProperty("columns")[0].GetString());
            Assert.Equal("42", doc.RootElement.GetProperty("rows")[0][0].GetString());
        }

        File.Delete(program);
    }

    /// <summary>Det som får CodeLens att dyka upp och parameterpanelen att veta
    /// vad den ska fråga efter.</summary>
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

        // Extensionen skickar parse-fel till Problems-panelen; sessionen får
        // aldrig starta, annars körs trasig T-SQL mot servern.
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
