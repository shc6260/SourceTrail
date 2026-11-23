using System.Diagnostics;

// Starts the SourceTrail MCP server next to this file without a console window and exits.
// The scheduled task and bridges run this instead of dotnet, so no terminal appears whatever the default terminal is,
// and the server inherits no handles from its caller. Arguments are passed through (e.g. --http --Server:AutoShutdown=true).
var directory = AppContext.BaseDirectory;
var host = Path.Combine(directory, OperatingSystem.IsWindows() ? "SourceTrail.Mcp.exe" : "SourceTrail.Mcp");
var start = File.Exists(host)
    ? new ProcessStartInfo(host)
    : new ProcessStartInfo("dotnet") { ArgumentList = { Path.Combine(directory, "SourceTrail.Mcp.dll") } };
foreach (var arg in args) start.ArgumentList.Add(arg);
start.UseShellExecute = false;
start.CreateNoWindow = true;
start.WorkingDirectory = directory;
using var process = Process.Start(start);
return process is null ? 1 : 0;
