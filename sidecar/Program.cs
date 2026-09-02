using System.Collections.Concurrent;
using SqlDebugger.Sidecar.Execution;
using SqlDebugger.Sidecar.Parsing;

var port = 5199;
for (var i = 0; i < args.Length - 1; i++)
    if (args[i] == "--port" && int.TryParse(args[i + 1], out var parsed))
        port = parsed;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
var app = builder.Build();

var sessions = new ConcurrentDictionary<Guid, DebugSessionRunner>();

// Extensionen probar denna för att avgöra om sidecaren redan kör.
// Versionen stämplas vid publish (-p:Version=...) så man ser vilket bygge som kör.
var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "tsql-debugger-sidecar", version }));

// Hittar en ev. funktions-/procedurdefinition i filen så extensionen kan
// erbjuda "debugga kroppen" och fråga efter parametervärden.
app.MapPost("/inspect", async (InspectRequest req) =>
{
    var source = await File.ReadAllTextAsync(req.ProgramPath);
    return Results.Ok(new { module = new ScriptDomAnalyzer().InspectModule(source) });
});

// Parsar och instrumenterar men kör INTE - klienten sätter breakpoints först
// och anropar sedan /run (DAP configurationDone).
app.MapPost("/session/start", async (StartSessionRequest req) =>
{
    // Anslut tidigt: ger ett begripligt fel direkt, och databasnamnet behövs för
    // att kvalificera __dbg-anropen så USE i scriptet inte bryter pauserna.
    string database;
    try
    {
        await using var probe = new Microsoft.Data.SqlClient.SqlConnection(req.ConnectionString);
        await probe.OpenAsync();
        database = probe.Database;
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = $"Kunde inte ansluta: {ex.Message}" });
    }
    var debugSchema = $"[{database.Replace("]", "]]")}].__dbg";

    var source = await File.ReadAllTextAsync(req.ProgramPath);
    var analyzer = new ScriptDomAnalyzer();
    var instrumented = req.Mode == "module"
        ? analyzer.InstrumentModuleBody(source, req.ProgramPath, debugSchema)
        : analyzer.Instrument(source, req.ProgramPath, debugSchema);

    if (instrumented.Errors.Count > 0)
        return Results.BadRequest(new { message = "Parse errors", errors = instrumented.Errors });

    var runner = new DebugSessionRunner(req.ConnectionString, instrumented, req.Mode, req.Params);
    sessions[runner.SessionId] = runner;

    return Results.Ok(new
    {
        sessionId = runner.SessionId,
        lineMap = instrumented.LineMap.Select(kv => new { line = kv.Key, stmtId = kv.Value })
    });
});

app.MapPost("/session/{id:guid}/run", (Guid id, RunRequest req) =>
{
    if (!sessions.TryGetValue(id, out var runner)) return Results.NotFound();
    // Batchen körs i bakgrunden - den blockerar i __dbg.Pause tills klienten signalerar
    return runner.TryStart(req.StopOnEntry) ? Results.Ok() : Results.Conflict();
});

app.MapPost("/session/{id:guid}/breakpoints", async (Guid id, BreakpointsRequest req) =>
{
    if (!sessions.TryGetValue(id, out var runner)) return Results.NotFound();
    await runner.SetBreakpointsAsync(req.StmtIds);
    return Results.Ok();
});

app.MapPost("/session/{id:guid}/signal", async (Guid id, SignalRequest req) =>
{
    if (!sessions.TryGetValue(id, out var runner)) return Results.NotFound();
    await runner.SignalAsync(req.Command);
    return Results.Ok();
});

app.MapGet("/session/{id:guid}/locals", async (Guid id) =>
{
    if (!sessions.TryGetValue(id, out var runner)) return Results.NotFound();
    return Results.Ok(await runner.GetLocalsAsync());
});

app.MapGet("/session/{id:guid}/events", async (Guid id, HttpContext ctx) =>
{
    if (!sessions.TryGetValue(id, out var runner)) return Results.NotFound();

    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";

    await foreach (var evt in runner.Events.ReadAllAsync(ctx.RequestAborted))
    {
        await ctx.Response.WriteAsync($"event: {evt.Name}\ndata: {evt.JsonData}\n\n", ctx.RequestAborted);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
    }
    return Results.Empty;
});

app.MapPost("/session/{id:guid}/stop", async (Guid id) =>
{
    if (sessions.TryRemove(id, out var runner))
        await runner.StopAsync();
    return Results.Ok();
});

// Låter en nyare extension byta ut en kvarlämnad äldre sidecar.
app.MapPost("/shutdown", (IHostApplicationLifetime lifetime) =>
{
    lifetime.StopApplication();
    return Results.Ok();
});

app.Run();

public record StartSessionRequest(
    string ProgramPath,
    string ConnectionString,
    string Mode,
    Dictionary<string, object?> Params);

public record InspectRequest(string ProgramPath);
public record RunRequest(bool StopOnEntry);
public record BreakpointsRequest(int[] StmtIds);
public record SignalRequest(string Command);
