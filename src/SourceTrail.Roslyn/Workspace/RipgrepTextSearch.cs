using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace SourceTrail.Roslyn.Workspace;

// rg selects files; the existing reader remains authoritative for decoding, columns and previews.
internal static class RipgrepTextSearch
{
    private static readonly string[] Extensions = [".cs", ".config", ".xml", ".sql"];
    private static readonly string[] Exclusions = ["bin", "obj", ".git", ".svn", ".vs", "node_modules"];

    internal static string? ResolveExecutable(string? configuredPath, string? searchPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return File.Exists(configuredPath) ? Path.GetFullPath(configuredPath) : null;
        // On Windows an extensionless sibling can be a Unix executable. Never prefer it to rg.exe.
        var name = OperatingSystem.IsWindows() ? "rg.exe" : "rg";
        foreach (var directory in (searchPath ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            try
            {
                var candidate = Path.Combine(directory.Trim().Trim('"'), name);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
        }
        return null;
    }

    internal static async Task<HashSet<string>?> FindFilesAsync(string[] roots, string? extension, string text,
        string? configuredPath, Action<string>? log, CancellationToken token)
    {
        // A line-oriented reader cannot match newlines. Replacement characters need its exact decoder.
        if (text.IndexOfAny(['\r', '\n', '\uFFFD']) >= 0) return null;
        var executable = ResolveExecutable(configuredPath);
        if (executable is null)
        {
            log?.Invoke("Text search engine: managed (ripgrep unavailable).");
            return null;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots.Where(Directory.Exists))
            {
                var start = new ProcessStartInfo(executable)
                {
                    WorkingDirectory = Path.GetFullPath(root),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                foreach (var arg in new[] { "--no-config", "--no-ignore", "--hidden", "--text", "--files-with-matches",
                    "--null", "--ignore-case", "--encoding", "none", "--regexp", EscapeLiteral(text),
                    // Select UTF-16/UTF-32 BOM files even when the UTF-8 search cannot interpret them.
                    "--regexp", @"(?-u:\A(?:\xFF\xFE|\xFE\xFF|\x00\x00\xFE\xFF))" })
                    start.ArgumentList.Add(arg);
                foreach (var suffix in extension is null ? Extensions : new[] { extension })
                {
                    start.ArgumentList.Add("--iglob"); start.ArgumentList.Add("*" + suffix);
                }
                foreach (var excluded in Exclusions)
                {
                    start.ArgumentList.Add("--iglob"); start.ArgumentList.Add("!**/" + excluded + "/**");
                }
                start.ArgumentList.Add("--"); start.ArgumentList.Add(".");
                using var process = new Process { StartInfo = start };
                if (!process.Start()) return null;
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                try { await process.WaitForExitAsync(timeout.Token); }
                finally
                {
                    if (!process.HasExited)
                    {
                        try { process.Kill(entireProcessTree: true); }
                        catch (InvalidOperationException) { }
                        catch (Win32Exception) { }
                    }
                    await Task.WhenAll(stdout, stderr);
                }
                if (process.ExitCode is not (0 or 1))
                {
                    log?.Invoke("Text search engine: managed (ripgrep returned an error).");
                    return null;
                }
                foreach (var file in (await stdout).Split('\0', StringSplitOptions.RemoveEmptyEntries))
                    matches.Add(Path.GetFullPath(file, start.WorkingDirectory));
            }
            log?.Invoke($"Text search engine: ripgrep candidates, files {matches.Count}; managed source verification.");
            return matches;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            log?.Invoke("Text search engine: managed (ripgrep timed out).");
            return null;
        }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            log?.Invoke($"Text search engine: managed (ripgrep failed: {error.GetType().Name}).");
            return null;
        }
    }

    private static string EscapeLiteral(string text)
    {
        var pattern = new StringBuilder();
        foreach (var character in text)
        {
            if (@"\.^$|?*+()[]{}".Contains(character)) pattern.Append('\\');
            pattern.Append(character);
        }
        return pattern.ToString();
    }
}
