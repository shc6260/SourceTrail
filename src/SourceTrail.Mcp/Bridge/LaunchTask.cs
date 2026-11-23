using System.Xml.Linq;

namespace SourceTrail.Mcp.Bridge;

/// <summary>
/// Task Scheduler definition the bridge registers on first use, so the shared server starts outside the client's
/// process tree and job without any manual setup. Tasks carrying <see cref="Marker"/> are managed by the bridge and are
/// rewritten when the publish folder changes; tasks registered by hand are run as they are and never rewritten.
/// </summary>
internal static class LaunchTask
{
    public const string LauncherFileName = "SourceTrail.Launcher.exe";
    public const string ServerArguments = "--http --Server:AutoShutdown=true";
    public const string Marker = "Managed by the SourceTrail MCP bridge";

    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>Builds the task XML: no trigger (run on demand), current user's interactive token, no time limit, normal priority.</summary>
    public static string Build(string launcherPath, string userId)
    {
        var task = new XElement(Ns + "Task", new XAttribute("version", "1.2"),
            new XElement(Ns + "RegistrationInfo",
                new XElement(Ns + "Description", $"Starts the shared SourceTrail MCP server without a window. {Marker}.")),
            new XElement(Ns + "Principals",
                new XElement(Ns + "Principal", new XAttribute("id", "Author"),
                    new XElement(Ns + "UserId", userId),
                    new XElement(Ns + "LogonType", "InteractiveToken"),
                    new XElement(Ns + "RunLevel", "LeastPrivilege"))),
            new XElement(Ns + "Settings",
                new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(Ns + "DisallowStartIfOnBatteries", "false"),
                new XElement(Ns + "StopIfGoingOnBatteries", "false"),
                new XElement(Ns + "AllowStartOnDemand", "true"),
                new XElement(Ns + "Enabled", "true"),
                new XElement(Ns + "ExecutionTimeLimit", "PT0S"),
                new XElement(Ns + "Priority", "5")),
            new XElement(Ns + "Actions", new XAttribute("Context", "Author"),
                new XElement(Ns + "Exec",
                    new XElement(Ns + "Command", launcherPath),
                    new XElement(Ns + "Arguments", ServerArguments),
                    new XElement(Ns + "WorkingDirectory", Path.GetDirectoryName(launcherPath) ?? ""))));
        // schtasks /XML expects a UTF-16 file; the caller writes this text with Encoding.Unicode.
        return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>" + Environment.NewLine + task;
    }

    public static bool IsManaged(string taskXml) => taskXml.Contains(Marker, StringComparison.Ordinal);

    /// <summary>Returns the first Exec command of a task definition, or null when the XML has none or cannot be read.</summary>
    public static string? CommandOf(string taskXml)
    {
        try { return XDocument.Parse(taskXml).Descendants(Ns + "Command").FirstOrDefault()?.Value.Trim().Trim('"'); }
        catch (System.Xml.XmlException) { return null; }
    }
}
