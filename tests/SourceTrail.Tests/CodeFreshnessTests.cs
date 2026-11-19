using System.Diagnostics;
using SourceTrail.Core.Contracts;
using SourceTrail.Core.Models;
using SourceTrail.Roslyn.Workspace;
using Xunit;

namespace SourceTrail.Tests;

public sealed class CodeFreshnessTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "sourcetrail-monitor-" + Guid.NewGuid().ToString("N"));
    private SolutionState State(string directory)
    {
        Directory.CreateDirectory(directory);
        string solution = Path.Combine(directory, "Test.sln");
        File.WriteAllText(solution, "");
        return new("Ready", solution, "test", null, [], []);
    }

    [Fact]
    public void FirstScanPeriodicReconciliationAndConcurrentEventsCannotBeSkipped()
    {
        var clock = new ManualClock();
        var state = State(root);
        using var monitor = new SolutionInputMonitor(state, [], TimeSpan.FromSeconds(30), clock);
        Assert.True(monitor.NeedsScan);
        monitor.CompleteScan(monitor.Version);
        Assert.False(monitor.NeedsScan);
        clock.Advance(29);
        Assert.False(monitor.NeedsScan);
        clock.Advance(1);
        Assert.True(monitor.NeedsScan);
        long started = monitor.Version;
        monitor.NotifyChange(Path.Combine(root, "New.cs"));
        monitor.CompleteScan(started);
        Assert.True(monitor.NeedsScan);
        Assert.True(monitor.HasChanges);
        monitor.CompleteScan(monitor.Version);
        Assert.False(monitor.NeedsScan);
        monitor.NotifyError();
        monitor.CompleteScan(monitor.Version);
        Assert.True(monitor.NeedsScan); // Overflow/error keeps strict fallback, even after a successful scan.
    }

    [Fact]
    public async Task ExplicitInputsRemainCoveredButAncestorBuildSettingsAreNotDependencies()
    {
        string repo = Path.Combine(root, "repo"), external = Path.Combine(root, "external");
        Directory.CreateDirectory(external);
        var state = State(repo);
        string assets = Path.Combine(repo, "obj", "project.assets.json");
        Directory.CreateDirectory(Path.GetDirectoryName(assets)!);
        string reference = Path.Combine(external, "Reference.dll");
        using var monitor = new SolutionInputMonitor(state, [assets, reference], TimeSpan.FromMinutes(1));
        monitor.CompleteScan(monitor.Version);
        monitor.NotifyChange(Path.Combine(repo, "bin", "output.dll"));
        Assert.False(monitor.NeedsScan);
        foreach (string input in new[] { assets, reference })
        {
            monitor.NotifyChange(input);
            Assert.True(monitor.NeedsScan);
            monitor.CompleteScan(monitor.Version);
        }
        await AwaitNotification(monitor, () => File.WriteAllText(assets, "{}"));
        await AwaitNotification(monitor, () => File.WriteAllText(reference, "reference"));
    }

    [Fact]
    public void UnlistedAncestorBuildSettingsDoNotInvalidateSourceIndex()
    {
        var state = State(Path.Combine(root, "repo"));
        using var monitor = new SolutionInputMonitor(state, [], TimeSpan.FromMinutes(1));
        monitor.CompleteScan(monitor.Version);
        long version = monitor.Version;
        monitor.NotifyChange(Path.Combine(root, "Directory.Build.props"));
        Assert.Equal(version, monitor.Version);
        Assert.False(monitor.NeedsScan);
    }

    [Fact]
    public void UnavailableWatchScopeFallsBackToFullChecks()
    {
        var state = State(Path.Combine(root, "repo"));
        using var monitor = new SolutionInputMonitor(state, [Path.Combine(root, "missing", "Reference.dll")], TimeSpan.FromMinutes(1));
        monitor.CompleteScan(monitor.Version);
        Assert.True(monitor.NeedsScan);
    }

    [Fact]
    public async Task ActualNotificationsCoverCreateEditRenameAndDirectoryDeletion()
    {
        var state = State(root);
        using var monitor = new SolutionInputMonitor(state, [], TimeSpan.FromMinutes(1));
        monitor.CompleteScan(monitor.Version);
        string directory = Path.Combine(root, "Folder.with.dot");
        await AwaitNotification(monitor, () => Directory.CreateDirectory(directory));
        string source = Path.Combine(directory, "Source.cs");
        await AwaitNotification(monitor, () => File.WriteAllText(source, "class A {}"));
        await AwaitNotification(monitor, () => File.WriteAllText(source, "class B {}"));
        await AwaitNotification(monitor, () => File.Move(source, Path.Combine(directory, "Renamed.cs")));
        await AwaitNotification(monitor, () => Directory.Delete(directory, true));
    }

    [Fact]
    public async Task NotifiedEditsCanForceHashEvenWithRestoredTimestampAndSize()
    {
        var state = State(root);
        string source = Path.Combine(root, "Source.cs");
        await File.WriteAllTextAsync(source, "class A {}");
        var before = await SolutionInputTracker.CaptureAsync(state, [], [], default);
        var stamp = Assert.Single(before, f => f.Path == source);
        await File.WriteAllTextAsync(source, "class B {}");
        File.SetLastWriteTimeUtc(source, stamp.LastWriteUtc);
        var after = await SolutionInputTracker.CaptureAsync(state, [], before, default, forceHash: true);
        Assert.False(SolutionInputTracker.SameContents(before, after));
        File.SetLastWriteTimeUtc(source, stamp.LastWriteUtc.AddMinutes(1));
        var touched = await SolutionInputTracker.CaptureAsync(state, [], after, default, forceHash: true);
        Assert.True(SolutionInputTracker.SameContents(after, touched));
    }

    private static async Task AwaitNotification(SolutionInputMonitor monitor, Action change)
    {
        long version = monitor.Version;
        change();
        var timer = Stopwatch.StartNew();
        while (monitor.Version == version && timer.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
        Assert.True(monitor.Version != version, "File notification did not arrive within five seconds.");
    }
    private sealed class ManualClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public void Advance(int seconds) => ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}

[Collection("Solution")]
public sealed class CodeFreshnessIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangesInvalidateDiskSnapshotsInStrictAndWatchedModes(bool watch)
    {
        string root = Path.Combine(Path.GetTempPath(), "sourcetrail-fresh-" + Guid.NewGuid().ToString("N"));
        string repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(repo);
        try
        {
            string solution = Path.Combine(repo, "Test.sln");
            File.WriteAllText(Path.Combine(repo, "Test.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(solution, "Microsoft Visual Studio Solution File, Format Version 12.00\r\nProject(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Test\", \"Test.csproj\", \"{A46FEED2-5B25-4FDB-AF18-F0A65EAF9D13}\"\r\nEndProject\r\nGlobal\r\nEndGlobal\r\n");
            string source = Path.Combine(repo, "First.cs");
            File.WriteAllText(source, "public class First { public void Before() {} }");
            using var code = new RoslynAnalyzer(new AnalysisOptions { WatchCodeFiles = watch, CodeCacheDirectory = Path.Combine(root, "cache") });
            await code.LoadAsync(solution, default);
            await code.EnsureFreshAsync(default);
            Assert.NotEmpty((await code.FindSymbolsAsync("Before", 0, 100, default)).Items);
            string? initial = code.Status().SnapshotId;
            await code.EnsureFreshAsync(default);
            Assert.Equal(initial, code.Status().SnapshotId);
            File.WriteAllText(source, "public class First { public void After() {} }");
            await WaitForReload(code, initial);
            Assert.Empty((await code.FindSymbolsAsync("Before", 0, 100, default)).Items);
            Assert.NotEmpty((await code.FindSymbolsAsync("After", 0, 100, default)).Items);
            initial = code.Status().SnapshotId;
            string added = Path.Combine(repo, "Added.cs");
            File.WriteAllText(added, "public class Added {}");
            await WaitForReload(code, initial);
            Assert.NotEmpty((await code.FindSymbolsAsync("Added", 0, 100, default)).Items);
            initial = code.Status().SnapshotId;
            File.Delete(added);
            await WaitForReload(code, initial);
            Assert.Empty((await code.FindSymbolsAsync("Added", 0, 100, default)).Items);
            initial = code.Status().SnapshotId;
            File.WriteAllText(Path.Combine(repo, "Conditional.cs"), "#if FEATURE\r\npublic class FeatureEnabled {}\r\n#endif\r\n");
            await WaitForReload(code, initial);
            Assert.Empty((await code.FindSymbolsAsync("FeatureEnabled", 0, 100, default)).Items);
            initial = code.Status().SnapshotId;
            File.WriteAllText(Path.Combine(repo, "Test.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><DefineConstants>FEATURE</DefineConstants></PropertyGroup></Project>");
            await WaitForReload(code, initial);
            Assert.NotEmpty((await code.FindSymbolsAsync("FeatureEnabled", 0, 100, default)).Items);
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => code.EnsureFreshAsync(cancel.Token));
            await code.EnsureFreshAsync(default);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static async Task WaitForReload(RoslynAnalyzer code, string? previous)
    {
        var timer = Stopwatch.StartNew();
        do
        {
            await code.EnsureFreshAsync(default);
            if (code.Status().SnapshotId != previous) return;
            await Task.Delay(20);
        } while (timer.Elapsed < TimeSpan.FromSeconds(10));
        Assert.NotEqual(previous, code.Status().SnapshotId);
    }
}
