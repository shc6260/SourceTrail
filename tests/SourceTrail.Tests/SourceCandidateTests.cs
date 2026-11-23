using SourceTrail.Roslyn.Workspace;
using Xunit;

namespace SourceTrail.Tests;

public sealed class SourceCandidateTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "st-candidates");

    private static string At(params string[] parts) => Path.Combine([Root, .. parts]);

    [Fact]
    public void SkipsOnlyUnselectedCsFilesInsideTheSearchedRoot()
    {
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { At("A", "Hit.cs") };
        var candidates = new RoslynAnalyzer.SourceCandidates(Root, selected);
        Assert.False(candidates.Skip(At("A", "Hit.cs")));
        Assert.True(candidates.Skip(At("A", "Miss.cs")));
        Assert.True(candidates.Skip(At("A", "Form.Designer.cs")));
        // ripgrep never searched these, so they must still be read by the managed scan.
        Assert.False(candidates.Skip(At("A", "obj", "Generated.cs")));
        Assert.False(candidates.Skip(At("B", "bin", "Debug", "X.cs")));
        Assert.False(candidates.Skip(Path.Combine(Path.GetTempPath(), "elsewhere", "Linked.cs")));
        Assert.False(candidates.Skip(At("A", "Script.sql")));
    }

    [Fact]
    public void WithoutRipgrepEveryFileIsRead()
    {
        Assert.False(RoslynAnalyzer.SourceCandidates.All.Skip(At("A", "Miss.cs")));
        Assert.False(new RoslynAnalyzer.SourceCandidates(Root, null).Skip(At("A", "Miss.cs")));
    }
}
