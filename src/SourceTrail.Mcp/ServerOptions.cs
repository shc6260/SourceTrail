namespace SourceTrail.Mcp;

/// <summary>
/// MCP transport settings. HTTP is the default so one resident process can serve every client
/// (Claude Code sessions, Codex, scripted workers) and keep its indexes loaded in memory.
/// </summary>
public sealed class ServerOptions
{
    public const string Http = "Http";
    public const string Stdio = "Stdio";

    /// <summary>Http (default, shared resident server) or Stdio (one process per client).</summary>
    public string Transport { get; set; } = Http;

    /// <summary>Listen URL for the HTTP transport. Only loopback hosts are accepted.</summary>
    public string Url { get; set; } = "http://127.0.0.1:3920";

    /// <summary>Endpoint path for the streamable HTTP MCP endpoint.</summary>
    public string Path { get; set; } = "/mcp";

    /// <summary>
    /// Allows load_solution and select_database_source to change the analysis target.
    /// Null means: allowed for Stdio, blocked for Http because one change would affect every connected client.
    /// </summary>
    public bool? AllowTargetChanges { get; set; }

    public bool UsesHttp => string.Equals(Transport, Http, StringComparison.OrdinalIgnoreCase);

    public bool TargetChangesAllowed => AllowTargetChanges ?? !UsesHttp;

    /// <summary>Validates transport values and keeps the HTTP listener on this machine only.</summary>
    public void Validate()
    {
        if (!UsesHttp && !string.Equals(Transport, Stdio, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Server:Transport must be '{Http}' or '{Stdio}', not '{Transport}'.");
        if (!UsesHttp) return;
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp)
            throw new ArgumentException($"Server:Url must be an absolute http URL, not '{Url}'.");
        if (!uri.IsLoopback)
            throw new ArgumentException($"Server:Url must use a loopback host (127.0.0.1, localhost or [::1]), not '{uri.Host}'.");
        if (string.IsNullOrWhiteSpace(Path) || !Path.StartsWith('/'))
            throw new ArgumentException($"Server:Path must start with '/', not '{Path}'.");
    }

    /// <summary>Maps the short --stdio / --http switches to Server:Transport for client registrations.</summary>
    public static string[] NormalizeArguments(string[] args) =>
        args.Select(arg => arg switch
        {
            "--stdio" => "--Server:Transport=Stdio",
            "--http" => "--Server:Transport=Http",
            _ => arg,
        }).ToArray();
}
