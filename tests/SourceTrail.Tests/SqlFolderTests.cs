using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using SourceTrail.Core.Models;
using SourceTrail.Core.Contracts;
using SourceTrail.Mcp.Tools;
using SourceTrail.SqlServer.Metadata;
namespace SourceTrail.Tests;
public sealed class SqlFolderTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SourceTrail-tests-" + Guid.NewGuid().ToString("N"));
    private string Scripts => Path.Combine(root, "scripts");
    private DatabaseOptions Options => new() { Mode = "SqlFiles", SqlFolder = Scripts, CacheDirectory = Path.Combine(root, "cache"), WatchFiles = false };
    public SqlFolderTests() => Directory.CreateDirectory(Scripts);
    private void Write(string name, string sql) => File.WriteAllText(Path.Combine(Scripts, name), sql);
    [Fact]
    public async Task ParsesActualDefinitionsAndWriteRelationsInMultiObjectFile()
    {
        Write("arbitrary.sql", "USE Demo;\nGO\nCREATE TABLE dbo.Reception(Id int NOT NULL, Name nvarchar(50) NULL);\nGO\nCREATE PROCEDURE dbo.usp_Save @Id int AS INSERT dbo.Reception(Id) VALUES(@Id);\nGO\nCREATE VIEW dbo.V AS SELECT Id FROM dbo.Reception;");
        using var db = new DatabaseAnalyzer(Options);
        var state = await db.SelectAsync("SqlFiles", Scripts, default);
        Assert.True(state.Objects == 3, string.Join(" | ", state.Warnings));
        var procedure = await db.AnalyzeAsync("dbo.usp_Save", false, default);
        Assert.Equal("SqlFiles", procedure.Source);
        Assert.Equal("Ready", procedure.Status);
        Assert.Equal("Ready", procedure.IndexStatus);
        Assert.Equal(0, procedure.IndexWarningCount);
        Assert.Null(procedure.Definition);
        Assert.Contains(procedure.Dependencies, d => d.Name == "Reception" && d.Access == "Insert" && d.Resolved);
        var objects = await db.FindAsync("Reception", 0, 100, false, default);
        Assert.Equal(2, objects.Items.Single().Members.Count);
        Assert.False(objects.Items.Single().Members[0].Nullable);
        Assert.Null(objects.Items.Single().Definition);
        Assert.Equal("Ready", objects.IndexStatus);
        Assert.Equal(0, objects.IndexWarningCount);
        Assert.Empty(objects.Warnings);
    }
    [Fact]
    public async Task ReusesDiskCacheAndReconcilesAddedModifiedDeletedFiles()
    {
        Write("a.sql", "CREATE TABLE dbo.A(Id int);");
        using (var db = new DatabaseAnalyzer(Options))
            Assert.Equal(1, (await db.SelectAsync("SqlFiles", Scripts, default)).ParsedFiles);
        using var reopened = new DatabaseAnalyzer(Options);
        var cached = await reopened.SelectAsync("SqlFiles", Scripts, default);
        Assert.Equal(1, cached.ReusedFiles); Assert.Equal(0, cached.ParsedFiles);
        Write("a.sql", "CREATE TABLE dbo.A(Id bigint);");
        Write("b.sql", "CREATE PROCEDURE dbo.B AS SELECT Id FROM dbo.A;");
        var updated = await reopened.RefreshAsync(default);
        Assert.Equal(2, updated.ParsedFiles);
        File.Delete(Path.Combine(Scripts, "a.sql"));
        await reopened.RefreshAsync(default);
        Assert.Empty((await reopened.FindAsync("A", 0, 100, false, default)).Items);
        Assert.Equal("Partial", (await reopened.AnalyzeAsync("dbo.B", false, default)).Status);
    }
    [Fact]
    public async Task ReportsDuplicatesDynamicSqlAndAbsenceWithoutFallback()
    {
        Write("a.sql", "CREATE PROCEDURE dbo.A AS EXEC(N'SELECT 1');");
        using var db = new DatabaseAnalyzer(Options);
        await db.SelectAsync("SqlFiles", Scripts, default);
        Assert.Contains((await db.AnalyzeAsync("dbo.A", false, default)).Warnings, w => w.Contains("Dynamic"));
        Write("duplicate.sql", "CREATE PROCEDURE dbo.A AS SELECT 1;");
        await db.RefreshAsync(default);
        var ambiguous = await db.AnalyzeAsync("dbo.A", false, default);
        Assert.Equal("Ambiguous", ambiguous.Status);
        Assert.Equal("Partial", ambiguous.IndexStatus);
        Assert.Contains(ambiguous.Warnings, w => w.Contains("Multiple definitions"));
        var missing = await db.AnalyzeAsync("Missing", false, default);
        Assert.Equal("NotFoundInProvidedFiles", missing.Status);
        Assert.Equal(db.Status().SnapshotId, missing.SnapshotId);
        Assert.Equal(db.Status().Warnings.Count, missing.IndexWarningCount);
        await Assert.ThrowsAsync<ArgumentException>(() => db.SelectAsync("LiveDatabase", null, default));
        await Assert.ThrowsAsync<ArgumentException>(() => db.SelectAsync("SqlFiles", Path.Combine(root, "absent"), default));
        Assert.Equal("SqlFiles", db.Status().Mode);
    }
    [Fact]
    public async Task CorruptCacheRecoversAndUtf16BomWorks()
    {
        File.WriteAllText(Path.Combine(Scripts, "unicode.sql"), "CREATE TABLE dbo.[접수](Id int);", System.Text.Encoding.Unicode);
        using (var db = new DatabaseAnalyzer(Options)) await db.SelectAsync("SqlFiles", Scripts, default);
        File.WriteAllText(Directory.GetFiles(Options.CacheDirectory).Single(), "broken");
        using var reopened = new DatabaseAnalyzer(Options);
        var state = await reopened.SelectAsync("SqlFiles", Scripts, default);
        Assert.Equal(1, state.ParsedFiles);
        Assert.Contains(state.Warnings, w => w.Contains("corrupt"));
        Assert.Single((await reopened.FindAsync("접수", 0, 100, false, default)).Items);
    }
    [Fact]
    public async Task ParseErrorsAreVisibleAndNestedFoldersAreLoaded()
    {
        Directory.CreateDirectory(Path.Combine(Scripts, "nested"));
        Write("nested/a.sql", "CREATE TABLE dbo.A(Id int);");
        Write("bad.sql", "CREATE PROCEDURE !!!");
        using var db = new DatabaseAnalyzer(Options);
        var state = await db.SelectAsync("SqlFiles", Scripts, default);
        Assert.Equal("Partial", state.Status); Assert.NotEmpty(state.Warnings);
        Assert.Single((await db.FindAsync("A", 0, 100, false, default)).Items);
    }
    [Fact]
    public async Task SearchSummarizesIndexWarningsAndKeepsRelevantDiagnostics()
    {
        Write("searched.sql", "CREATE PROCEDURE dbo.P AS EXEC(N'SELECT 1');\nGO\nCREATE PROCEDURE !!!");
        Write("duplicate.sql", "CREATE PROCEDURE dbo.P AS SELECT 1;");
        Write("unrelated.sql", "CREATE PROCEDURE !!!");
        using var db = new DatabaseAnalyzer(Options);
        await db.SelectAsync("SqlFiles", Scripts, default);

        var result = await db.FindAsync("dbo.P", 0, 2, false, default);
        var status = db.Status();
        Assert.Equal("Partial", result.IndexStatus);
        Assert.Equal(status.SnapshotId, result.SnapshotId);
        Assert.Equal(status.Warnings.Count, result.IndexWarningCount);
        Assert.Contains(status.Warnings, w => w.Contains("unrelated.sql"));
        Assert.DoesNotContain(result.Warnings, w => w.Contains("unrelated.sql"));
        Assert.Contains(result.Warnings, w => w.Contains("searched.sql"));
        Assert.Contains(result.Items.Single(o => o.File.EndsWith("searched.sql")).Warnings, w => w.Contains("Dynamic"));

        var firstPage = await db.FindAsync("dbo.P", 0, 1, false, default);
        Assert.Single(firstPage.Items);
        Assert.True(firstPage.Truncated);
        Assert.Contains(firstPage.Warnings, w => w.StartsWith("Duplicate definitions:"));

        var missing = await db.FindAsync("missing", 0, 1, false, default);
        Assert.Empty(missing.Items);
        Assert.Empty(missing.Warnings);
        Assert.Equal("Partial", missing.IndexStatus);
        Assert.Equal(status.Warnings.Count, missing.IndexWarningCount);
    }
    [Fact]
    public async Task SqlLocationOptionKeepsCandidatesAndWarningsInToolResponse()
    {
        Write("one.sql", "CREATE PROCEDURE dbo.P @Id int AS EXEC(N'SELECT 1');");
        Write("two.sql", "CREATE PROCEDURE dbo.P AS SELECT 2;");
        using var db = new DatabaseAnalyzer(Options);
        using var gate = new SemaphoreSlim(1, 1);
        var tool = new DatabaseTools(db, NullLogger<DatabaseTools>.Instance, new SourceTrail.Mcp.ServerOptions());
        const string query = "dbo.P";
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var detailed = JsonSerializer.SerializeToElement(await tool.FindSqlObject(query, default, limit: 1), options);
        var locations = JsonSerializer.SerializeToElement(await tool.FindSqlObject(query, default, limit: 1, locationsOnly: true), options);
        var item = locations.GetProperty("items")[0];
        var fullItem = detailed.GetProperty("items")[0];
        foreach (var name in new[] { "database", "schema", "name", "kind", "file", "line", "warnings" })
            Assert.True(JsonElement.DeepEquals(fullItem.GetProperty(name), item.GetProperty(name)), name);
        foreach (var name in new[] { "total", "offset", "limit", "truncated", "warnings", "indexStatus", "indexWarningCount" })
            Assert.True(JsonElement.DeepEquals(detailed.GetProperty(name), locations.GetProperty(name)), name);
        foreach (var name in new[] { "members", "references", "constraints" })
        {
            Assert.True(fullItem.TryGetProperty(name, out _), name);
            Assert.False(item.TryGetProperty(name, out _), name);
        }
        Assert.False(fullItem.TryGetProperty("definition", out _)); // empty definitions are omitted
        Assert.False(item.TryGetProperty("definition", out _));
        Assert.True(locations.GetProperty("truncated").GetBoolean());
        Assert.Contains("Duplicate definitions", locations.GetProperty("warnings")[0].GetString());
    }
    [Fact]
    public async Task ProcedureAnalysisSummarizesIndexWarningsAndKeepsRelevantDiagnostics()
    {
        Write("target.sql", "CREATE PROCEDURE dbo.P AS BEGIN EXEC(N'SELECT 1'); SELECT Id FROM dbo.Missing; END;\nGO\nCREATE PROCEDURE !!!");
        Write("unrelated.sql", "CREATE PROCEDURE !!!");
        using var db = new DatabaseAnalyzer(Options);

        var result = await db.AnalyzeAsync("dbo.P", true, default);
        var status = db.Status();
        Assert.Equal("Partial", result.Status);
        Assert.Equal(status.Status, result.IndexStatus);
        Assert.Equal(status.SnapshotId, result.SnapshotId);
        Assert.Equal(status.Warnings.Count, result.IndexWarningCount);
        Assert.Contains("CREATE PROCEDURE dbo.P", result.Definition);
        Assert.Contains(result.Dependencies, d => d.Name == "Missing" && !d.Resolved);
        Assert.Contains(result.Warnings, w => w.Contains("Dynamic"));
        Assert.Contains(result.Warnings, w => w.Contains("Unresolved references"));
        Assert.Contains(result.Warnings, w => w.Contains("target.sql"));
        Assert.DoesNotContain(result.Warnings, w => w.Contains("unrelated.sql"));
        Assert.Contains(status.Warnings, w => w.Contains("unrelated.sql"));
    }
    [Fact]
    public async Task ChangeTrackerDetectsAddDeleteAndConfigChanges()
    {
        var sln = Path.Combine(Scripts, "Demo.sln"); File.WriteAllText(sln, "test");
        var tracker = new SolutionInputTracker();
        var state = new SolutionState("Ready", sln, "snapshot", DateTimeOffset.UtcNow, [], []);
        Assert.False(await tracker.ChangedAsync(state, [], default));
        Write("Added.cs", "class Added {}"); Assert.True(await tracker.ChangedAsync(state, [], default));
        Assert.False(await tracker.ChangedAsync(state, [], default));
        File.Delete(Path.Combine(Scripts, "Added.cs")); Assert.True(await tracker.ChangedAsync(state, [], default));
        Write("Directory.Build.props", "<Project />"); Assert.False(await tracker.ChangedAsync(state, [], default));
        Write("App.config", "<configuration />"); Assert.True(await tracker.ChangedAsync(state, [], default));
    }
    [Fact]
    public async Task RealExportSmokeWhenExplicitlyConfigured()
    {
        var path = Environment.GetEnvironmentVariable("SOURCETRAIL_TEST_SQL_FOLDER");
        if (string.IsNullOrWhiteSpace(path)) return;
        using var db = new DatabaseAnalyzer(Options);
        var state = await db.SelectAsync("SqlFiles", path, default);
        Assert.True(state.Files > 0); Assert.True(state.Objects > 0);
        Console.WriteLine($"SQL export: files={state.Files}, objects={state.Objects}, parsed={state.ParsedFiles}, reused={state.ReusedFiles}, diagnostics={state.Warnings.Count}");
        var result = await db.AnalyzeAsync("dbo.usp_select_SmartInfoReception", false, default);
        Assert.Equal("SqlFiles", result.Source); Assert.NotEqual("NotFoundInProvidedFiles", result.Status);
        Console.WriteLine($"Sample procedure status={result.Status}; dependencies={result.Dependencies.Count}");
        using var reopened = new DatabaseAnalyzer(Options);
        var reused = await reopened.SelectAsync("SqlFiles", path, default);
        Assert.Equal(state.Files, reused.ReusedFiles);
        Console.WriteLine($"Reopened export cache: reused={reused.ReusedFiles}, parsed={reused.ParsedFiles}");
    }
    [Fact]
    public async Task WatcherRefreshesIndexAtNextRequest()
    {
        var options = Options; options.WatchFiles = true;
        Write("a.sql", "CREATE TABLE dbo.A(Id int);");
        using var db = new DatabaseAnalyzer(options);
        await db.SelectAsync("SqlFiles", Scripts, default);
        Write("new.sql", "CREATE TABLE dbo.NewTable(Id int);");
        await Task.Delay(100);
        Assert.Single((await db.FindAsync("NewTable", 0, 100, false, default)).Items);
        File.Delete(Path.Combine(Scripts, "new.sql"));
        await Task.Delay(100);
        Assert.Empty((await db.FindAsync("NewTable", 0, 100, false, default)).Items);
    }
    [Fact]
    public async Task DistinguishesDatabaseAndSchemaAndFindsFunctionCalls()
    {
        Write("a.sql", "USE A;\nGO\nCREATE FUNCTION dbo.F(@Id int) RETURNS int AS BEGIN RETURN @Id; END;\nGO\nCREATE PROCEDURE dbo.P AS SELECT dbo.F(1);\nGO\nUSE B;\nGO\nCREATE PROCEDURE dbo.P AS SELECT 1;");
        using var db = new DatabaseAnalyzer(Options);
        await db.SelectAsync("SqlFiles", Scripts, default);
        Assert.Equal("Ambiguous", (await db.AnalyzeAsync("dbo.P", false, default)).Status);
        var result = await db.AnalyzeAsync("A.dbo.P", false, default);
        Assert.Contains(result.Dependencies, d => d.Name == "F" && d.Kind == "Function" && d.Resolved);
    }
    [Fact]
    public async Task ConnectsExportedAlterTableAddConstraints()
    {
        Write("a.sql", "CREATE TABLE dbo.Parent(Id int PRIMARY KEY);\nGO\nCREATE TABLE dbo.Child(Id int, ParentId int);\nGO\nALTER TABLE dbo.Child ADD CONSTRAINT FK_Child FOREIGN KEY(ParentId) REFERENCES dbo.Parent(Id);\nGO\nALTER TABLE dbo.Child CHECK CONSTRAINT FK_Child;");
        using var db = new DatabaseAnalyzer(Options);
        var state = await db.SelectAsync("SqlFiles", Scripts, default);
        Assert.Equal(2, state.Objects);
        var table = Assert.Single((await db.FindAsync("Child", 0, 100, false, default)).Items);
        Assert.Contains(table.Constraints!, c => c.Contains("FOREIGN KEY"));
        Assert.Contains(table.References, r => r.Name == "Parent" && r.Access == "ForeignKey");
    }
    [Fact]
    public async Task RoslynReloadsWhenSourceIsAddedChangedAndDeleted()
    {
        var project = Path.Combine(Scripts, "Refresh.csproj");
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        var solution = Path.Combine(Scripts, "Refresh.sln");
        File.WriteAllText(solution, "Microsoft Visual Studio Solution File, Format Version 12.00\nProject(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Refresh\", \"Refresh.csproj\", \"{A46FEED2-5B25-4FDB-AF18-F0A65EAF9D13}\"\nEndProject\nGlobal\nEndGlobal\n");
        Write("First.cs", "public class First { public void Run() {} }");
        using var code = new SourceTrail.Roslyn.Workspace.RoslynAnalyzer();
        await code.LoadAsync(solution, default);
        var initial = code.Status().SnapshotId;
        Write("Added.cs", "public class Added { public void NewMethod() {} }");
        await code.EnsureFreshAsync(default);
        Assert.NotEqual(initial, code.Status().SnapshotId);
        Assert.NotEmpty((await code.FindSymbolsAsync("NewMethod", 0, 100, default)).Items);
        Write("Added.cs", "public class Added { public void ChangedMethod() {} }");
        await code.EnsureFreshAsync(default);
        Assert.Empty((await code.FindSymbolsAsync("NewMethod", 0, 100, default)).Items);
        Assert.NotEmpty((await code.FindSymbolsAsync("ChangedMethod", 0, 100, default)).Items);
        File.Delete(Path.Combine(Scripts, "Added.cs"));
        await code.EnsureFreshAsync(default);
        Assert.Empty((await code.FindSymbolsAsync("ChangedMethod", 0, 100, default)).Items);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
