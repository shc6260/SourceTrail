using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SourceTrail.Core.Contracts;
using SourceTrail.Core.Flow;
using SourceTrail.Core.Models;
using SourceTrail.Mcp.Tools;
using SourceTrail.Roslyn.Workspace;
using Xunit;

namespace SourceTrail.Tests;

public sealed class PerformanceRegressionTests
{
    [Fact]
    public async Task SqlToolDoesNotInspectCSharpButCodeToolsStillDo()
    {
        using var code = new StubCode { FailFreshness = true };
        var sql = new StubSql();
        using var gate = new SemaphoreSlim(1, 1);
        var tools = new AnalysisTools(code, sql, new FlowAnalyzer(code, sql),
            NullLogger<AnalysisTools>.Instance, new(), gate);
        var result = await tools.AnalyzeProcedure("dbo.Save", default, true);
        Assert.Equal("dbo.Save", result.Procedure);
        Assert.True(sql.IncludedDefinition);
        Assert.Equal(0, code.FreshnessCalls);
        await tools.SearchText("Save", default);
        Assert.Equal(0, code.FreshnessCalls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tools.FindSymbol("Save", default));
        Assert.Equal(1, code.FreshnessCalls);
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task SqlFailureAndCancellationDoNotLeakExecutionGate()
    {
        using var code = new StubCode();
        var sql = new StubSql { Fail = true };
        using var gate = new SemaphoreSlim(1, 1);
        var tools = new AnalysisTools(code, sql, new FlowAnalyzer(code, sql),
            NullLogger<AnalysisTools>.Instance, new(), gate);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tools.AnalyzeProcedure("dbo.Save", default));
        Assert.Equal(1, gate.CurrentCount);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tools.AnalyzeProcedure("dbo.Save", cancel.Token));
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
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
    public void CompactRoundTripPreservesEveryCallSiteAndStateAndReducesRepeatedText()
    {
        var nodes = Enumerable.Range(0, 8).Select(i => new FlowNode(new string('x', 120) + i,
            "Example.Type.Method" + i, "Method", "Project", "C:\\source\\shared.cs", i + 1)).ToArray();
        var edges = Enumerable.Range(0, 120).Select(i => new FlowEdge(nodes[i % 8].Id, nodes[(i + 1) % 8].Id,
            i % 2 == 0 ? "RoslynInvocation" : "DispatchCandidate", "Static evidence; runtime execution is not proven.",
            i % 3 == 0 ? null : "C:\\source\\shared.cs", i % 5 == 0 ? null : i)).ToArray();
        var original = new FlowResult("Partial", "CodeToDatabase", "snapshot", nodes, edges, true, ["warning", "warning"]);
        var compact = FlowCompactor.Compact(original);
        var wire = JsonSerializer.Deserialize<CompactFlowResult>(JsonSerializer.Serialize(compact))!;
        var expanded = FlowCompactor.Expand(wire);
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(expanded));
        Assert.True(JsonSerializer.Serialize(compact).Length < JsonSerializer.Serialize(original).Length / 2);
        Assert.Throws<ArgumentException>(() => FlowCompactor.Expand(compact with { SchemaVersion = 2 }));
        Assert.Throws<ArgumentException>(() => FlowCompactor.Expand(compact with { Edges = [new(-1, 0, 0, 0, null, null)] }));
        var empty = new FlowResult("Ready", "ProcedureToUi", null, [], [], false, []);
        Assert.Equal(JsonSerializer.Serialize(empty), JsonSerializer.Serialize(FlowCompactor.Expand(FlowCompactor.Compact(empty))));
    }

    private sealed class StubSql : IProcedureAnalyzer
    {
        public bool Fail { get; set; }
        public bool IncludedDefinition { get; private set; }
        public Task<ProcedureAnalysis> AnalyzeAsync(string procedure, bool includeDefinition, CancellationToken token)
        {
            if (Fail) throw new InvalidOperationException("SQL failed");
            IncludedDefinition = includeDefinition;
            return Task.FromResult(new ProcedureAnalysis("Ready", procedure, "dbo", "Test", includeDefinition ? "definition" : null, [], []));
        }
    }

    private sealed class StubCode : ICodeAnalyzer, IRefreshableCodeAnalyzer
    {
        public bool FailFreshness { get; set; }
        public int FreshnessCalls { get; private set; }
        public Task EnsureFreshAsync(CancellationToken token)
        {
            FreshnessCalls++;
            if (FailFreshness) throw new InvalidOperationException("C# failed");
            return Task.CompletedTask;
        }
        public SolutionState Status() => new("NotLoaded", null, null, null, [], []);
        public Task<SolutionState> LoadAsync(string path, CancellationToken token) => Task.FromResult(Status());
        public Task<SolutionState> ReloadAsync(CancellationToken token) => Task.FromResult(Status());
        public Task<Page<SymbolInfo>> FindSymbolsAsync(string query, int offset, int limit, CancellationToken token) => Task.FromResult(new Page<SymbolInfo>([], 0, offset, limit, false, null, []));
        public Task<SymbolOverview> OverviewAsync(string symbol, CancellationToken token) => throw new NotSupportedException();
        public Task<Page<ReferenceInfo>> ReferencesAsync(string symbol, int offset, int limit, CancellationToken token) => throw new NotSupportedException();
        public Task<Page<TextMatch>> SearchTextAsync(string text, int offset, int limit, CancellationToken token) =>
            Task.FromResult(new Page<TextMatch>([], 0, offset, limit, false, null, []));
        public Task<Page<ProcedureUsage>> ProcedureUsagesAsync(string procedure, int offset, int limit, CancellationToken token) => throw new NotSupportedException();
        public Task<CodeExpansion> ExpandAsync(string symbol, bool callers, CancellationToken token) => throw new NotSupportedException();
        public void Dispose() { }
    }
}

[Collection("Solution")]
public sealed class SymbolCancellationTests(LoadedSolution fixture)
{
    [Fact]
    public async Task ColdExpansionPreservesVerifiedCallsWithoutPromotingTextCandidates()
    {
        using var code = new RoslynAnalyzer(new AnalysisOptions
        {
            ProcedureCallRules = [new("Demo.Data.Db", "ExecuteProcedure", 0)]
        });
        await code.LoadAsync(fixture.FixturePath, default);
        var cold = await code.ExpandAsync("Demo.Data.Repository.Save()", false, default);
        var usage = Assert.Single(cold.ProcedureUsages);
        Assert.True(usage.IsVerifiedCall);
        Assert.Equal("dbo.usp_TestSave", usage.Procedure);

        var indexed = await code.ProcedureUsagesAsync("dbo.usp_TestSave", 0, 100, default);
        Assert.Equal(usage, Assert.Single(indexed.Items, item => item.IsVerifiedCall));
        Assert.Contains(indexed.Items, item => !item.IsVerifiedCall);
        var reverse = await code.ExpandAsync(cold.Symbol.Id, true, default);
        Assert.Equal(usage, Assert.Single(reverse.ProcedureUsages));
    }

    [Fact]
    public async Task ProcedurePathCapsReadsButPreservesWritesAndUnclassifiedDependencies()
    {
        var code = fixture.Code;
        var entry = (await code.FindSymbolsAsync("Demo.Data.Repository.Save()", 0, 100, default)).Items
            .Single(symbol => symbol.FullName == "Demo.Data.Repository.Save()");
        var reads = Enumerable.Range(1, 20).Select(line => Dependency("ReadCandidate", line)).ToArray();
        var retained = Enumerable.Range(21, 13).Select(line => Dependency("UpdateCandidate", line))
            .Concat(new[] { Dependency("Insert", 34), Dependency("DeleteCandidate", 35), Dependency("MergeCandidate", 36),
                Dependency("Execute", 37), Dependency("Reference", 38) }).ToArray();
        var sql = new DependencyFixture(reads.Concat(retained).ToArray());

        var result = await new FlowAnalyzer(code, sql).TraceProcedurePathAsync(entry.Id, "dbo.usp_TestSave", 0, 40, default);

        Assert.Equal("NotConnected", result.PathStatus);
        Assert.Equal(38, result.DependencyCount);
        Assert.Equal(retained.Concat(reads.Take(8)).ToArray(), result.DirectDependencies.ToArray());

        static SqlObject Dependency(string access, int line) =>
            new(null, null, "dbo", "Object" + line, "Table", access, false, false, false, "fixture.sql", line);
    }

    private sealed class DependencyFixture(IReadOnlyList<SqlObject> dependencies) : IProcedureAnalyzer
    {
        public Task<ProcedureAnalysis> AnalyzeAsync(string procedure, bool includeDefinition, CancellationToken token) =>
            Task.FromResult(new ProcedureAnalysis("Partial", procedure, "dbo", null, null, dependencies, [], "SqlFiles"));
    }

    [Fact]
    public async Task CompactMcpToolsPreserveNormalTraceResults()
    {
        var code = fixture.Code;
        var sql = new SourceTrail.SqlServer.Metadata.SqlProcedureAnalyzer();
        using var gate = new SemaphoreSlim(1, 1);
        var tools = new AnalysisTools(code, sql, new FlowAnalyzer(code, sql),
            NullLogger<AnalysisTools>.Instance, new(), gate);
        var symbol = (await code.FindSymbolsAsync("Demo.Data.Repository.Save()", 0, 100, default)).Items
            .Single(s => s.FullName == "Demo.Data.Repository.Save()");
        var plain = await tools.TraceCodeToDatabase(symbol.Id, default, 5, 40);
        var compact = await tools.TraceCodeToDatabaseCompact(symbol.Id, default, 5, 40);
        Assert.Equal(JsonSerializer.Serialize(plain), JsonSerializer.Serialize(FlowCompactor.Expand(compact)));
        var path = await tools.TraceProcedurePath(symbol.Id, "dbo.usp_TestSave", default, 5, 40);
        Assert.Equal("StaticPath", path.PathStatus);
        Assert.Equal("dbo.usp_TestSave", path.Steps.Last().Target);
        Assert.All(path.Steps, step => { Assert.NotNull(step.File); Assert.True(step.Line > 0); });
        var disconnected = await tools.TraceProcedurePath(symbol.Id, "dbo.usp_TestSave", default, 0, 40);
        Assert.Equal("NotConnected", disconnected.PathStatus);
        Assert.Empty(disconnected.Steps);
        Assert.True(disconnected.SearchTruncated);
        var reverse = await tools.TraceProcedureToUi("dbo.usp_TestSave", default, 5, 40);
        var reverseCompact = await tools.TraceProcedureToUiCompact("dbo.usp_TestSave", default, 5, 40);
        Assert.Equal(JsonSerializer.Serialize(reverse), JsonSerializer.Serialize(FlowCompactor.Expand(reverseCompact)));
    }

    [Fact]
    public async Task CancelledBuildRetriesToTheSameCompleteIndex()
    {
        using var code = new RoslynAnalyzer();
        await code.LoadAsync(fixture.FixturePath, default);
        using var cancel = new CancellationTokenSource();
        int processed = 0;
        code.SymbolDocumentIndexed = () => { processed++; cancel.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => code.FindSymbolsAsync("Demo.", 0, 1000, cancel.Token));
        Assert.True(processed > 0);
        code.SymbolDocumentIndexed = null;
        var actual = await code.FindSymbolsAsync("Demo.", 0, 1000, default);
        var expected = await fixture.Code.FindSymbolsAsync("Demo.", 0, 1000, default);
        Assert.Equal(expected.Items.Select(s => s.FullName).Order(), actual.Items.Select(s => s.FullName).Order());
        Assert.Equal(expected.Total, actual.Total);
    }
}
