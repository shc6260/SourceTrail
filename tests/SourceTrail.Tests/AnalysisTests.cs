using SourceTrail.Core.Contracts;
using SourceTrail.Core.Flow;
using SourceTrail.Core.Models;
using SourceTrail.Roslyn.Workspace;
using SourceTrail.SqlServer.Metadata;
using Xunit;

namespace SourceTrail.Tests;

[CollectionDefinition("Solution", DisableParallelization = true)]
public sealed class SolutionCollection : ICollectionFixture<LoadedSolution> { }

public sealed class LoadedSolution : IAsyncLifetime
{
    public RoslynAnalyzer Code { get; } = new(new AnalysisOptions
    {
        ProcedureCallRules = [new("Demo.Data.Db", "ExecuteProcedure", 0)]
    });
    public string FixturePath { get; } = FindFixture();
    public async Task InitializeAsync() => await Code.LoadAsync(FixturePath, CancellationToken.None);
    public Task DisposeAsync() { Code.Dispose(); return Task.CompletedTask; }
    private static string FindFixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SourceTrail.sln")))
            directory = directory.Parent;
        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Repository root not found."),
            "tests", "Fixtures", "Demo", "Demo.sln");
    }
}

[Collection("Solution")]
public sealed class AnalysisTests(LoadedSolution fixture)
{
    private readonly RoslynAnalyzer _code = fixture.Code;
    private static readonly CancellationToken Token = CancellationToken.None;
    private async Task<SymbolInfo> Find(string fullName)
    {
        var result = await _code.FindSymbolsAsync(fullName, 0, 100, Token);
        return Assert.Single(result.Items, s => s.FullName == fullName);
    }

    [Fact]
    public void LoadsAllProjectsAndReportsSnapshot()
    {
        var status = _code.Status();
        Assert.Equal("Ready", status.Status);
        Assert.Equal(2, status.Projects.Count);
        Assert.NotNull(status.SnapshotId);
        Assert.All(status.Projects, p => Assert.Equal(0, p.CompilationErrors));
    }

    [Fact]
    public async Task SymbolSearchReturnsUniqueOverloadIdsAndPaging()
    {
        var result = await _code.FindSymbolsAsync("Demo.Data.Repository.Save(", 0, 1, Token);
        Assert.Equal(2, result.Total);
        Assert.True(result.Truncated);
        var next = await _code.FindSymbolsAsync("Demo.Data.Repository.Save(", 1, 1, Token);
        Assert.NotEqual(result.Items[0].Id, next.Items[0].Id);
        Assert.NotNull(result.Items[0].File);
        Assert.True(result.Items[0].Line > 0);
    }

    [Fact]
    public async Task AmbiguousNameRequiresAnExactId()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _code.ReferencesAsync("Save", 0, 100, Token));
    }

    [Fact]
    public async Task OverviewSeparatesMembersAndMarksWinFormsType()
    {
        var type = await Find("Demo.UI.TestForm");
        var result = await _code.OverviewAsync(type.Id, Token);
        Assert.Contains(result.Types, s => s.IsUi);
        Assert.Single(result.Constructors);
        Assert.Contains(result.Fields, s => s.Name == "_button");
        Assert.Contains(result.Properties, s => s.Name == "Caption");
        Assert.Contains(result.Methods, s => s.Name == "SaveClicked");
    }

    [Fact]
    public async Task ReferencesIncludeActualCallersAndMethodGroupReferences()
    {
        var symbol = await Find("Demo.Data.Repository.Save()");
        var result = await _code.ReferencesAsync(symbol.Id, 0, 100, Token);
        Assert.Contains(result.Items, r => r.ReferencingSymbol?.FullName == "Demo.UI.Service.Save()");
        Assert.Contains(result.Items, r => r.ReferencingSymbol?.FullName == "Demo.Data.Repository.DelegateOnly()");
    }

    [Fact]
    public async Task IncomingGraphDoesNotTreatDelegateAssignmentAsInvocation()
    {
        var symbol = await Find("Demo.Data.Repository.Save()");
        var result = await _code.ExpandAsync(symbol.Id, true, Token);
        Assert.DoesNotContain(result.Relations, r => r.From.FullName == "Demo.Data.Repository.DelegateOnly()");
        Assert.Contains(result.Relations, r => r.From.FullName == "Demo.UI.Service.Save()");
    }

    [Fact]
    public async Task ProcedureUsageResolvesConstantAndContainingSymbol()
    {
        var result = await _code.ProcedureUsagesAsync("dbo.usp_TestSave", 0, 100, Token);
        var usage = Assert.Single(result.Items, u => u.IsVerifiedCall);
        Assert.Equal("Demo.Data.Repository.Save()", usage.ContainingSymbol?.FullName);
        Assert.Equal("ConfiguredProcedureCall", usage.RelationType);
    }

    [Fact]
    public async Task CommentsAreTextMatchesButNotProcedureUsage()
    {
        var usages = await _code.ProcedureUsagesAsync("dbo.usp_CommentOnly", 0, 100, Token);
        Assert.Empty(usages.Items);
        var text = await _code.SearchTextAsync("dbo.usp_CommentOnly", 0, 100, Token);
        Assert.NotEmpty(text.Items);
        Assert.DoesNotContain(text.Items, t => t.File.Contains("\\obj\\") || t.File.Contains("\\bin\\"));
    }

    [Fact]
    public async Task UnregisteredStringIsOnlyACandidate()
    {
        var usages = await _code.ProcedureUsagesAsync("dbo.usp_NotCalled", 0, 100, Token);
        Assert.NotEmpty(usages.Items);
        Assert.All(usages.Items, u => Assert.False(u.IsVerifiedCall));
        var flow = await new FlowAnalyzer(_code, new FakeDatabase()).TraceProcedureAsync("dbo.usp_NotCalled", 5, 200, Token);
        Assert.Single(flow.Nodes);
        Assert.Equal("Partial", flow.Status);
    }

    [Fact]
    public async Task ForwardTraceIncludesFormServiceRepositoryProcedureAndMockTable()
    {
        var symbol = await Find("Demo.UI.TestForm.SaveClicked(object?, System.EventArgs)");
        var result = await new FlowAnalyzer(_code, new FakeDatabase()).TraceCodeAsync(symbol.Id, 8, 200, Token);
        Assert.Contains(result.Nodes, n => n.Kind == "Procedure" && n.Name == "dbo.usp_TestSave");
        Assert.Contains(result.Nodes, n => n.Name == "DemoTable");
        Assert.Contains(result.Nodes, n => n.Name == "Demo.Data.Repository.Save()");
        Assert.Contains(result.Edges, e => e.RelationType == "DispatchCandidate");
        Assert.Equal("Partial", result.Status); // Interface dispatch is explicitly a candidate.
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task ReverseTraceIncludesUiAndEventSubscription()
    {
        var result = await new FlowAnalyzer(_code, new FakeDatabase()).TraceProcedureAsync("dbo.usp_TestSave", 8, 200, Token);
        Assert.Contains(result.Nodes, n => n.Kind.StartsWith("Ui"));
        Assert.Contains(result.Edges, e => e.RelationType == "EventSubscription");
    }

    [Fact]
    public async Task CyclesTerminateAndLimitsAreReported()
    {
        var symbol = await Find("Demo.Data.Repository.CycleA()");
        var flow = new FlowAnalyzer(_code, new FakeDatabase());
        var result = await flow.TraceCodeAsync(symbol.Id, 5, 200, Token);
        Assert.Equal(2, result.Nodes.Count);
        Assert.Equal(2, result.Edges.Count);
        Assert.False(result.Truncated);
        var limited = await flow.TraceCodeAsync(symbol.Id, 0, 200, Token);
        Assert.Single(limited.Nodes);
        Assert.True(limited.Truncated);
        var nodesLimited = await flow.TraceCodeAsync(symbol.Id, 5, 1, Token);
        Assert.Single(nodesLimited.Nodes);
        Assert.True(nodesLimited.Truncated);
        Assert.All(nodesLimited.Edges, e => Assert.Contains(nodesLimited.Nodes, n => n.Id == e.To));
    }

    [Fact]
    public async Task DbOptionalAndSourceTraceStillWorks()
    {
        var sql = new SqlProcedureAnalyzer();
        var metadata = await sql.AnalyzeAsync("dbo.usp_TestSave", false, Token);
        Assert.Equal("NotConfigured", metadata.Status);
        var symbol = await Find("Demo.Data.Repository.Save()");
        var flow = await new FlowAnalyzer(_code, sql).TraceCodeAsync(symbol.Id, 5, 200, Token);
        Assert.Contains(flow.Nodes, n => n.Kind == "Procedure");
        Assert.Equal("Partial", flow.Status);
    }

    [Fact]
    public async Task MissingSolutionPreservesExistingSnapshot()
    {
        string? snapshot = _code.Status().SnapshotId;
        await Assert.ThrowsAsync<ArgumentException>(() => _code.LoadAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sln"), Token));
        Assert.Equal(snapshot, _code.Status().SnapshotId);
    }

    [Fact]
    public async Task ReloadInvalidatesSymbolIdsAndCaches()
    {
        var oldSymbol = await Find("Demo.Data.Repository.Save()");
        var snapshot = _code.Status().SnapshotId;
        await _code.ReloadAsync(Token);
        Assert.NotEqual(snapshot, _code.Status().SnapshotId);
        await Assert.ThrowsAsync<ArgumentException>(() => _code.ReferencesAsync(oldSymbol.Id, 0, 100, Token));
        var fresh = await Find("Demo.Data.Repository.Save()");
        Assert.NotEqual(oldSymbol.Id, fresh.Id);
    }

    private sealed class FakeDatabase : IProcedureAnalyzer
    {
        public Task<ProcedureAnalysis> AnalyzeAsync(string procedure, bool includeDefinition, CancellationToken cancellationToken) =>
            Task.FromResult(new ProcedureAnalysis("Ready", procedure, "dbo", "Fixture", null,
                [new SqlObject(null, "Fixture", "dbo", "DemoTable", "USER_TABLE", "Unknown", true, false, false)],
                ["SQL dependencies in this test are mocked, not live database validation."]));
    }
}
