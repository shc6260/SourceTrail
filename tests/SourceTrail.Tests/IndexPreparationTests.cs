using System.Text.Json;
using SourceTrail.Core.Models;
using SourceTrail.Mcp.Indexing;
using SourceTrail.Roslyn.Workspace;
using SourceTrail.SqlServer.Metadata;
using Xunit;

namespace SourceTrail.Tests;

public sealed class IndexPreparationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SourceTrail-prepare-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(root, "source");
    private string Sql => Path.Combine(root, "sql");
    private string Index => Path.Combine(root, "index");
    private AnalysisOptions Analysis => new() { SolutionPath = Source };
    private DatabaseOptions Database => new() { SqlFolder = Sql, WatchFiles = false };
    private IndexPreparationOptions Options => new() { Directory = Index };
    private IndexPreparationService Service => new(Analysis, Database, Options);
    public IndexPreparationTests()
    {
        Directory.CreateDirectory(Source); Directory.CreateDirectory(Sql);
        File.WriteAllText(Path.Combine(Source, "Screen.cs"), "namespace Demo; partial class Screen { void Save_Click(object s, object e) {} }");
        File.WriteAllText(Path.Combine(Source, "Screen.Designer.cs"), "namespace Demo; partial class Screen { void InitializeComponent() { this.Text = \"접수\"; this.btnSave.Text = \"저장\"; this.btnSave.Click += this.Save_Click; } }");
        File.WriteAllText(Path.Combine(Sql, "a.sql"), "CREATE TABLE dbo.A(Id int);");
    }

    [Fact]
    public async Task PreparesAllThreeIndexesAndReopenedRuntimeReusesThem()
    {
        var progress = new List<IndexProgress>();
        var report = await Service.PrepareAsync(progress: progress.Add);
        Assert.True(report.Published, report.Error);
        Assert.Equal(2, report.Code!.Parsed);
        Assert.True(report.Code.Symbols > 0);
        Assert.Equal("Built", report.Ui!.IndexState);
        Assert.Equal(1, report.Ui.Screens);
        Assert.Equal(1, report.Sql!.ParsedFiles);
        Assert.Contains(progress, p => p.Stage == "CSharp" && p.Completed == p.Total);
        Assert.Contains(report.Stages, s => s.Stage == "Publish");
        var analysis = Analysis; var database = Database;
        analysis.SolutionPath += Path.DirectorySeparatorChar;
        database.SqlFolder += Path.DirectorySeparatorChar;
        Assert.True(IndexPreparationService.ApplyActive(analysis, database, Options));
        Assert.Equal(Source, analysis.SolutionPath);
        using var code = new RoslynAnalyzer(analysis);
        int parsed = 0; code.SymbolDocumentIndexed = () => parsed++;
        await code.LoadAsync(Source, default);
        Assert.Single((await code.FindSymbolsAsync("Demo.Screen.Save_Click", 0, 20, default)).Items);
        Assert.Equal(0, parsed);
        Assert.Equal("Disk", (await code.FindUiActionsAsync("접수 저장", 5, default)).IndexState);
        using var sql = new SqlFolderIndex(database);
        Assert.Equal(1, (await sql.LoadAsync(Sql, default)).ReusedFiles);
        Assert.Equal(0, sql.LastReadFiles);
    }

    [Fact]
    public async Task IncrementalPreparationReusesUnchangedAndReconcilesChangedAddedDeletedFiles()
    {
        var first = await Service.PrepareAsync();
        var same = await Service.PrepareAsync();
        Assert.True(same.Published, same.Error);
        Assert.Equal(0, same.Code!.Parsed); Assert.Equal(2, same.Code.Reused);
        Assert.Equal("Disk", same.Ui!.IndexState);
        Assert.Equal(0, same.Sql!.ParsedFiles);
        Assert.Equal(1, same.Sql.ReusedFiles);
        File.Delete(Path.Combine(Source, "Screen.cs"));
        File.WriteAllText(Path.Combine(Source, "Other.cs"), "class Other { void NewMethod() {} }");
        File.WriteAllText(Path.Combine(Sql, "a.sql"), "CREATE TABLE dbo.A(Id bigint);");
        File.WriteAllText(Path.Combine(Sql, "b.sql"), "CREATE TABLE dbo.B(Id int);");
        var changed = await Service.PrepareAsync();
        Assert.True(changed.Published, changed.Error);
        Assert.Equal(1, changed.Code!.Parsed); Assert.Equal(1, changed.Code.Reused);
        Assert.Equal(2, changed.Sql!.ParsedFiles);
        Assert.True(Directory.Exists(Path.Combine(Index, "generations", first.Generation!)));
        var options = Analysis; var db = Database;
        IndexPreparationService.ApplyActive(options, db, Options);
        using var code = new RoslynAnalyzer(options);
        await code.LoadAsync(Source, default);
        Assert.Empty((await code.FindSymbolsAsync("Save_Click", 0, 20, default)).Items);
        Assert.Single((await code.FindSymbolsAsync("NewMethod", 0, 20, default)).Items);
    }

    [Fact]
    public async Task RebuildIgnoresCacheEvenWhenSizeAndTimesArePreserved()
    {
        var first = await Service.PrepareAsync();
        var path = Path.Combine(Sql, "a.sql");
        var time = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, "CREATE TABLE dbo.Z(Id int);");
        File.SetLastWriteTimeUtc(path, time);
        var rebuilt = await Service.PrepareAsync(rebuild: true);
        Assert.True(rebuilt.Published, rebuilt.Error);
        Assert.Equal("Rebuild", rebuilt.Mode);
        Assert.Equal(2, rebuilt.Code!.Parsed); Assert.Equal(0, rebuilt.Code.Reused);
        Assert.Equal("Built", rebuilt.Ui!.IndexState);
        Assert.Equal(1, rebuilt.Sql!.ParsedFiles); Assert.Equal(0, rebuilt.Sql.ReusedFiles);
        var options = Analysis; var database = Database;
        IndexPreparationService.ApplyActive(options, database, Options);
        using var db = new DatabaseAnalyzer(database);
        Assert.Single((await db.FindAsync("dbo.Z", 0, 20, false, default)).Items);
        Assert.True(Directory.Exists(Path.Combine(Index, "generations", first.Generation!)));
    }

    [Fact]
    public async Task CancellationKeepsActivePointerAndRemovesOnlyTheUnpublishedGeneration()
    {
        var first = await Service.PrepareAsync();
        var pointer = File.ReadAllText(Path.Combine(Index, "active.json"));
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service.PrepareAsync(rebuild: true,
            progress: p => { if (p.Stage == "CSharp" && p.Completed > 0) cancel.Cancel(); }, cancellationToken: cancel.Token));
        Assert.Equal(pointer, File.ReadAllText(Path.Combine(Index, "active.json")));
        Assert.Equal(first.Generation, IndexPreparationService.ReadActive(Index)!.Generation);
        Assert.Single(Directory.GetDirectories(Path.Combine(Index, "generations")));
    }

    [Fact]
    public async Task ReadFailureAndInputsChangingBeforePublishKeepThePreviousIndex()
    {
        var first = await Service.PrepareAsync();
        var sqlPath = Path.Combine(Sql, "a.sql");
        File.WriteAllText(sqlPath, "CREATE TABLE dbo.Changed(Id bigint);");
        using (var locked = new FileStream(sqlPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var failed = await Service.PrepareAsync();
            Assert.False(failed.Published);
            Assert.False(failed.ReconnectRequired);
            Assert.Contains("could not read/save", failed.Error);
            Assert.Equal(first.Generation, IndexPreparationService.ReadActive(Index)!.Generation);
        }
        var changed = await Service.PrepareAsync(progress: p =>
        {
            if (p.Stage == "SQL" && p.Completed > 0)
                File.WriteAllText(Path.Combine(Source, "AddedDuringBuild.cs"), "class AddedDuringBuild {}");
        });
        Assert.False(changed.Published);
        Assert.Contains("changed during preparation", changed.Error);
        Assert.Equal(first.Generation, IndexPreparationService.ReadActive(Index)!.Generation);
        Assert.Single(Directory.GetDirectories(Path.Combine(Index, "generations")));
    }

    [Fact]
    public async Task ConcurrentPreparationAndUnsafeOrMismatchedPointersAreRejected()
    {
        await Service.PrepareAsync();
        using (var locked = new FileStream(Path.Combine(Index, "prepare.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAsync<IOException>(() => Service.PrepareAsync());
        var analysis = Analysis; analysis.SolutionPath = Sql;
        Assert.False(IndexPreparationService.ApplyActive(analysis, Database, Options));
        File.WriteAllText(Path.Combine(Index, "active.json"), JsonSerializer.Serialize(
            new IndexPreparationService.ActiveIndex(1, "..", Source, Sql, DateTimeOffset.UtcNow)));
        Assert.Null(IndexPreparationService.ReadActive(Index));
    }

    [Fact]
    public async Task CommandSupportsIncrementalRebuildAndRejectsUnknownOptions()
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(0, await IndexPreparationCommand.RunAsync([], Analysis, Database, Options, output, error, default));
        Assert.True(JsonDocument.Parse(output.ToString()).RootElement.GetProperty("Published").GetBoolean());
        output.GetStringBuilder().Clear();
        Assert.Equal(0, await IndexPreparationCommand.RunAsync(["--rebuild"], Analysis, Database, Options, output, error, default));
        Assert.Equal("Rebuild", JsonDocument.Parse(output.ToString()).RootElement.GetProperty("Mode").GetString());
        var pointer = File.ReadAllText(Path.Combine(Index, "active.json"));
        Assert.Equal(1, await IndexPreparationCommand.RunAsync(["--rebiuld"], Analysis, Database, Options, output, error, default));
        Assert.Equal(pointer, File.ReadAllText(Path.Combine(Index, "active.json")));
    }

    public void Dispose() => Directory.Delete(root, true);
}
