using SourceTrail.Roslyn.Workspace;
using Xunit;
namespace SourceTrail.Tests;

public sealed class LazySourceIndexTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "lazy-source-" + Guid.NewGuid());
    public LazySourceIndexTests() => Directory.CreateDirectory(root);
    [Fact]
    public async Task QualifiedSearchParsesOnlyPreferredFilesAndReportsItsScope()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "Wanted.cs"), "namespace Fast; partial class Wanted { void Save() {} }");
        await File.WriteAllTextAsync(Path.Combine(root, "Extra.cs"), "namespace Fast; partial class Wanted { void Save(int x) {} }");
        await File.WriteAllTextAsync(Path.Combine(root, "Bad.cs"), "class Bad { void Broken( }");
        using var code = new RoslynAnalyzer();
        int parsed = 0; code.SymbolDocumentIndexed = () => parsed++;
        await code.LoadAsync(root, default); Assert.Equal(0, parsed);
        var narrowed = await code.FindSymbolsAsync("Fast.Wanted.Save", 0, 20, default);
        Assert.Single(narrowed.Items); Assert.Equal(1, parsed);
        Assert.Contains(narrowed.Warnings, warning => warning.Contains("Filename-prioritized"));
        Assert.Equal("Ready", code.Status().Status);
        var broader = await code.FindSymbolsAsync("Save(", 0, 20, default);
        Assert.Equal(2, broader.Total); Assert.Equal(2, parsed);
        await code.FindSymbolsAsync("Bad", 0, 20, default);
        Assert.Equal("Partial", code.Status().Status);
    }
    [Fact]
    public async Task MissingFilenameHintFallsBackToSourceContent()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "Unrelated.cs"), "namespace Fast; class Wanted { void Save() {} }");
        using var code = new RoslynAnalyzer(); await code.LoadAsync(root, default);
        var result = await code.FindSymbolsAsync("Fast.Wanted.Save", 0, 20, default);
        Assert.Single(result.Items);
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("Filename-prioritized"));
    }
    [Fact]
    public async Task OverviewIdsCanBeUsedImmediatelyWithoutParsingTheWholeFolder()
    {
        string file = Path.Combine(root, "Wanted.cs");
        await File.WriteAllTextAsync(file, "namespace Fast; class Wanted { void Save() { Unknown.Send(); } }");
        using var code = new RoslynAnalyzer(new() { SolutionPath = root });
        var view = await code.OverviewAsync(file, default);
        var method = Assert.Single(view.Methods);
        var calls = await code.FindDatabaseCallsAsync(method.Id, 20, default);
        Assert.Equal("Send", Assert.Single(calls.MethodCandidates).Name);
    }
    public void Dispose() => Directory.Delete(root, true);
}
