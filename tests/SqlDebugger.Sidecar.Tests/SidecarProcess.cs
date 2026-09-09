using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace SqlDebugger.Sidecar.Tests;

/// <summary>
/// Runs the real sidecar the way the extension runs it - `--port 0`, the URL
/// read from stdout, the token in the environment - and talks HTTP to it.
///
/// The other tests drive DebugSessionRunner directly, which leaves the entire
/// surface the extension actually uses untested: routing, JSON shapes, the SSE
/// stream and the auth middleware. A mistake in any of those compiles, goes
/// green, and fails first in VS Code.
/// </summary>
public sealed class SidecarProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly HttpClient _client;
    private readonly HttpClient _stream;
    private readonly List<string> _output;

    public string Url { get; }
    public string Token { get; }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private SidecarProcess(Process process, string url, string token, List<string> output)
    {
        _process = process;
        _output = output;
        Url = url;
        Token = token;
        // A short timeout: an ordinary call should fail fast and name itself
        // rather than stalling the whole job.
        _client = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(30) };
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        // The event stream is long-lived on purpose, and has a token of its own.
        _stream = new HttpClient { BaseAddress = new Uri(url), Timeout = Timeout.InfiniteTimeSpan };
        _stream.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public static async Task<SidecarProcess> StartAsync()
    {
        var token = Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepositoryRoot()
        };
        foreach (var arg in new[] { SidecarDll(), "--port", "0" })
            start.ArgumentList.Add(arg);
        start.Environment["SQLDBGR_TOKEN"] = token;
        // Otherwise an unhandled exception is a 500 with an empty body, and the
        // test only says that something went wrong.
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";

        var process = Process.Start(start) ?? throw new InvalidOperationException("could not start the sidecar");

        var url = new TaskCompletionSource<string>();
        var output = new List<string>();
        void Capture(string? line)
        {
            if (line is null) return;
            lock (output) output.Add(line);
            var match = System.Text.RegularExpressions.Regex.Match(line, @"SQLDBGR_SIDECAR_URL=(\S+)");
            if (match.Success) url.TrySetResult(match.Groups[1].Value.TrimEnd('/'));
        }
        process.OutputDataReceived += (_, e) => Capture(e.Data);
        process.ErrorDataReceived += (_, e) => Capture(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // No build time here, the DLL is already built, so the wait can be
        // short. A process that dies at startup should be reported straight away
        // rather than passed over in silence until the timeout expires.
        var timeout = Task.Delay(TimeSpan.FromSeconds(30));
        var exited = process.WaitForExitAsync();
        var finished = await Task.WhenAny(url.Task, exited, timeout);
        if (finished != url.Task)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            var why = finished == exited
                ? $"the sidecar exited with code {process.ExitCode} before writing its URL"
                : "the sidecar never wrote SQLDBGR_SIDECAR_URL within 30s";
            lock (output) throw new InvalidOperationException($"{why}:\n" + string.Join("\n", output));
        }

        return new SidecarProcess(process, await url.Task, token, output);
    }

    public async Task<T> PostAsync<T>(string path, object body)
    {
        var response = await _client.PostAsJsonAsync(path, body, Json);
        await ThrowIfFailedAsync(response, "POST " + path);
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    public async Task PostAsync(string path, object body)
    {
        var response = await _client.PostAsJsonAsync(path, body, Json);
        await ThrowIfFailedAsync(response, "POST " + path);
    }

    public async Task<T> GetAsync<T>(string path)
    {
        var response = await _client.GetAsync(path);
        await ThrowIfFailedAsync(response, "GET " + path);
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    /// <summary>The sidecar's most recent output, for error messages.</summary>
    public string RecentOutput()
    {
        lock (_output) return string.Join("\n", _output.TakeLast(40));
    }

    /// <summary>Without a bearer token, so the auth middleware can be tested.</summary>
    public async Task<HttpStatusCode> GetUnauthenticatedAsync(string path)
    {
        using var bare = new HttpClient { BaseAddress = new Uri(Url) };
        return (await bare.GetAsync(path)).StatusCode;
    }

    /// <summary>Reads the SSE stream and splits it on a blank line, exactly as
    /// sidecarClient.ts does. Returns (name, data) per event.</summary>
    public async IAsyncEnumerable<(string Name, string Data)> ReadEventsAsync(
        Guid sessionId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/session/{sessionId}/events");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var response = await _stream.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var name = "message";
        var data = "";
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) yield break;
            if (line.Length == 0)
            {
                if (data.Length > 0) yield return (name, data);
                name = "message";
                data = "";
            }
            else if (line.StartsWith("event:")) name = line[6..].Trim();
            else if (line.StartsWith("data:")) data += line[5..].Trim();
        }
    }

    private async Task ThrowIfFailedAsync(HttpResponseMessage response, string what)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync();
        // The sidecar's own log is where the reason actually is; without it a
        // 500 with an empty body says nothing at all.
        string tail;
        lock (_output) tail = string.Join("\n", _output.TakeLast(40));
        throw new InvalidOperationException(
            $"{what} -> {(int)response.StatusCode} {response.ReasonPhrase}: {body}\n\n--- sidecar log ---\n{tail}");
    }

    /// <summary>The already-built sidecar. The test project references the
    /// sidecar project only so that it gets built.</summary>
    private static string SidecarDll()
    {
        var root = RepositoryRoot();
        var matches = Directory.GetFiles(
            Path.Combine(root, "sidecar", "bin"), "SqlDebugger.Sidecar.dll", SearchOption.AllDirectories);
        if (matches.Length == 0)
            throw new InvalidOperationException(
                $"found no built sidecar under {Path.Combine(root, "sidecar", "bin")}");
        return matches.OrderByDescending(File.GetLastWriteTimeUtc).First();
    }

    private static string RepositoryRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "sidecar", "SqlDebugger.Sidecar.csproj")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("could not find the repository root");
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        _stream.Dispose();
        try { _process.Kill(entireProcessTree: true); } catch { /* already gone */ }
        try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); } catch { /* strunt i det */ }
        _process.Dispose();
    }
}
