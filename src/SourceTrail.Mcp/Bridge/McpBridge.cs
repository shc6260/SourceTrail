using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SourceTrail.Mcp.Bridge;

/// <summary>
/// Light stdio front for the shared HTTP server. Clients launch it as an ordinary stdio MCP server; it starts the
/// shared server when needed, relays JSON-RPC lines, reports a heartbeat while connected and says goodbye on exit.
/// </summary>
public sealed class McpBridge(ServerOptions server, string serverDll, TextWriter log)
{
    private readonly string _id = Guid.NewGuid().ToString("N");
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim _stdout = new(1, 1);
    private readonly SemaphoreSlim _starting = new(1, 1);

    public async Task<int> RunAsync(Stream input, Stream output, CancellationToken cancellationToken)
    {
        var writer = new StreamWriter(output, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        await TryEnsureServerAsync(cancellationToken);
        using var heartbeat = new CancellationTokenSource();
        var beating = HeartbeatAsync(heartbeat.Token);
        var pending = new List<Task>();
        using (var reader = new StreamReader(input, new UTF8Encoding(false)))
        {
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                pending.RemoveAll(task => task.IsCompleted);
                pending.Add(RelayAsync(line, writer, cancellationToken));
            }
        }
        await Task.WhenAll(pending);
        heartbeat.Cancel();
        try { await beating; } catch (OperationCanceledException) { }
        await PostQuietlyAsync("bridge/leave", CancellationToken.None);
        return 0;
    }

    private async Task RelayAsync(string line, StreamWriter writer, CancellationToken token)
    {
        JsonNode? id = null;
        try { id = JsonNode.Parse(line)?["id"]?.DeepClone(); } catch (JsonException) { }
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, server.McpUri)
                { Content = new StringContent(line, Encoding.UTF8, "application/json") };
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                var body = await response.Content.ReadAsStringAsync(token);
                foreach (var message in Messages(response.Content.Headers.ContentType?.MediaType, body))
                    await WriteAsync(writer, message);
                if (!response.IsSuccessStatusCode && id is not null && string.IsNullOrWhiteSpace(body))
                    await WriteAsync(writer, Error(id, $"SourceTrail server returned HTTP {(int)response.StatusCode}."));
                return;
            }
            catch (HttpRequestException error) when (attempt == 0)
            {
                log.WriteLine($"SourceTrail bridge: server unreachable ({error.Message}); starting it and retrying once.");
                await TryEnsureServerAsync(token);
            }
            catch (HttpRequestException error)
            {
                if (id is not null) await WriteAsync(writer, Error(id, $"SourceTrail server unavailable: {error.Message}"));
                return;
            }
        }
    }

    // Streamable HTTP answers either one JSON body or SSE events whose data lines carry the JSON-RPC messages.
    internal static IEnumerable<string> Messages(string? mediaType, string body)
    {
        if (string.IsNullOrWhiteSpace(body)) yield break;
        if (!string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            yield return body.Trim();
            yield break;
        }
        var data = new StringBuilder();
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                if (data.Length > 0) { yield return data.ToString(); data.Clear(); }
                continue;
            }
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            if (data.Length > 0) data.Append('\n');
            data.Append(line.AsSpan(5).TrimStart(' '));
        }
        if (data.Length > 0) yield return data.ToString();
    }

    internal static string Error(JsonNode id, string message) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["error"] = new JsonObject { ["code"] = -32000, ["message"] = message } }
            .ToJsonString();

    private async Task WriteAsync(StreamWriter writer, string message)
    {
        await _stdout.WaitAsync();
        try { await writer.WriteLineAsync(message.Replace("\r", "").Replace("\n", " ")); }
        finally { _stdout.Release(); }
    }

    private async Task HeartbeatAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (!await PostQuietlyAsync("bridge/heartbeat", token)) await TryEnsureServerAsync(token);
            await Task.Delay(TimeSpan.FromSeconds(server.HeartbeatSeconds), token);
        }
    }

    private async Task<bool> PostQuietlyAsync(string path, CancellationToken token)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await _http.PostAsJsonAsync(new Uri(server.BaseUri, path), new BridgeBeat(_id), timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException) { return false; }
    }

    // A start failure is logged and retried on the next request or heartbeat; it never ends the client's session.
    private async Task TryEnsureServerAsync(CancellationToken token)
    {
        try { await EnsureServerAsync(token); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log.WriteLine($"SourceTrail bridge: {error.Message}");
        }
    }

    private async Task EnsureServerAsync(CancellationToken token)
    {
        if (await IsListeningAsync(token)) return;
        await _starting.WaitAsync(token);
        FileStream? startLock = null;
        try
        {
            // One machine-wide start at a time, so two clients opening together still get a single server.
            // A file lock works across processes and, unlike a mutex, is not tied to the thread that took it.
            startLock = await AcquireStartLockAsync(token);
            if (await IsListeningAsync(token)) return;
            if (!StartViaScheduledTask()) StartDirectly();
            var deadline = DateTime.UtcNow.AddSeconds(server.StartTimeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                if (await IsListeningAsync(token)) return;
                await Task.Delay(200, token);
            }
            throw new TimeoutException($"SourceTrail server did not start listening on {server.BaseUri} within {server.StartTimeoutSeconds}s.");
        }
        finally
        {
            startLock?.Dispose();
            _starting.Release();
        }
    }

    private async Task<FileStream> AcquireStartLockAsync(CancellationToken token)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sourcetrail-start-{server.BaseUri.Port}.lock");
        var deadline = DateTime.UtcNow.AddSeconds(server.StartTimeoutSeconds);
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
            catch (IOException) when (DateTime.UtcNow < deadline) { await Task.Delay(150, token); }
        }
    }

    // Windowless launcher published next to the server DLL; null for older publish folders without it.
    private string? LauncherPath
    {
        get
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(serverDll) ?? "", LaunchTask.LauncherFileName);
            return OperatingSystem.IsWindows() && File.Exists(path) ? path : null;
        }
    }

    // Task Scheduler starts the server outside this client's process tree and job, so closing the client keeps it alive.
    private bool StartViaScheduledTask()
    {
        if (string.IsNullOrWhiteSpace(server.LaunchTask) || !OperatingSystem.IsWindows()) return false;
        try
        {
            EnsureLaunchTask();
            var started = RunSchtasks(out _, "/Run", "/TN", server.LaunchTask) == 0;
            log.WriteLine(started ? $"SourceTrail bridge: started server via scheduled task '{server.LaunchTask}'."
                : $"SourceTrail bridge: scheduled task '{server.LaunchTask}' unavailable; starting server directly.");
            return started;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    // Registers the launch task on first use, and rewrites a bridge-managed task whose launcher moved (new publish folder).
    // A task registered by hand is left as it is. A failure here only means the run below fails and the bridge starts directly.
    private void EnsureLaunchTask()
    {
        if (LauncherPath is not { } launcher) return;
        if (RunSchtasks(out var current, "/Query", "/TN", server.LaunchTask, "/XML") == 0)
        {
            if (!LaunchTask.IsManaged(current)) return;
            if (string.Equals(LaunchTask.CommandOf(current), launcher, StringComparison.OrdinalIgnoreCase)) return;
        }
        var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sourcetrail-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(file, LaunchTask.Build(launcher, $"{Environment.UserDomainName}\\{Environment.UserName}"), Encoding.Unicode);
            var created = RunSchtasks(out var output, "/Create", "/TN", server.LaunchTask, "/XML", file, "/F") == 0;
            log.WriteLine(created ? $"SourceTrail bridge: registered scheduled task '{server.LaunchTask}' for {launcher}."
                : $"SourceTrail bridge: could not register scheduled task '{server.LaunchTask}': {output.Trim()}");
        }
        finally { File.Delete(file); }
    }

    private static int RunSchtasks(out string output, params string[] arguments)
    {
        var start = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start);
        if (process is null) { output = ""; return -1; }
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10_000)) { output = ""; return -1; }
        output = stdout.Result + stderr.Result;
        return process.ExitCode;
    }

    private void StartDirectly()
    {
        // The launcher is a GUI-subsystem program, so neither it nor the server it starts opens a console window.
        var launcher = LauncherPath;
        var host = launcher ?? Environment.ProcessPath ?? "dotnet";
        var arguments = $"{LaunchTask.ServerArguments} --Server:Url={server.Url} --Server:Path={server.Path}";
        if (launcher is null) arguments = $"\"{serverDll}\" {arguments}";
        // Shell execution does not inherit this bridge's stdio handles, so the client's pipes close when the bridge exits.
        Process.Start(new ProcessStartInfo(host, arguments) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = System.IO.Path.GetDirectoryName(serverDll) })?.Dispose();
        log.WriteLine("SourceTrail bridge: started server directly.");
    }

    private async Task<bool> IsListeningAsync(CancellationToken token)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(500));
            await client.ConnectAsync(server.BaseUri.Host, server.BaseUri.Port, timeout.Token);
            return true;
        }
        catch (SocketException) { return false; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
    }
}
