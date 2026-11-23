using ModelContextProtocol;

namespace SourceTrail.Mcp.Tools;

/// <summary>Blocks per-call target changes on the shared HTTP server, where they would affect every client.</summary>
public static class TargetChangeGuard
{
    public static void EnsureAllowed(ServerOptions server, string tool, string settingKey)
    {
        if (server.TargetChangesAllowed) return;
        throw new McpException(
            $"{tool} is disabled on the shared HTTP server because it would change the analysis target for every connected client. " +
            $"Set {settingKey} in the server configuration and restart the server, or set Server:AllowTargetChanges=true.");
    }
}
