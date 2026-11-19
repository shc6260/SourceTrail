using System.Text.Json;
using SourceTrail.Core.Models;
using SourceTrail.SqlServer.Metadata;
using Xunit;

namespace SourceTrail.Tests;

public sealed class SqlIncrementalIndexTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SourceTrail-sql-incremental-" + Guid.NewGuid().ToString("N"));
    private string Scripts => Path.Combine(root, "scripts");
    private DatabaseOptions Options => new() { SqlFolder = Scripts, CacheDirectory = Path.Combine(root, "cache"), WatchFiles = false };
    public SqlIncrementalIndexTests() => Directory.CreateDirectory(Scripts);
    private string Write(string name, string type = "int")
    {
        var path = Path.Combine(Scripts, name + ".sql");
        File.WriteAllText(path, $"CREATE TABLE dbo.{name}(Id {type});");
        return path;
    }

    [Fact]
    public async Task RestartReusesDiskCacheWithoutReadingSqlOrRewritingCache()
    {
        Write("A"); Write("B");
        using (var first = new SqlFolderIndex(Options))
        {
            Assert.Equal(2, (await first.LoadAsync(Scripts, default)).ParsedFiles);
            Assert.Equal(2, first.LastReadFiles);
        }
        var cache = Directory.GetFiles(Options.CacheDirectory).Single();
        var cacheTime = File.GetLastWriteTimeUtc(cache);
        using var reopened = new SqlFolderIndex(Options);
        var state = await reopened.LoadAsync(Scripts, default);
        Assert.Equal(0, reopened.LastReadFiles);
        Assert.Equal(0, state.ParsedFiles);
        Assert.Equal(2, state.ReusedFiles);
        Assert.Equal(cacheTime, File.GetLastWriteTimeUtc(cache));
    }

    [Fact]
    public async Task ReadsOnlyModifiedAndAddedFilesAndReconcilesRenameAndDeletion()
    {
        var a = Write("A"); var b = Write("B"); Write("C");
        using var index = new SqlFolderIndex(Options);
        await index.LoadAsync(Scripts, default);
        var time = File.GetLastWriteTimeUtc(a);
        Write("A", "bigint");
        File.SetLastWriteTimeUtc(a, time.AddSeconds(1));
        Write("D");
        var updated = await index.LoadAsync(Scripts, default);
        Assert.Equal(2, index.LastReadFiles);
        Assert.Equal(2, updated.ParsedFiles);
        Assert.Equal(2, updated.ReusedFiles);
        Directory.CreateDirectory(Path.Combine(Scripts, "nested"));
        var moved = Path.Combine(Scripts, "nested", "renamed.sql");
        File.Move(b, moved);
        File.Delete(a);
        var renamed = await index.LoadAsync(Scripts, default);
        Assert.Equal(1, index.LastReadFiles);
        Assert.Equal(3, renamed.Files);
        Assert.DoesNotContain(index.Objects, o => o.Name == "A");
        Assert.Equal(moved, index.Objects.Single(o => o.Name == "B").File);
    }

    [Fact]
    public async Task NotificationsReadSameStampChangesAndWatcherErrorVerifiesAllFiles()
    {
        var a = Write("A"); Write("B");
        using var index = new SqlFolderIndex(Options);
        await index.LoadAsync(Scripts, default);
        var time = File.GetLastWriteTimeUtc(a);
        File.WriteAllText(a, "CREATE TABLE dbo.Z(Id int);");
        File.SetLastWriteTimeUtc(a, time);
        index.MarkChanged(a);
        await index.EnsureAsync(default);
        Assert.Equal(1, index.LastReadFiles);
        Assert.Contains(index.Objects, o => o.Name == "Z");
        File.WriteAllText(a, "CREATE TABLE dbo.Y(Id int);");
        File.SetLastWriteTimeUtc(a, time);
        index.MarkWatcherError();
        await index.EnsureAsync(default);
        Assert.Equal(2, index.LastReadFiles);
        Assert.Equal(1, index.Status.ParsedFiles);
        Assert.Contains(index.Objects, o => o.Name == "Y");
    }

    [Fact]
    public async Task LegacyCacheAddsStampsWithoutReparsingAndExplicitVerificationReadsAll()
    {
        Write("A");
        using (var first = new SqlFolderIndex(Options)) await first.LoadAsync(Scripts, default);
        var path = Directory.GetFiles(Options.CacheDirectory).Single();
        var cache = JsonSerializer.Deserialize<SqlFolderIndex.Cache>(File.ReadAllText(path))!;
        var oldFiles = cache.Files.ToDictionary(p => p.Key, p => p.Value with { Stamp = null });
        File.WriteAllText(path, JsonSerializer.Serialize(new SqlFolderIndex.Cache("sql-index-3-scriptdom180", Scripts, oldFiles)));
        using var reopened = new SqlFolderIndex(Options);
        var state = await reopened.LoadAsync(Scripts, default);
        Assert.Equal(1, reopened.LastReadFiles);
        Assert.Equal(0, state.ParsedFiles);
        await reopened.LoadAsync(Scripts, default);
        Assert.Equal(0, reopened.LastReadFiles);
        await reopened.LoadAsync(Scripts, default, verifyContent: true);
        Assert.Equal(1, reopened.LastReadFiles);
        Assert.Equal(0, reopened.Status.ParsedFiles);
    }

    [Fact]
    public async Task FailedCacheSaveRetriesEvenWhenSourceIsUnchanged()
    {
        Write("A");
        var options = Options;
        using var index = new SqlFolderIndex(options);
        await index.LoadAsync(Scripts, default);
        var cachePath = Directory.GetFiles(options.CacheDirectory).Single();
        File.Delete(cachePath);
        Directory.CreateDirectory(cachePath); // 캐시 파일 교체만 실패시킨다.
        Write("B");
        var failed = await index.LoadAsync(Scripts, default);
        Assert.Contains(failed.Warnings, w => w.Contains("Disk cache could not be saved"));
        Directory.Delete(cachePath);
        File.WriteAllText(cachePath, "stale");
        await index.LoadAsync(Scripts, default);
        Assert.Equal(0, index.LastReadFiles);
        using var reopened = new SqlFolderIndex(options);
        Assert.Equal(2, (await reopened.LoadAsync(Scripts, default)).ReusedFiles);
        Assert.Equal(0, reopened.LastReadFiles);
    }

    public void Dispose() => Directory.Delete(root, true);
}
