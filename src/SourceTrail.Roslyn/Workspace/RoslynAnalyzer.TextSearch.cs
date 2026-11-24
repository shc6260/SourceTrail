using System.Diagnostics;
using SourceTrail.Core;
using SourceTrail.Core.Models;
namespace SourceTrail.Roslyn.Workspace;
public sealed partial class RoslynAnalyzer
{
    public async Task<Page<TextMatch>> SearchTextAsync(string text, int offset, int limit, CancellationToken cancellationToken,
        string? directory = null, string? extension = null)
    {
        RequireText(text, nameof(text)); ValidatePage(offset, limit);
        if (extension is not null && (!extension.StartsWith('.') || extension.Contains("..") ||
            extension.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')) ||
            !SearchExtensions.Contains(Path.GetExtension("file" + extension))))
            throw new ArgumentException("Use .cs, .config, .xml, .sql, or a compound suffix such as .Designer.cs; wildcards and paths are not supported.", nameof(extension));
        var queued = Stopwatch.GetTimestamp();
        using (AnalysisTelemetry.Measure("text.queue")) await _gate.WaitAsync(cancellationToken);
        var queueMs = Stopwatch.GetElapsedTime(queued).TotalMilliseconds;
        try
        {
            var solutionRoot = GetSolutionRoot();
            var matches = new List<TextMatch>();
            var roots = (directory is null ? new[] { solutionRoot }.Concat(_options.AdditionalSearchRoots) :
                new[] { ResolveSolutionPath(solutionRoot, directory) }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            string[] files;
            var linkedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var started = Stopwatch.GetTimestamp();
            using (AnalysisTelemetry.Measure("text.enumerate"))
            {
                files = roots.SelectMany(root => EnumerateSearchFiles(root, extension, linkedFiles))
                    .Select(file => { cancellationToken.ThrowIfCancellationRequested(); return file; })
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
            var enumerateMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            started = Stopwatch.GetTimestamp();
            HashSet<string>? candidates = null;
            RipgrepTextSearch.LineSearchResult? lines = null;
            if (_options.UseRipgrep && files.Length > 0)
                using (AnalysisTelemetry.Measure("text.ripgrep"))
                {
                    // Lines come straight from ripgrep; only text whose case folding ripgrep cannot match exactly keeps the file-candidate path.
                    lines = await RipgrepTextSearch.FindLinesAsync(roots, extension, text, _options.RipgrepPath, log, cancellationToken);
                    if (lines is null && !RipgrepTextSearch.SupportsLineResults(text))
                        candidates = await RipgrepTextSearch.FindFilesAsync(roots, extension, text, _options.RipgrepPath, log, cancellationToken);
                }
            var ripgrepMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            started = Stopwatch.GetTimestamp();
            using (AnalysisTelemetry.Measure("text.scan"))
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (lines is not null && !linkedFiles.Contains(file) && !lines.Managed.Contains(file))
                    {
                        if (lines.Hits.TryGetValue(file, out var hits))
                            matches.AddRange(hits.Select(hit => new TextMatch(file, hit.Line, hit.Column, hit.Preview)));
                    }
                    else if (candidates is null || candidates.Contains(file) || linkedFiles.Contains(file))
                        CollectTextMatches(file, text, matches, cancellationToken);
                }
            log?.Invoke($"Text search timing: queue {queueMs:F3} ms, enumerate {enumerateMs:F3} ms, ripgrep {ripgrepMs:F3} ms, " +
                $"scan {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F3} ms, files {files.Length}, matches {matches.Count}.");
            return Paginate(matches.ToArray(), offset, limit);
        }
        finally { _gate.Release(); }
    }

    private static bool IsLinkedSearchFile(FileInfo file)
    {
        try { return (file.Attributes & FileAttributes.ReparsePoint) != 0; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private static IEnumerable<string> EnumerateSearchFiles(string root, string? extension, HashSet<string>? linkedFiles = null)
    {
        if (!Directory.Exists(root)) yield break;
        var pending = new Stack<string>(); pending.Push(Path.GetFullPath(root));
        while (pending.TryPop(out var current))
        {
            FileInfo[] files; DirectoryInfo[] directories;
            try
            {
                var folder = new DirectoryInfo(current);
                files = folder.GetFiles(); directories = folder.GetDirectories();
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var file in files)
                if (extension is null ? SearchExtensions.Contains(file.Extension) :
                    file.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    if (linkedFiles is not null && IsLinkedSearchFile(file)) linkedFiles.Add(file.FullName);
                    yield return file.FullName;
                }
            foreach (var directory in directories)
                if (!ExcludedDirectories.Contains(directory.Name) &&
                    (directory.Attributes & FileAttributes.ReparsePoint) == 0) pending.Push(directory.FullName);
        }
    }

    // One match per line: 1-based UTF-16 column of the first occurrence and the trimmed preview (240 characters at most).
    internal static TextMatch? CreateTextMatch(string file, int lineNumber, string line, string text)
    {
        int position = line.IndexOf(text, StringComparison.OrdinalIgnoreCase);
        if (position < 0) return null;
        var trimmed = line.Trim();
        return new(file, lineNumber, position + 1, trimmed[..Math.Min(trimmed.Length, 240)]);
    }

    private static void CollectTextMatches(string file, string text,
        List<TextMatch> matches, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int lineNumber = 0;
        try
        {
            foreach (var line in File.ReadLines(file))
            {
                cancellationToken.ThrowIfCancellationRequested();
                lineNumber++;
                if (CreateTextMatch(file, lineNumber, line, text) is { } match) matches.Add(match);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }


}
