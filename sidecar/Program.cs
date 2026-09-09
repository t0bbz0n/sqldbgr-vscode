using System.Collections.Concurrent;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using SqlDebugger.Sidecar.Execution;
using SqlDebugger.Sidecar.Parsing;

var port = 5199;
for (var i = 0; i < args.Length - 1; i++)
    if (args[i] == "--port" && int.TryParse(args[i + 1], out var parsed))
        port = parsed;

// A Windows-1252 fallback for older .sql files that are not UTF-8.
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://127.0.0.1:{port}"); // --port 0 is a random port: one sidecar per VS Code window
// ASP.NET's info logging would otherwise fill the output channel in VS Code.
builder.Logging.SetMinimumLevel(args.Contains("--verbose") ? LogLevel.Information : LogLevel.Warning);
var app = builder.Build();

// Auth: the extension supplies one token per start (SQLDBGR_TOKEN). Without it
// any local process could read files through /inspect and start sessions under
// the user's login. If the variable is missing, which means a sidecar someone
// started themselves, it runs without auth.
var token = Environment.GetEnvironmentVariable("SQLDBGR_TOKEN");
if (!string.IsNullOrEmpty(token))
{
    app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Path == "/health" || ctx.Request.Headers.Authorization == $"Bearer {token}")
        {
            await next();
            return;
        }
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsync("missing or invalid sidecar token");
    });
}

// The extension reads the actual address from stdout when the port is random.
app.Lifetime.ApplicationStarted.Register(() =>
{
    var address = app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
    Console.WriteLine($"SQLDBGR_SIDECAR_URL={address}");
});

var sessions = new ConcurrentDictionary<Guid, DebugSessionRunner>();

// The extension probes this to tell whether a sidecar is already running. The
// version is stamped at publish time (-p:Version=...), so it is always clear
// which build is answering.
var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "sqldbgr-sidecar", version }));

// Finds a function or procedure definition in the file, if there is one, so the
// extension can offer to debug the body and ask for parameter values.
app.MapPost("/inspect", async (InspectRequest req) =>
{
    var source = await ReadSourceAsync(req.ProgramPath);
    var analyzer = new ScriptDomAnalyzer();
    return Results.Ok(new
    {
        module = analyzer.InspectModule(source),
        parseErrors = analyzer.GetParseErrors(source),
        // Spans, so the client can map breakpoints inside multi-line statements.
        statements = analyzer.Instrument(source, req.ProgramPath).StmtToSpan
            .Select(kv => new { stmtId = kv.Key, kv.Value.Line, kv.Value.EndLine })
    });
});

// Parses and instruments but does NOT run: the client sets breakpoints first and
// then calls /run (DAP configurationDone).
app.MapPost("/session/start", async (StartSessionRequest req) =>
{
    // Connect early: it gives a comprehensible error straight away, and the
    // database name is needed to qualify the __dbg calls, so a USE in the script
    // cannot break the pauses.
    string database;
    try
    {
        await using var probe = new Microsoft.Data.SqlClient.SqlConnection(req.ConnectionString);
        await probe.OpenAsync();
        database = probe.Database;
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = $"Could not connect: {ex.Message}" });
    }
    // The __dbg schema can live in another database, for environments without DDL rights in the target.
    var debugDatabase = string.IsNullOrWhiteSpace(req.DebugDatabase) ? database : req.DebugDatabase;
    var debugSchema = $"[{debugDatabase.Replace("]", "]]")}].__dbg";

    var source = await ReadSourceAsync(req.ProgramPath);
    var analyzer = new ScriptDomAnalyzer();
    var instrumented = req.Mode == "module"
        ? analyzer.InstrumentModuleBody(source, req.ProgramPath, debugSchema)
        : analyzer.Instrument(source, req.ProgramPath, debugSchema);

    if (instrumented.Errors.Count > 0)
        return Results.BadRequest(new { message = "Parse errors", errors = instrumented.Errors });

    var options = new DebugSessionOptions(req.Mode, req.Transaction ?? "none", debugDatabase);
    // Params and Breakpoints are optional in the JSON. Without the ?? [] they
    // arrive as null and the run dies on a bare NullReferenceException, far
    // from here.
    var runner = new DebugSessionRunner(req.ConnectionString, instrumented, options, req.Params ?? []);
    sessions[runner.SessionId] = runner;

    return Results.Ok(new
    {
        sessionId = runner.SessionId,
        lineMap = instrumented.LineMap.Select(kv => new { line = kv.Key, stmtId = kv.Value }),
        statements = instrumented.StmtToSpan.Select(kv => new { stmtId = kv.Key, kv.Value.Line, kv.Value.EndLine })
    });
});

app.MapPost("/session/{id:guid}/run", (Guid id, RunRequest req) =>
{
    if (!sessions.TryGetValue(id, out var runner)) return Results.NotFound();
    // The batch runs in the background; it blocks in __dbg.Pause until the client signals.
    return runner.TryStart(req.StopOnEntry) ? Results.Ok() : Results.Conflict();
});

app.MapPost("/session/{id:guid}/breakpoints", async (Guid id, BreakpointsRequest req) =>
{
    if (!sessions.TryGetValue(id, out var runner)) return Results.NotFound();
    await runner.SetBreakpointsAsync(req.Breakpoints ?? []);
    return Results.Ok();
});

app.MapPost("/session/{id:guid}/signal", async (Guid id, SignalRequest req) =>
{
    if (!sessions.TryGetValue(id, out var runner)) return Results.NotFound();
    await runner.SignalAsync(req.Command);
    return Results.Ok();
});

app.MapPost("/session/{id:guid}/variables", async (Guid id, SetVariableRequest req) =>
{
    if (!sessions.TryGetValue(id, out var runner)) return Results.NotFound();
    await runner.SetVariableAsync(req.Name, req.Value);
    return Results.Ok();
});

// Hover and Watch: expressions are evaluated against the captured locals on a connection of their own.
app.MapPost("/session/{id:guid}/evaluate", async (Guid id, EvaluateRequest req) =>
{
    if (!sessions.TryGetValue(id, out var runner)) return Results.NotFound();
    var (value, error) = await runner.EvaluateAsync(req.Expression);
    return Results.Ok(new { value, error });
});

// Describes an existing session. Attach mode connects to a session that has
// already been caught, and needs its line map without starting anything.
app.MapGet("/session/{id:guid}", (Guid id) =>
{
    if (!sessions.TryGetValue(id, out var runner)) return Results.NotFound();
    return Results.Ok(new
    {
        sessionId = id,
        program = runner.Script.SourcePath,
        lineMap = runner.Script.LineMap.Select(kv => new { line = kv.Key, stmtId = kv.Value }),
        statements = runner.Script.StmtToSpan.Select(kv => new { stmtId = kv.Key, kv.Value.Line, kv.Value.EndLine })
    });
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

// Lets a newer extension replace an older sidecar left behind.
app.MapPost("/shutdown", (IHostApplicationLifetime lifetime) =>
{
    lifetime.StopApplication();
    return Results.Ok();
});

app.Run();

/// <summary>A byte order mark decides if there is one. Otherwise strict UTF-8,
/// falling back to Windows-1252, which is common in older .sql files and would
/// otherwise turn non-ASCII letters into mojibake.</summary>
static async Task<string> ReadSourceAsync(string path)
{
    var bytes = await File.ReadAllBytesAsync(path);
    if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
    if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
    if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
    try
    {
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
    }
    catch (DecoderFallbackException)
    {
        return Encoding.GetEncoding(1252).GetString(bytes);
    }
}

public record StartSessionRequest(
    string ProgramPath,
    string ConnectionString,
    string Mode,
    Dictionary<string, object?>? Params = null,
    string? Transaction = null,
    string? DebugDatabase = null);

public record InspectRequest(string ProgramPath);
public record RunRequest(bool StopOnEntry);
public record BreakpointsRequest(BreakpointSpec[]? Breakpoints);
public record SignalRequest(string Command);
public record SetVariableRequest(string Name, string? Value);
public record EvaluateRequest(string Expression);
