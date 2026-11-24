using SourceTrail.Core.Contracts;
using SourceTrail.Core.Models;
using SourceTrail.Roslyn.Workspace;
using Xunit;
namespace SourceTrail.Tests;
[CollectionDefinition("Solution", DisableParallelization = true)]
public sealed class SolutionCollection : ICollectionFixture<LoadedSolution> { }
public sealed class LoadedSolution : IAsyncLifetime
{
    public RoslynAnalyzer Code { get; } = new(new AnalysisOptions { ProcedureCallRules = [new("Demo.Data.Db", "ExecuteProcedure", 0)] });
    public string FixturePath { get; } = FindFixture();
    public async Task InitializeAsync() => await Code.LoadAsync(FixturePath, default);
    public Task DisposeAsync() { Code.Dispose(); return Task.CompletedTask; }
    private static string FindFixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SourceTrail.sln"))) directory = directory.Parent;
        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException(), "tests", "Fixtures", "Demo", "Demo.sln");
    }
}
[Collection("Solution")]
public sealed class AnalysisTests(LoadedSolution fixture)
{
    private RoslynAnalyzer Code => fixture.Code;
    [Fact]
    public void SourceIndexNeverClaimsCompilationWasChecked()
    {
        Assert.Equal("Ready", Code.Status().Status);
        Assert.Equal(2, Code.Status().Projects.Count);
        Assert.All(Code.Status().Projects, project => Assert.Null(project.CompilationErrors));
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), assembly => assembly.GetName().Name == "Microsoft.CodeAnalysis.Workspaces.MSBuild");
    }
    [Fact]
    public async Task UiCandidatesWorkWithoutAnyDeclarationIndexOrCompiler()
    {
        using var code = new RoslynAnalyzer(new AnalysisOptions { SolutionPath = fixture.FixturePath });
        var spaced = await code.FindUiActionsAsync("재고 저장 찾아줘", 5, default);
        var joined = await code.FindUiActionsAsync("재고저장", 5, default);
        var english = await code.FindUiActionsAsync("재고 save", 5, default);
        var edit = await code.FindUiActionsAsync("재고 수정", 5, default);
        Assert.Equal("SaveClicked", Assert.Single(spaced.Items).Handler);
        Assert.Equal(spaced.Items.Select(item => item.Handler), joined.Items.Select(item => item.Handler));
        Assert.Equal(spaced.Items.Select(item => item.Handler), english.Items.Select(item => item.Handler));
        Assert.Equal("SaveClicked", edit.Items[0].Handler);
        Assert.Equal("NotLoaded", code.Status().Status);
        Assert.Contains(spaced.Items[0].Evidence, evidence => evidence.Kind == "Handler");
        Assert.Contains(spaced.Items[0].Evidence, evidence => evidence.Kind == "HandlerCall" && evidence.Text == "SaveData");
        Assert.Empty((await code.FindUiActionsAsync("재고저장소", 5, default)).Items);
    }
    [Fact]
    public async Task DeclarationsArePagedAndAmbiguousNamesRequireSourceIds()
    {
        var first = await Code.FindSymbolsAsync("Demo.Data.Repository.Save(", 0, 1, default);
        var second = await Code.FindSymbolsAsync("Demo.Data.Repository.Save(", 1, 1, default);
        Assert.Equal(2, first.Total); Assert.True(first.Truncated);
        Assert.NotEqual(first.Items[0].Id, second.Items[0].Id);
        await Assert.ThrowsAsync<ArgumentException>(() => Code.ReferencesAsync("Demo.Data.Repository.Save", 0, 10, default));
        var references = await Code.ReferencesAsync(first.Items[0].Id, 0, 100, default);
        Assert.NotEmpty(references.Items);
        Assert.All(references.Items, item => Assert.Equal("NameReferenceCandidate", item.RelationType));
        Assert.Contains(references.Warnings, warning => warning.Contains("not resolved"));
    }
    [Fact]
    public async Task OverviewAndDesignerBindingsHaveSourceLocations()
    {
        var file = Path.Combine(Path.GetDirectoryName(fixture.FixturePath)!, "Demo.UI", "DesignerTestForm.cs");
        var overview = await Code.OverviewAsync(file, default);
        Assert.Contains(overview.Methods, method => method.Name == "SaveData");
        var handler = Assert.Single((await Code.FindEventHandlersAsync(file, "save", 0, 10, default)).Items);
        Assert.Equal("SaveClicked", handler.Handler); Assert.Equal("Click", handler.Event);
        Assert.True(handler.HandlerLine > 0); Assert.NotNull(handler.SymbolId);
        Assert.EndsWith(".Designer.cs", handler.BindingFile);
    }
    [Fact]
    public async Task TextSearchPreservesPagingAndSupportsCompoundSuffixes()
    {
        var messages = new List<string>();
        using var code = new RoslynAnalyzer(new AnalysisOptions { SolutionPath = fixture.FixturePath }, messages.Add);
        var result = await code.SearchTextAsync("SaveClicked", 0, 1, default, "Demo.UI", ".Designer.cs");
        Assert.NotEmpty(result.Items); Assert.All(result.Items, item => Assert.EndsWith(".Designer.cs", item.File));
        Assert.Contains(messages, message => message.StartsWith("Text search timing:"));
        Assert.DoesNotContain(messages, message => message.Contains("SaveClicked"));
        foreach (var suffix in new[] { "*.cs", ".exe", "../.cs", ".foo/Bar.cs" })
            await Assert.ThrowsAsync<ArgumentException>(() => code.SearchTextAsync("Save", 0, 10, default, null, suffix));
        await Assert.ThrowsAsync<ArgumentException>(() => code.SearchTextAsync("Save", 0, 10, default, "..", ".cs"));
    }
    [Fact]
    public async Task ProcedureLiteralsAreCandidatesAndCommentsAreExcluded()
    {
        var usage = await Code.ProcedureUsagesAsync("dbo.usp_TestSave", 0, 100, default);
        Assert.NotEmpty(usage.Items); Assert.All(usage.Items, item => Assert.False(item.IsVerifiedCall));
        Assert.Empty((await Code.ProcedureUsagesAsync("dbo.usp_CommentOnly", 0, 100, default)).Items);
    }
    [Fact]
    public async Task DbSearchReportsCurrentMethodConfigurationsAndNextKeywordsOnly()
    {
        var result = await Code.FindDatabaseCallsAsync("Demo.Data.CollectionCalls.Save", 50, default);
        Assert.Equal("Candidates", result.Status);
        Assert.Contains(result.Calls, call => call.Targets.Contains("dbo.usp_CollectionInsert"));
        Assert.Contains(result.Calls, call => call.Targets.Contains("dbo.usp_CollectionUpdate"));
        // Unused configurations are intentionally candidates, never upgraded to execution proof.
        Assert.Contains(result.Calls, call => call.Targets.Contains("dbo.usp_UnusedCollection") && call.Execution == "CommandConfiguration");
        Assert.All(result.Calls, call => Assert.Contains("check", call.Condition!, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.MethodCandidates, method => method.Name == "ExecuteBatch");
        Assert.DoesNotContain(result.MethodCandidates, method => method.Name == "ExecuteNonQuery");
        var dynamic = await Code.FindDatabaseCallsAsync("Demo.Data.CollectionCalls.Dynamic", 20, default);
        Assert.Empty(dynamic.Calls); Assert.NotEmpty(dynamic.Unresolved);
        var cycles = await Code.FindDatabaseCallsAsync("Demo.Data.Repository.CycleA", 20, default);
        Assert.Equal("CycleB", Assert.Single(cycles.MethodCandidates).Name);
        var limited = await Code.FindDatabaseCallsAsync("Demo.Data.CollectionCalls.Save", 1, default);
        Assert.True(limited.Truncated); Assert.Single(limited.Calls);
    }
    [Fact]
    public async Task AdapterConfigurationsAreCandidatesWithRowStateEvidence()
    {
        var result = await Code.FindDatabaseCallsAsync("Demo.Data.DatabaseCalls.Save", 50, default);
        Assert.Contains(result.Calls, call => call.Kind == "AdapterCommandCandidate" && call.Targets.Contains("dbo.usp_SaveFixture") && call.Condition!.Contains("Modified"));
        Assert.Contains(result.Calls, call => call.Targets.Contains("dbo.usp_ClearFixture"));
        Assert.NotEmpty(result.Unresolved);
    }
    [Fact]
    public async Task InvalidSourceRootDoesNotReplacePublishedIndex()
    {
        var snapshot = Code.Status().SnapshotId;
        await Assert.ThrowsAsync<ArgumentException>(() => Code.LoadAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sln"), default));
        Assert.Equal(snapshot, Code.Status().SnapshotId);
    }
}