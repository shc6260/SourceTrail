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

    internal readonly record struct LineHit(int Line, int Column, string Preview);

    /// <summary>
    /// Lines ripgrep matched, by full path, plus files whose lines the managed reader must still decode
    /// (UTF-32 BOM files, which ripgrep cannot decode, and files with lone CR line breaks, which ripgrep does not split).
    /// </summary>
    internal sealed record LineSearchResult(Dictionary<string, List<LineHit>> Hits, HashSet<string> Managed);

    /// <summary>
    /// Case-insensitive line matching is delegated to ripgrep only when its Unicode folding cannot differ from
    /// OrdinalIgnoreCase: no CR/LF/replacement character and no cased non-ASCII or surrogate characters.
    /// </summary>
    internal static bool SupportsLineResults(string text) =>
        text.IndexOfAny(['\r', '\n', '\uFFFD']) < 0 &&
        text.All(character => character < 0x80 || (!char.IsSurrogate(character) &&
            char.ToUpperInvariant(character) == character && char.ToLowerInvariant(character) == character));

    // Uses the lines ripgrep reports instead of reading the candidate files again. ripgrep decodes UTF-16 BOM files
    // itself and reports other files as UTF-8 text, the decoding the managed reader applies to them. One ripgrep pass
    // also reports the files it cannot decode like the managed reader: a first line starting with NUL (UTF-32 files,
    // which ripgrep reads as UTF-16 or raw bytes) and lines holding a CR that is not part of CRLF.
    internal static async Task<LineSearchResult?> FindLinesAsync(string[] roots, string? extension, string text,
        string? configuredPath, Action<string>? log, CancellationToken token)
    {
        if (!SupportsLineResults(text)) return null;
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
            var result = new LineSearchResult(new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase));
            foreach (var root in roots.Where(Directory.Exists))
            {
                var full = Path.GetFullPath(root);
                var hits = new Dictionary<string, List<LineHit>>(StringComparer.OrdinalIgnoreCase);
                bool unreadable = false;
                var managed = new List<string>();
                var start = CreateStart(executable, full, extension, ["--json", "--ignore-case", "--regexp", EscapeLiteral(text),
                    "--regexp", @"(?-u:\A\x00)", "--regexp", @"(?-u:\r[^\n])"]);
                var exit = await RunAsync(start, line =>
                {
                    switch (ParseRecord(line, full, text, out var path, out var hit))
                    {
                        case Record.Hit:
                            if (!hits.TryGetValue(path!, out var list)) hits[path!] = list = [];
                            list.Add(hit);
                            break;
                        case Record.Managed: managed.Add(path!); break;
                        case Record.Unreadable: unreadable = true; break;
                    }
                }, timeout.Token);
                if (exit is not (0 or 1) || unreadable)
                {
                    log?.Invoke("Text search engine: managed (ripgrep returned an error).");
                    return null;
                }
                foreach (var pair in hits) result.Hits.TryAdd(pair.Key, pair.Value);
                result.Managed.UnionWith(managed);
            }
            foreach (var file in result.Managed) result.Hits.Remove(file);
            log?.Invoke($"Text search engine: ripgrep lines, files {result.Hits.Count}, managed files {result.Managed.Count}; no source re-read.");
            return result;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            log?.Invoke("Text search engine: managed (ripgrep timed out).");
            return null;
        }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException or FormatException or System.Text.Json.JsonException)
        {
            log?.Invoke($"Text search engine: managed (ripgrep failed: {error.GetType().Name}).");
            return null;
        }
    }

    private static ProcessStartInfo CreateStart(string executable, string root, string? extension, string[] options)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[] { "--no-config", "--no-ignore", "--hidden", "--text" }.Concat(options)) start.ArgumentList.Add(arg);
        foreach (var suffix in extension is null ? Extensions : new[] { extension })
        {
            start.ArgumentList.Add("--iglob"); start.ArgumentList.Add("*" + suffix);
        }
        foreach (var excluded in Exclusions)
        {
            start.ArgumentList.Add("--iglob"); start.ArgumentList.Add("!**/" + excluded + "/**");
        }
        start.ArgumentList.Add("--"); start.ArgumentList.Add(".");
        return start;
    }

    // Returns ripgrep's exit code, or -1 when the process did not start. Output is consumed line by line.
    private static async Task<int> RunAsync(ProcessStartInfo start, Action<string> onLine, CancellationToken token)
    {
        using var process = new Process { StartInfo = start };
        if (!process.Start()) return -1;
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            while (await process.StandardOutput.ReadLineAsync(token) is { } line)
                if (line.Length > 0) onLine(line);
            await process.WaitForExitAsync(token);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
            }
            try { await stderr; } catch (IOException) { }
        }
        return process.ExitCode;
    }

    private enum Record { Skip, Hit, Managed, Unreadable }

    // Parses one `rg --json` record. Records that are not matches are skipped. A match whose path is not UTF-8
    // text cannot be mapped to a file, so the caller falls back to the managed search.
    private static Record ParseRecord(string json, string root, string text, out string? path, out LineHit hit)
    {
        path = null; hit = default;
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var element = document.RootElement;
        if (element.GetProperty("type").GetString() != "match") return Record.Skip;
        var data = element.GetProperty("data");
        if (!data.GetProperty("path").TryGetProperty("text", out var pathText)) return Record.Unreadable;
        var full = Path.GetFullPath(pathText.GetString()!, root);
        var lines = data.GetProperty("lines");
        var raw = lines.TryGetProperty("text", out var textValue) ? textValue.GetString()!
            : Encoding.UTF8.GetString(Convert.FromBase64String(lines.GetProperty("bytes").GetString()!));
        var number = data.GetProperty("line_number").GetInt32();
        if (number == 1 && raw.StartsWith('\uFEFF')) raw = raw[1..]; // The managed reader drops a UTF-8 BOM.
        if (raw.EndsWith('\n')) raw = raw[..^1];
        if (raw.EndsWith('\r')) raw = raw[..^1];
        path = full;
        // Lines matched by the NUL / lone-CR patterns mean the managed reader would split or decode this file differently.
        if ((number == 1 && raw.StartsWith('\0')) || raw.Contains('\r')) return Record.Managed;
        // Column and preview use the managed rule on ripgrep's line; a line it matched only through different case folding is dropped.
        var match = RoslynAnalyzer.CreateTextMatch(full, number, raw, text);
        if (match is null) return Record.Skip;
        hit = new(match.Line, match.Column, match.Preview);
        return Record.Hit;
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
