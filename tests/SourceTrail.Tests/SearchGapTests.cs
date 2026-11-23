using SourceTrail.Core.Models;
using SourceTrail.Roslyn.Workspace;
using Xunit;
namespace SourceTrail.Tests;

// Regressions found while tracing "검사 결과 저장" (result save) through a legacy solution.
public sealed class SearchGapTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "search-gaps-" + Guid.NewGuid());
    public SearchGapTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task TaskPhraseFindsCompoundScreenTitleAndReportsUnusedWords()
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
                    this.btnClose.Caption = "닫기";
                    this.btnClose.ItemClick += new ItemClickEventHandler(this.btnClose_ItemClick);
                }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(root, "ResultForm.cs"), """
            namespace Lab;
            partial class ResultForm : Form
            {
                private void btnSave_ItemClick(object sender, ItemClickEventArgs e) => fctSave();
                private void btnClose_ItemClick(object sender, ItemClickEventArgs e) => Close();
                private void fctSave() { }
            }
            """);
        using var code = new RoslynAnalyzer(new AnalysisOptions { SolutionPath = root });
        var phrase = await code.FindUiActionsAsync("검사 결과 저장", 5, default);
        var hit = Assert.Single(phrase.Items);
        Assert.Equal("btnSave_ItemClick", hit.Handler);
        Assert.Equal(["검사"], phrase.UnmatchedTerms);
        var joined = await code.FindUiActionsAsync("결과저장", 5, default);
        Assert.Equal("btnSave_ItemClick", Assert.Single(joined.Items).Handler);
        Assert.Null(joined.UnmatchedTerms);
        Assert.Empty((await code.FindUiActionsAsync("결과저장소", 5, default)).Items);
    }

    [Fact]
    public async Task QualifiedMemberFallsBackToBaseTypeDeclaredInAnotherFile()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "BaseCore.Proxy.cs"),
            "namespace M; public partial class BaseCore { protected ResultClient ProxyResultClient { get; } }");
        await File.WriteAllTextAsync(Path.Combine(root, "BaseCore.cs"), "namespace M; public partial class BaseCore { }");
        await File.WriteAllTextAsync(Path.Combine(root, "ResultCore.cs"),
            "namespace M; public class ResultCore : BaseCore, IDisposable { public void Save() { ProxyResultClient.Set(); } public void Dispose() { } }");
        using var code = new RoslynAnalyzer();
        await code.LoadAsync(root, default);
        var result = await code.FindSymbolsAsync("ResultCore.ProxyResultClient", 0, 20, default);
        var member = Assert.Single(result.Items);
        Assert.Equal("M.BaseCore", member.ContainingType);
        Assert.Contains(result.Warnings, warning => warning.Contains("base types (BaseCore"));
        Assert.Empty((await code.FindSymbolsAsync("ResultCore.Missing", 0, 20, default)).Items);
        var overview = await code.OverviewAsync(Path.Combine(root, "ResultCore.cs"), default);
        Assert.Equal(["M.ResultCore : BaseCore, IDisposable"], overview.BaseTypes);
    }

    [Fact]
    public async Task ExactNameHidesPrefixMatchesButKeepsOverloadsAndSubstringFallback()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "Service.cs"), """
            namespace S;
            public class Service
            {
                public void Set_Info(int x) { }
                public void Set_Info(string x) { }
                public void Set_InfoImage() { }
                public void Set_InfoStatus() { }
            }
            """);
        using var code = new RoslynAnalyzer();
        await code.LoadAsync(root, default);
        var exact = await code.FindSymbolsAsync("Set_Info", 0, 20, default);
        Assert.Equal(2, exact.Total);
        Assert.All(exact.Items, item => Assert.Equal("Set_Info", item.Name));
        Assert.Contains(exact.Warnings, warning => warning.Contains("2 substring matches omitted"));
        Assert.Single((await code.FindSymbolsAsync("S.Service.Set_Info(int)", 0, 20, default)).Items);
        Assert.Equal(4, (await code.FindSymbolsAsync("Set_Inf", 0, 20, default)).Total);
    }

    [Fact]
    public async Task AdapterInSharedDesignerFileIsFoundByClassName()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "Service.cs"), """
            namespace S;
            public class Service
            {
                public void SaveImage(object table) { var adapter = new ImageTableAdapter(); adapter.Update(table); }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(root, "DSResult.Designer.cs"), """
            namespace S;
            public partial class ImageTableAdapter
            {
                private void InitAdapter()
                {
                    this._adapter.InsertCommand.CommandText = "dbo.usp_insert_Image";
                    this._adapter.DeleteCommand.CommandText = "dbo.usp_delete_Image";
                    this._adapter.UpdateCommand.CommandText = "UPDATE [dbo].[Image] SET [Data] = @Data " + "WHERE ([Id] = @Original_Id)";
                }
            }
            """);
        using var code = new RoslynAnalyzer();
        await code.LoadAsync(root, default);
        var method = Assert.Single((await code.FindSymbolsAsync("S.Service.SaveImage", 0, 20, default)).Items);
        var result = await code.FindDatabaseCallsAsync(method.Id, 20, default);
        Assert.Contains(result.Calls, call => call.Targets.Contains("dbo.usp_insert_Image") && call.Condition!.Contains("Added"));
        Assert.Contains(result.Calls, call => call.Targets.Contains("dbo.usp_delete_Image") && call.Condition!.Contains("Deleted"));
        Assert.Contains(result.Calls, call => call.Kind == "AdapterSqlCandidate" && call.Targets.Contains("UPDATE dbo.Image") && call.Condition!.Contains("Modified"));
        Assert.Empty(result.Unresolved);
    }

    public void Dispose() => Directory.Delete(root, true);
}
