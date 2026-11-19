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
            var roots = directory is null ? new[] { solutionRoot }.Concat(_options.AdditionalSearchRoots) :
                new[] { ResolveSolutionPath(solutionRoot, directory) };
            string[] files;
            var started = Stopwatch.GetTimestamp();
            using (AnalysisTelemetry.Measure("text.enumerate"))
            {
                files = roots.SelectMany(root => EnumerateSearchFiles(root, extension))
                    .Select(file => { cancellationToken.ThrowIfCancellationRequested(); return file; })
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
            var enumerateMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            started = Stopwatch.GetTimestamp();
            using (AnalysisTelemetry.Measure("text.scan"))
                foreach (var file in files) CollectTextMatches(file, text, matches, cancellationToken);
            log?.Invoke($"Text search timing: queue {queueMs:F3} ms, enumerate {enumerateMs:F3} ms, " +
                $"scan {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F3} ms, files {files.Length}, matches {matches.Count}.");
            return Paginate(matches.ToArray(), offset, limit);
        }
        finally { _gate.Release(); }
    }

    private static IEnumerable<string> EnumerateSearchFiles(string root, string? extension)
    {
        if (!Directory.Exists(root)) yield break;
        var pending = new Stack<string>(); pending.Push(Path.GetFullPath(root));
        while (pending.TryPop(out var current))
        {
            string[] files; string[] directories;
            try { files = Directory.GetFiles(current); directories = Directory.GetDirectories(current); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var file in files)
                if (extension is null ? SearchExtensions.Contains(Path.GetExtension(file)) :
                    file.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) yield return file;
            foreach (var directory in directories)
                if (!ExcludedDirectories.Contains(Path.GetFileName(directory)) &&
                    (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0) pending.Push(directory);
        }
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
                int position = line.IndexOf(text, StringComparison.OrdinalIgnoreCase);
                if (position >= 0) matches.Add(new(file, lineNumber, position + 1, line.Trim()[..Math.Min(line.Trim().Length, 240)]));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }


}
