using SourceTrail.Core.Models;

namespace SourceTrail.Roslyn.Workspace;

// Notifications authorize skipping scans; errors restore full request-boundary checks.
internal sealed class SolutionInputMonitor : IDisposable
{
    private readonly List<FileSystemWatcher> watchers = [];
    private readonly HashSet<string> exactPaths;
    private readonly string[] roots;
    private readonly TimeProvider clock;
    private readonly TimeSpan interval;
    private long version;
    private long checkedVersion;
    private long checkedAt;
    private bool failed;
    private bool hasChecked;
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", ".git", ".svn", ".vs", "node_modules" };

    internal SolutionInputMonitor(SolutionState state, IEnumerable<string> inputs, TimeSpan interval,
        TimeProvider? clock = null)
    {
        this.interval = interval;
        this.clock = clock ?? TimeProvider.System;
        exactPaths = inputs.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        exactPaths.Add(Path.GetFullPath(state.SolutionPath!));
        foreach (var project in state.Projects)
            if (project.File is not null) exactPaths.Add(Path.GetFullPath(project.File));
        var candidates = exactPaths.Where(p => Path.GetExtension(p).Equals(".cs", StringComparison.OrdinalIgnoreCase))
            .Select(p => Path.GetDirectoryName(p)!)
            .Concat(state.Projects.Where(p => p.File is not null).Select(p => Path.GetDirectoryName(Path.GetFullPath(p.File!))!))
            .Append(Directory.Exists(state.SolutionPath) ? Path.GetFullPath(state.SolutionPath!) : Path.GetDirectoryName(Path.GetFullPath(state.SolutionPath!))!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        roots = candidates.Where(root => !candidates.Any(other => IsUnder(root, other))).ToArray();
        try
        {
            foreach (var root in roots) Watch(root, true);
            foreach (var directory in exactPaths.Select(p => Path.GetDirectoryName(p)!).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                // Explicit obj inputs and references remain covered outside normal recursive scans.
                if (Covered(directory)) continue;
                Watch(directory, false);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            NotifyError();
            foreach (var watcher in watchers) watcher.Dispose();
            watchers.Clear();
        }
    }

    internal long Version => Interlocked.Read(ref version);
    internal bool HasChanges => Volatile.Read(ref failed) || Version != checkedVersion;
    internal bool NeedsScan => Volatile.Read(ref failed) || !hasChecked || Version != checkedVersion ||
        clock.GetElapsedTime(checkedAt) >= interval;
    internal void CompleteScan(long observedVersion)
    {
        checkedVersion = observedVersion; // An event arriving during a scan stays pending.
        checkedAt = clock.GetTimestamp();
        hasChecked = true;
    }
    internal void NotifyError()
    {
        Volatile.Write(ref failed, true);
        Interlocked.Increment(ref version);
    }
    internal void NotifyChange(string path)
    {
        if (exactPaths.Contains(path) || roots.Any(root =>
            (path.Equals(root, StringComparison.OrdinalIgnoreCase) || IsUnder(path, root)) &&
            !ExcludedPath(path, root)))
            Interlocked.Increment(ref version);
    }
    private bool Covered(string directory) => roots.Any(root =>
        (directory.Equals(root, StringComparison.OrdinalIgnoreCase) || IsUnder(directory, root)) && !ExcludedPath(directory, root));
    private static bool IsUnder(string path, string root) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool ExcludedPath(string path, string root) =>
        Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(Excluded.Contains);
    private void Watch(string directory, bool recursive)
    {
        var watcher = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = recursive,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime
        };
        watchers.Add(watcher);
        watcher.Changed += (_, e) => NotifyChange(e.FullPath);
        watcher.Created += (_, e) => NotifyChange(e.FullPath);
        watcher.Deleted += (_, e) => NotifyChange(e.FullPath);
        watcher.Renamed += (_, e) => { NotifyChange(e.OldFullPath); NotifyChange(e.FullPath); };
        watcher.Error += (_, _) => NotifyError();
        watcher.EnableRaisingEvents = true;
    }
    public void Dispose()
    {
        foreach (var watcher in watchers) watcher.Dispose();
    }
}
