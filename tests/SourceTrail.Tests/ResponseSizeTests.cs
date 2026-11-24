using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SourceTrail.Core.Models;
using SourceTrail.Mcp.Tools;
using SourceTrail.Roslyn.Workspace;
using SourceTrail.SqlServer.Metadata;
using Xunit;
namespace SourceTrail.Tests;

// 3.3.0: smaller tool responses without changing which declarations, screens or objects are found.
public sealed class ResponseSizeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "response-size-" + Guid.NewGuid().ToString("N"));
    public ResponseSizeTests() => Directory.CreateDirectory(root);
    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    [Fact]
    public async Task ResultWarningsOmitSyntaxDiagnosticsButStatusKeepsThem()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "Good.cs"), "namespace Lab; class Good { void Run() { } }");
        await File.WriteAllTextAsync(Path.Combine(root, "Broken.cs"), "namespace Lab; class Broken { void Run( { } ");
        using var code = new RoslynAnalyzer(new AnalysisOptions { SolutionPath = root });
        await code.LoadAsync(root, default);
        var page = await code.FindSymbolsAsync("Run", 0, 20, default);
        Assert.Single(page.Warnings);
        Assert.StartsWith("Text/syntax candidates only", page.Warnings[0]);
        Assert.NotEmpty(code.Status().Diagnostics);
    }

    [Fact]
    public async Task OverviewIsBriefByDefaultAndDetailKeepsFullRecords()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "Result.cs"), "namespace Lab; class Result { int count; public int Count { get; set; } public void Save(int id) { } }");
        using var code = new RoslynAnalyzer(new AnalysisOptions { SolutionPath = root });
        await code.LoadAsync(root, default);
        var tool = new AnalysisTools(code, NullLogger<AnalysisTools>.Instance, new AnalysisOptions { SolutionPath = root },
            new SemaphoreSlim(1, 1), new SourceTrail.Mcp.ServerOptions());
        var file = Path.Combine(root, "Result.cs");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var brief = JsonSerializer.SerializeToElement(await tool.GetSymbolOverview(file, default), options);
        var full = JsonSerializer.SerializeToElement(await tool.GetSymbolOverview(file, default, detail: true), options);
        var briefMethod = brief.GetProperty("methods")[0];
        var fullMethod = full.GetProperty("methods")[0];
        Assert.Equal("Save", briefMethod.GetProperty("name").GetString());
        foreach (var name in new[] { "id", "name", "symbolKind", "line", "signature" })
            Assert.True(JsonElement.DeepEquals(fullMethod.GetProperty(name), briefMethod.GetProperty(name)), name);
        foreach (var name in new[] { "namespace", "fullName", "project", "containingType", "isUi", "file" })
        {
            Assert.True(fullMethod.TryGetProperty(name, out _), name);
            Assert.False(briefMethod.TryGetProperty(name, out _), name);
        }
        Assert.Equal(file, brief.GetProperty("file").GetString());
        Assert.Equal(full.GetProperty("properties").GetArrayLength(), brief.GetProperty("properties").GetArrayLength());
        Assert.Equal(full.GetProperty("fields")[0].GetProperty("name").GetString(), brief.GetProperty("fields")[0].GetProperty("name").GetString());
        Assert.True(brief.GetRawText().Length < full.GetRawText().Length);
    }

    [Fact]
    public async Task SqlObjectSearchPrefersExactNameAndFallsBackToSubstring()
    {
        Directory.CreateDirectory(Path.Combine(root, "scripts"));
        File.WriteAllText(Path.Combine(root, "scripts", "a.sql"), "CREATE PROCEDURE dbo.usp_update_Info AS SELECT 1;\nGO\nCREATE PROCEDURE dbo.usp_update_InfoImage AS SELECT 2;\nGO\nCREATE PROCEDURE dbo.usp_update_InfoStatus AS SELECT 3;");
        using var db = new DatabaseAnalyzer(new DatabaseOptions { Mode = "SqlFiles", SqlFolder = Path.Combine(root, "scripts"), CacheDirectory = Path.Combine(root, "cache"), WatchFiles = false });
        await db.SelectAsync("SqlFiles", Path.Combine(root, "scripts"), default);
        foreach (var query in new[] { "usp_update_Info", "dbo.usp_update_info" })
        {
            var exact = await db.FindAsync(query, 0, 20, false, default);
            Assert.Equal("usp_update_Info", Assert.Single(exact.Items).Name);
            Assert.Contains(exact.Warnings, warning => warning.Contains("2 substring matches omitted"));
            Assert.Null(exact.Items[0].Definition);
        }
        var none = await db.FindAsync("usp_update_Inf", 0, 20, false, default);
        Assert.Equal(3, none.Items.Count);
        Assert.DoesNotContain(none.Warnings, warning => warning.Contains("substring matches omitted"));
        var other = await db.FindAsync("other.usp_update_Info", 0, 20, false, default);
        Assert.Empty(other.Items);
        var definition = await db.FindAsync("usp_update_Info", 0, 20, true, default);
        Assert.Contains("SELECT 1", definition.Items[0].Definition);
    }

    [Fact]
    public async Task UiActionEvidenceIsLimitedKeepsHandlerAndStatesFileOnce()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "ResultForm.Designer.cs"), """
            namespace Lab;
            partial class ResultForm
            {
                private void InitializeComponent()
                {
                    this.Text = "결과입력";
                    this.btnSave.Caption = "저장";
                    this.btnSave.ItemClick += new ItemClickEventHandler(this.btnSave_ItemClick);
                }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(root, "ResultForm.cs"), """
            namespace Lab;
            partial class ResultForm
            {
                private void btnSave_ItemClick(object sender, ItemClickEventArgs e) { fctSave(); }
                private void fctSave() { }
            }
            class Shell { void Menu() { var item = new MenuItem(OpenResult) { Caption = "결과입력" }; } void OpenResult() { var form = new ResultForm(); } }
            """);
        using var code = new RoslynAnalyzer(new AnalysisOptions { SolutionPath = root });
        var result = await code.FindUiActionsAsync("결과입력 저장", 5, default);
        var candidate = Assert.Single(result.Items);
        Assert.InRange(candidate.Evidence.Count, 1, 3);
        Assert.Contains(candidate.Evidence, item => item.Kind == "Handler");
        Assert.Contains(candidate.Evidence, item => item.Kind == "HandlerCall" && item.Text == "fctSave");
        Assert.EndsWith("ResultForm.cs", candidate.File);
        Assert.All(candidate.Evidence, item => Assert.True(item.File is null || item.File != candidate.File));
        var json = JsonSerializer.Serialize(candidate);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(json, "ResultForm.cs\""));
    }
}
