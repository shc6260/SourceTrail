namespace SourceTrail.Mcp;

/// <summary>
/// MCP transport settings. HTTP is the default so one resident process can serve every client
/// (Claude Code sessions, Codex, scripted workers) and keep its indexes loaded in memory.
/// Bridge mode is a light stdio front that starts the shared HTTP server on demand and forwards requests to it.
/// </summary>
public sealed class ServerOptions
{
    public const string Http = "Http";
    public const string Stdio = "Stdio";
    public const string Bridge = "Bridge";

    /// <summary>Http (default, shared resident server), Bridge (stdio front for the shared server) or Stdio (one process per client).</summary>
    public string Transport { get; set; } = Http;

    /// <summary>Listen URL for the HTTP transport, and the server a bridge connects to. Only loopback hosts are accepted.</summary>
    public string Url { get; set; } = "http://127.0.0.1:3920";

    /// <summary>Endpoint path for the streamable HTTP MCP endpoint.</summary>
    public string Path { get; set; } = "/mcp";

    /// <summary>
    /// Allows load_solution and select_database_source to change the analysis target.
    /// Null means: allowed for Stdio, blocked for Http because one change would affect every connected client.
    /// </summary>
    public bool? AllowTargetChanges { get; set; }

    /// <summary>HTTP server exits after no bridge has been connected for ShutdownAfterNoBridgesSeconds. Bridges start servers with this on.</summary>
    public bool AutoShutdown { get; set; }

    /// <summary>Grace period with zero connected bridges before an auto-shutdown server exits.</summary>
    public int ShutdownAfterNoBridgesSeconds { get; set; } = 600;

    /// <summary>A bridge without a heartbeat for this long is treated as gone (crashed or killed clients).</summary>
    public int BridgeTimeoutSeconds { get; set; } = 90;

    /// <summary>How often a bridge reports that it is still connected.</summary>
    public int HeartbeatSeconds { get; set; } = 30;

    /// <summary>
    /// Windows scheduled task a bridge runs to start the server outside the client's process tree, so closing one client
    /// does not kill the shared server. Empty, or a missing task, makes the bridge start the server directly.
    /// </summary>
    public string LaunchTask { get; set; } = "SourceTrail MCP";

    /// <summary>Seconds a bridge waits for a starting server to accept connections.</summary>
    public int StartTimeoutSeconds { get; set; } = 30;

    public bool UsesHttp => string.Equals(Transport, Http, StringComparison.OrdinalIgnoreCase);

    public bool UsesBridge => string.Equals(Transport, Bridge, StringComparison.OrdinalIgnoreCase);

    public bool TargetChangesAllowed => AllowTargetChanges ?? !UsesHttp;

    public Uri BaseUri => new(Url.TrimEnd('/') + "/");

    public Uri McpUri => new(BaseUri, Path.TrimStart('/'));

    /// <summary>Validates transport values and keeps the HTTP listener on this machine only.</summary>
    public void Validate()
    {
        if (!UsesHttp && !UsesBridge && !string.Equals(Transport, Stdio, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Server:Transport must be '{Http}', '{Bridge}' or '{Stdio}', not '{Transport}'.");
        if (!UsesHttp && !UsesBridge) return;
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp)
            throw new ArgumentException($"Server:Url must be an absolute http URL, not '{Url}'.");
        if (!uri.IsLoopback)
            throw new ArgumentException($"Server:Url must use a loopback host (127.0.0.1, localhost or [::1]), not '{uri.Host}'.");
        if (string.IsNullOrWhiteSpace(Path) || !Path.StartsWith('/'))
            throw new ArgumentException($"Server:Path must start with '/', not '{Path}'.");
        if (ShutdownAfterNoBridgesSeconds < 1 || BridgeTimeoutSeconds < 1 || HeartbeatSeconds < 1 || StartTimeoutSeconds < 1)
            throw new ArgumentException("Server timing settings must be positive seconds.");
        if (HeartbeatSeconds >= BridgeTimeoutSeconds)
            throw new ArgumentException("Server:HeartbeatSeconds must be shorter than Server:BridgeTimeoutSeconds.");
    }

    /// <summary>Maps the short --stdio / --http / --bridge switches to Server:Transport for client registrations.</summary>
    public static string[] NormalizeArguments(string[] args) =>
        args.Select(arg => arg switch
        {
            "--stdio" => "--Server:Transport=Stdio",
            "--http" => "--Server:Transport=Http",
            "--bridge" => "--Server:Transport=Bridge",
            _ => arg,
        }).ToArray();
}
