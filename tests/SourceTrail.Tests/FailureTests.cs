using SourceTrail.Roslyn.Workspace;
using SourceTrail.Core.Models;
using SourceTrail.SqlServer.Metadata;
using Xunit;
namespace SourceTrail.Tests;
[Collection("Solution")]
public sealed class FailureTests(LoadedSolution fixture)
{
    [Fact]
    public async Task MissingReferenceTypesDoNotPreventSourceSearch()
    {
        using var code = new RoslynAnalyzer();
        var root = Directory.GetParent(Path.GetDirectoryName(fixture.FixturePath)!)!.FullName;
        var state = await code.LoadAsync(Path.Combine(root, "Broken", "Broken.sln"), default);
        Assert.Equal("Ready", state.Status);
        Assert.All(state.Projects, project => Assert.Null(project.CompilationErrors));
        Assert.NotEmpty((await code.FindSymbolsAsync("BrokenClass", 0, 20, default)).Items);
    }
    [Fact]
    public void LiveDatabaseIsRejectedWithoutAttemptingConnection()
    {
        Assert.Throws<ArgumentException>(() => new DatabaseAnalyzer(new DatabaseOptions { Mode = "LiveDatabase" }));
    }
}