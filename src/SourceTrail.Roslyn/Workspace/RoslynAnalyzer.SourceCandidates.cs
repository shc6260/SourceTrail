namespace SourceTrail.Roslyn.Workspace;

public sealed partial class RoslynAnalyzer
{
    private bool _symbolsStale;

    // Rebuild the flattened declaration list once after a batch parse instead of after every file.
    private void RefreshIndexedSymbols()
    {
        if (!_symbolsStale) return;
        _indexedSymbols = _sourceFiles.SelectMany(source => source.Symbols).ToArray();
        _symbolsStale = false;
    }

    // ripgrep narrows which catalog files must be read for a name scan; the managed read still verifies every candidate.
    private async Task<SourceCandidates> SourceCandidatesAsync(string text, CancellationToken token)
    {
        if (!_options.UseRipgrep || _sourceFiles.Length == 0) return SourceCandidates.All;
        var root = GetSolutionRoot();
        var files = await RipgrepTextSearch.FindFilesAsync([root], ".cs", text, _options.RipgrepPath, log, token);
        return files is null ? SourceCandidates.All : new SourceCandidates(root, files);
    }

    internal sealed class SourceCandidates(string? root, HashSet<string>? files)
    {
        public static readonly SourceCandidates All = new(null, null);
        private static readonly string[] Excluded = ["bin", "obj", ".git", ".svn", ".vs", "node_modules"];
        private readonly string? _prefix = root is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;

        /// <summary>True only when ripgrep searched this file's location and did not select it.</summary>
        public bool Skip(string file)
        {
            if (files is null || _prefix is null || files.Contains(file)) return false;
            if (!file.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase)) return false;
            if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return false;
            var relative = file[_prefix.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return !relative.Take(relative.Length - 1).Any(part => Excluded.Contains(part, StringComparer.OrdinalIgnoreCase));
        }
    }
}
