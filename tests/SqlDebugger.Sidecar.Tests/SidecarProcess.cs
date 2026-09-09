using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace SqlDebugger.Sidecar.Tests;

/// <summary>
/// Kör den riktiga sidecaren som extensionen kör den - `--port 0`, URL:en läst
/// från stdout, token i miljön - och pratar HTTP med den.
///
/// Övriga tester driver DebugSessionRunner direkt, vilket lämnar hela ytan
/// extensionen faktiskt använder otestad: routing, JSON-former, SSE-strömmen
/// och auth-middlewaren. Ett fel där kompilerar, går grönt och havererar först
/// i VS Code.
/// </summary>
public sealed class SidecarProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly HttpClient _client;
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
        _client = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromMinutes(2) };
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
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
        // Annars blir ett ohanterat undantag en 500 med tom body, och testet
        // säger bara att något gick fel.
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";

        var process = Process.Start(start) ?? throw new InvalidOperationException("kunde inte starta sidecaren");

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

        // Ingen byggtid här - DLL:en är redan byggd - så väntan får vara kort.
        var timeout = Task.Delay(TimeSpan.FromSeconds(60));
        if (await Task.WhenAny(url.Task, timeout) == timeout)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* redan borta */ }
            lock (output)
                throw new InvalidOperationException(
                    "sidecaren skrev aldrig SQLDBGR_SIDECAR_URL:\n" + string.Join("\n", output));
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

    /// <summary>Sidecarens senaste utskrifter, för felmeddelanden.</summary>
    public string RecentOutput()
    {
        lock (_output) return string.Join("\n", _output.TakeLast(40));
    }

    /// <summary>Utan bearer-token, så auth-middlewaren går att testa.</summary>
    public async Task<HttpStatusCode> GetUnauthenticatedAsync(string path)
    {
        using var bare = new HttpClient { BaseAddress = new Uri(Url) };
        return (await bare.GetAsync(path)).StatusCode;
    }

    /// <summary>Läser SSE-strömmen och delar upp den på tomrad, precis som
    /// sidecarClient.ts gör. Returnerar (namn, data) per händelse.</summary>
    public async IAsyncEnumerable<(string Name, string Data)> ReadEventsAsync(
        Guid sessionId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/session/{sessionId}/events");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
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
        // Sidecarens egen logg är där orsaken faktiskt står; utan den säger en
        // 500 med tom body ingenting alls.
        string tail;
        lock (_output) tail = string.Join("\n", _output.TakeLast(40));
        throw new InvalidOperationException(
            $"{what} -> {(int)response.StatusCode} {response.ReasonPhrase}: {body}\n\n--- sidecar-logg ---\n{tail}");
    }

    /// <summary>Den färdigbyggda sidecaren. Testprojektet refererar
    /// sidecar-projektet enbart för att få den byggd.</summary>
    private static string SidecarDll()
    {
        var root = RepositoryRoot();
        var matches = Directory.GetFiles(
            Path.Combine(root, "sidecar", "bin"), "SqlDebugger.Sidecar.dll", SearchOption.AllDirectories);
        if (matches.Length == 0)
            throw new InvalidOperationException(
                $"hittade ingen byggd sidecar under {Path.Combine(root, "sidecar", "bin")}");
        return matches.OrderByDescending(File.GetLastWriteTimeUtc).First();
    }

    private static string RepositoryRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "sidecar", "SqlDebugger.Sidecar.csproj")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("hittade inte repo-roten");
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        try { _process.Kill(entireProcessTree: true); } catch { /* redan borta */ }
        try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); } catch { /* strunt i det */ }
        _process.Dispose();
    }
}
