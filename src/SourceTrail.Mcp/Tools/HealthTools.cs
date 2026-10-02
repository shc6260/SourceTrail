using System.ComponentModel;
using ModelContextProtocol.Server;

namespace SourceTrail.Mcp.Tools;

[McpServerToolType]
public static class HealthTools
{
    [McpServerTool(Name = "ping", ReadOnly = true, Destructive = false)]
    [Description("Checks that the SourceTrail MCP process responds. Does not load source code or connect to a database.")]
    public static string Ping() => "pong";
}
