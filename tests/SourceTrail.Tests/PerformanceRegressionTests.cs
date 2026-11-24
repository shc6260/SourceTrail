using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SourceTrail.Core.Contracts;
using SourceTrail.Core.Models;
using SourceTrail.Mcp.Tools;
using SourceTrail.Roslyn.Workspace;
using Xunit;
namespace SourceTrail.Tests;
public sealed class PerformanceRegressionTests
{
    [Fact]
    public async Task TextSearchSkipsIndexPreparationAndSourceFailuresReleaseGate()
    {
        using var code = new StubCode(); using var gate = new SemaphoreSlim(1, 1);
        var tools = new AnalysisTools(code, NullLogger<AnalysisTools>.Instance, new AnalysisOptions(), gate, new SourceTrail.Mcp.ServerOptions());
        await tools.SearchText("Save", default);
        Assert.Equal(0, code.FreshnessCalls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tools.FindSymbol("Save", default));
        Assert.Equal(1, code.FreshnessCalls); Assert.Equal(1, gate.CurrentCount);
    }
    private sealed class StubCode : ICodeAnalyzer, IRefreshableCodeAnalyzer
    {
        public int FreshnessCalls;
        public Task EnsureFreshAsync(CancellationToken token) { FreshnessCalls++; throw new InvalidOperationException("fixture"); }
        public SolutionState Status() => new("NotLoaded", null, null, null, [], []);
        public Task<SolutionState> LoadAsync(string path, CancellationToken token) => Task.FromResult(Status());
        public Task<SolutionState> ReloadAsync(CancellationToken token) => Task.FromResult(Status());
        public Task<Page<SymbolInfo>> FindSymbolsAsync(string q, int o, int l, CancellationToken t) => throw new NotSupportedException();
        public Task<SymbolOverview> OverviewAsync(string s, CancellationToken t, string? memberQuery = null) => throw new NotSupportedException();
        public Task<Page<ReferenceInfo>> ReferencesAsync(string s, int o, int l, CancellationToken t) => throw new NotSupportedException();
        public Task<Page<EventHandlerMatch>> FindEventHandlersAsync(string f, string? q, int o, int l, CancellationToken t) => throw new NotSupportedException();
        public Task<Page<TextMatch>> SearchTextAsync(string s, int o, int l, CancellationToken t, string? directory = null, string? extension = null) => Task.FromResult(new Page<TextMatch>([], 0, o, l, false, null, []));
        public Task<Page<ProcedureUsage>> ProcedureUsagesAsync(string p, int o, int l, CancellationToken t) => throw new NotSupportedException();
        public void Dispose() { }
    }    [Fact]
    public async Task StreamingFingerprintMatchesPreviousByteArrayAlgorithm()
    {
        string root = Path.Combine(Path.GetTempPath(), "sourcetrail-hash-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            string large = Path.Combine(root, "reference.dll"), empty = Path.Combine(root, "empty.cs");
            await File.WriteAllBytesAsync(large, Enumerable.Range(0, 1024 * 1024).Select(i => (byte)i).ToArray());
            await File.WriteAllTextAsync(empty, "");
            string[] paths = [large, Path.Combine(root, "missing.cs"), empty];
            using var old = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var path in paths.Order(StringComparer.OrdinalIgnoreCase))
            {
                old.AppendData(Encoding.UTF8.GetBytes(path));
                old.AppendData(File.Exists(path) ? SHA256.HashData(await File.ReadAllBytesAsync(path)) : [0]);
            }
            string expected = Convert.ToHexString(old.GetHashAndReset());
            Assert.Equal(expected, await SolutionInputTracker.ComputeFingerprintAsync(paths, default));
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SolutionInputTracker.ComputeFingerprintAsync(paths, cancel.Token));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DiskIndexStampsDistinguishTimestampChangesFromContentChanges()
    {
        string root = Path.Combine(Path.GetTempPath(), "sourcetrail-stamps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string solution = Path.Combine(root, "Fixture.sln"), source = Path.Combine(root, "Fixture.cs");
            await File.WriteAllTextAsync(solution, "");
            await File.WriteAllTextAsync(source, "class A {}");
            var state = new SolutionState("Ready", solution, null, null, [], []);
            var original = await SolutionInputTracker.CaptureAsync(state, [], [], default);
            var stamp = Assert.Single(original, file => file.Path == source);
            File.SetLastWriteTimeUtc(source, stamp.LastWriteUtc.AddMinutes(-1));
            var touched = await SolutionInputTracker.CaptureAsync(state, [], original, default);
            Assert.True(SolutionInputTracker.SameContents(original, touched));
            await File.WriteAllTextAsync(source, "class B {}");
            var edited = await SolutionInputTracker.CaptureAsync(state, [], touched, default);
            Assert.False(SolutionInputTracker.SameContents(original, edited));
            File.Delete(source);
            var removed = await SolutionInputTracker.CaptureAsync(state, [], edited, default);
            Assert.False(SolutionInputTracker.SameContents(edited, removed));
        }
        finally { Directory.Delete(root, true); }
    }


}
[Collection("Solution")]
public sealed class SourceIndexRegressionTests(LoadedSolution fixture)
{
    [Fact]
    public async Task DiskIndexAndSourceIdsAreStableAcrossReopenAndExplicitReload()
    {
        string cache = Path.Combine(Path.GetTempPath(), "source-cache-" + Guid.NewGuid());
        try
        {
            var options = new AnalysisOptions { CodeCacheDirectory = cache };
            using var first = new RoslynAnalyzer(options);
            var state = await first.LoadAsync(fixture.FixturePath, default);
            var symbol = Assert.Single((await first.FindSymbolsAsync("Demo.Data.Repository.Save()", 0, 20, default)).Items);
            var messages = new List<string>();
            using var second = new RoslynAnalyzer(options, messages.Add);
            Assert.Equal(state.SnapshotId, (await second.LoadAsync(fixture.FixturePath, default)).SnapshotId);
            Assert.Equal(symbol, Assert.Single((await second.FindSymbolsAsync(symbol.FullName, 0, 20, default)).Items));
            Assert.Contains(messages, message => message.Contains("parsed 0"));
            Assert.DoesNotContain(messages, message => message.Contains("Loading solution"));
            await second.ReloadAsync(default);
            Assert.Equal(symbol.Id, Assert.Single((await second.FindSymbolsAsync(symbol.FullName, 0, 20, default)).Items).Id);
        }
        finally { if (Directory.Exists(cache)) Directory.Delete(cache, true); }
    }
    [Fact]
    public async Task CancellationDuringIndexingDoesNotPublishPartialCache()
    {
        using var code = new RoslynAnalyzer(); using var cancel = new CancellationTokenSource();
        await code.LoadAsync(fixture.FixturePath, default);
        var snapshot = code.Status().SnapshotId;
        code.SymbolDocumentIndexed = () => cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => code.FindSymbolsAsync("Demo.", 0, 20, cancel.Token));
        Assert.Equal(snapshot, code.Status().SnapshotId);
        code.SymbolDocumentIndexed = null;
        await code.LoadAsync(fixture.FixturePath, default);
        Assert.NotEmpty((await code.FindSymbolsAsync("Repository", 0, 20, default)).Items);
    }
    [Fact]
    public async Task FolderSearchNeedsNeitherProjectNorReferenceAssemblies()
    {
        string root = Path.Combine(Path.GetTempPath(), "plain-source-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "Source.cs"), "namespace Plain; public class Source : UnknownLibrary { public void Save(UnknownType x) { Api.Send(x); } }");
            using var code = new RoslynAnalyzer();
            var state = await code.LoadAsync(root, default);
            Assert.Equal("Ready", state.Status);
            var symbol = Assert.Single((await code.FindSymbolsAsync("Plain.Source.Save", 0, 20, default)).Items);
            Assert.Contains((await code.FindDatabaseCallsAsync(symbol.Id, 20, default)).MethodCandidates, method => method.Name == "Send");
            string snapshot = state.SnapshotId!;
            await File.WriteAllTextAsync(Path.Combine(root, "Reference.dll"), "changed");
            await File.WriteAllTextAsync(Path.Combine(root, "NuGet.Config"), "changed");
            await code.EnsureFreshAsync(default);
            Assert.Equal(snapshot, code.Status().SnapshotId);
            await File.WriteAllTextAsync(Path.Combine(root, "Source.cs"), "namespace Plain; public class Source { public void Changed() {} }");
            await code.EnsureFreshAsync(default);
            Assert.NotEqual(snapshot, code.Status().SnapshotId);
            Assert.Empty((await code.FindSymbolsAsync("Save", 0, 20, default)).Items);
            Assert.NotEmpty((await code.FindSymbolsAsync("Changed", 0, 20, default)).Items);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task SyntaxErrorsAreReportedWithoutStoppingOtherDeclarations()
    {
        string root = Path.Combine(Path.GetTempPath(), "bad-source-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "Good.cs"), "class Good { void Save() {} }");
            await File.WriteAllTextAsync(Path.Combine(root, "Bad.cs"), "class Bad { void Broken( }");
            using var code = new RoslynAnalyzer();
            await code.LoadAsync(root, default);
            await code.FindSymbolsAsync("Bad", 0, 20, default);
            var state = code.Status();
            Assert.Equal("Partial", state.Status); Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Severity == "SyntaxError");
            Assert.NotEmpty((await code.FindSymbolsAsync("Good.Save", 0, 20, default)).Items);
        }
        finally { Directory.Delete(root, true); }
    }
}
