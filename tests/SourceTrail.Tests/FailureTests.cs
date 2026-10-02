using SourceTrail.Core.Models;
using SourceTrail.Roslyn.Workspace;
using SourceTrail.SqlServer.Metadata;
using Xunit;

namespace SourceTrail.Tests;

[Collection("Solution")]
public sealed class FailureTests(LoadedSolution fixture)
{
    [Fact]
    public async Task CompilationErrorsAreReportedAsPartial()
    {
        using var analyzer = new RoslynAnalyzer();
        var fixtureRoot = Directory.GetParent(Path.GetDirectoryName(fixture.FixturePath)!)!.FullName;
        var state = await analyzer.LoadAsync(Path.Combine(fixtureRoot, "Broken", "Broken.sln"), CancellationToken.None);
        Assert.Equal("Partial", state.Status);
        Assert.Contains(state.Projects, p => p.CompilationErrors > 0);
        Assert.Contains(state.Diagnostics, d => d.Severity == "Error");
        var result = await analyzer.FindSymbolsAsync("BrokenClass", 0, 100, CancellationToken.None);
        Assert.NotEmpty(result.Items);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public async Task DatabaseFailureReturnsStatusWithoutCredentials()
    {
        var sql = new SqlProcedureAnalyzer(new DatabaseOptions
        {
            ConnectionString = "Server=127.0.0.1,1;Database=Fixture;User ID=Fixture;Password=SecretFixture;Encrypt=false;Connect Timeout=1;ConnectRetryCount=0",
            CommandTimeoutSeconds = 1
        });
        var result = await sql.AnalyzeAsync("dbo.usp_TestSave", false, CancellationToken.None);
        Assert.Equal("Unavailable", result.Status);
        Assert.Empty(result.Dependencies);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("SecretFixture") || w.Contains("127.0.0.1"));
    }
}
