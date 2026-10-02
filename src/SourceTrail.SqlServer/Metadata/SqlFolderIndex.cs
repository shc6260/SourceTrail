using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SourceTrail.Core.Models;
namespace SourceTrail.SqlServer.Metadata;

/// <summary>SQL 폴더를 색인하고 파일별 분석 결과를 메모리와 디스크에서 재사용한다.</summary>
public sealed class SqlFolderIndex(DatabaseOptions options) : IDisposable
{
    // 파서/모델 의미가 바뀌면 캐시 버전을 올린다. 분석기 객체 자체는 저장하지 않는다.
    private string CacheDirectory => string.IsNullOrWhiteSpace(options.CacheDirectory) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SourceTrail", "cache") : options.CacheDirectory;
    private const string Version = "sql-index-3-scriptdom180";
    public sealed record CachedFile(string Hash, SqlFileObject[] Objects, string[] Warnings);
    public sealed record Cache(string Version, string Folder, Dictionary<string, CachedFile> Files);
    private Dictionary<string, CachedFile> files = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? watcher;
    private int dirty = 1;
    private SqlSourceState state = new("SqlFiles", "NotLoaded", null, null, null, 0, 0, 0, 0, []);
    public SqlSourceState Status => state;
    private SqlFileObject[] objects = [];
    public IEnumerable<SqlFileObject> Objects => objects;
    public async Task<SqlSourceState> LoadAsync(string folder, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder)) throw new ArgumentException("Provide an existing absolute SQL folder.");
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var warnings = new List<string>();
        var cachePath = GetCachePath(folder);
        var previous = await ReadPreviousFilesAsync(folder, cachePath, warnings, token);
        // 스캔 전에 초기화해 스캔 도중 발생한 변경 이벤트를 다음 요청까지 유지한다.
        Interlocked.Exchange(ref dirty, 0);
        var next = new Dictionary<string, CachedFile>(StringComparer.OrdinalIgnoreCase);
        int parsed = 0, reused = 0;
        try
        {
            var scan = await ScanFilesAsync(folder, previous, warnings, token);
            next = scan.Files;
            parsed = scan.Parsed;
            reused = scan.Reused;
            var merged = MergeDefinitions(next, warnings);
            await SaveCacheAsync(cachePath, folder, next, warnings, token);
            PublishSnapshot(folder, next, merged, parsed, reused, warnings);
            ConfigureWatcher(folder);
            return state;
        }
        catch { Interlocked.Exchange(ref dirty, 1); throw; }
    }
    // 파일 감지 누락은 주기적인 전체 대조로 보완하고, 실제 갱신은 요청 경계에서 수행한다.
    public async Task EnsureAsync(CancellationToken token)
    {
        if (state.Folder is null)
        {
            if (string.IsNullOrWhiteSpace(options.SqlFolder))
                throw new InvalidOperationException("Select SqlFiles with an absolute sqlFolder first.");
            await LoadAsync(options.SqlFolder, token);
        }
        else if (NeedsRefresh())
        {
            await LoadAsync(state.Folder, token);
        }
    }

    private bool NeedsRefresh() => !options.WatchFiles || Volatile.Read(ref dirty) != 0 ||
        DateTimeOffset.UtcNow - state.AnalyzedAt > TimeSpan.FromSeconds(30);

    private void Changed(object sender, FileSystemEventArgs e) => Interlocked.Exchange(ref dirty, 1);
    public static string Key(SqlFileObject o) => string.Join(".", o.Database, o.Schema, o.Name);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static IEnumerable<string> Enumerate(string root, List<string> warnings)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            string[] paths;
            try { paths = Directory.GetFileSystemEntries(directory); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { warnings.Add($"Directory unreadable: {directory}"); continue; }
            foreach (var path in paths)
            {
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) { warnings.Add($"Skipped link: {path}"); continue; }
                if (attributes.HasFlag(FileAttributes.Directory)) { if (Path.GetFileName(path) is not ".git" and not "bin" and not "obj") pending.Push(path); }
                else if (Path.GetExtension(path).Equals(".sql", StringComparison.OrdinalIgnoreCase)) yield return path;
            }
        }
    }
    public void Dispose() => watcher?.Dispose();

    private sealed record FileScan(Dictionary<string, CachedFile> Files, int Parsed, int Reused);

    private string GetCachePath(string folder) =>
        Path.Combine(CacheDirectory, "sql-" + Hash(Encoding.UTF8.GetBytes(folder.ToUpperInvariant())) + ".json");

    // 같은 폴더는 메모리 색인을 사용하고, 다른 폴더는 버전이 맞는 디스크 캐시만 읽는다.
    private async Task<Dictionary<string, CachedFile>> ReadPreviousFilesAsync(
        string folder, string cachePath, List<string> warnings, CancellationToken token)
    {
        var previous = files;
        if (!string.Equals(state.Folder, folder, StringComparison.OrdinalIgnoreCase))
        {
            previous = new(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(cachePath))
                {
                    var cache = JsonSerializer.Deserialize<Cache>(await File.ReadAllTextAsync(cachePath, token));
                    if (cache?.Version == Version && string.Equals(cache.Folder, folder, StringComparison.OrdinalIgnoreCase))
                        previous = new(cache.Files, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            { warnings.Add("Cache unavailable/corrupt; rebuilding from source files."); }
        }
        return previous;
    }

    // 파일 목록을 새로 구성하므로 삭제된 파일은 다음 색인에서 자연스럽게 제외된다.
    private static async Task<FileScan> ScanFilesAsync(string folder,
        Dictionary<string, CachedFile> previous, List<string> warnings, CancellationToken token)
    {
        var next = new Dictionary<string, CachedFile>(StringComparer.OrdinalIgnoreCase);
        int parsed = 0, reused = 0;
        foreach (var path in Enumerate(folder, warnings))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var bytes = await File.ReadAllBytesAsync(path, token);
                var hash = Hash(bytes);
                if (previous.TryGetValue(path, out var cached) && cached.Hash == hash) { next[path] = cached; reused++; continue; }
                using var stream = new MemoryStream(bytes);
                using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
                var text = await reader.ReadToEndAsync(token);
                var objects = SqlFileParser.Parse(text, path, out var errors);
                next[path] = new(hash, objects, errors);
                parsed++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or DecoderFallbackException)
            { warnings.Add($"Unable to read {path} ({e.GetType().Name}); encoding/access requires verification."); }
        }
        return new(next, parsed, reused);
    }

    // SSMS의 CREATE TABLE과 별도 ALTER TABLE ADD를 연결한다. 중복 정의는 병합하지 않는다.
    private static SqlFileObject[] MergeDefinitions(Dictionary<string, CachedFile> next, List<string> warnings)
    {
        warnings.AddRange(next.Values.SelectMany(f => f.Warnings));
        foreach (var group in next.Values.SelectMany(f => f.Objects).Where(o => o.Kind != "TablePatch").GroupBy(Key, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            warnings.Add($"Duplicate definitions: {group.Key}. Select the intended export/database; definitions are not merged.");
        var allObjects = next.Values.SelectMany(f => f.Objects).ToArray();
        var definitions = allObjects.Where(o => o.Kind != "TablePatch").ToArray();
        var patches = allObjects.Where(o => o.Kind == "TablePatch").GroupBy(Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        foreach (var patch in patches)
            if (definitions.Count(o => o.Kind == "Table" && Key(o).Equals(patch.Key, StringComparison.OrdinalIgnoreCase)) != 1)
                warnings.Add($"ALTER TABLE target missing or ambiguous: {patch.Key}.");
        var merged = definitions.Select(o => ApplyTablePatch(o, definitions, patches)).ToArray();
        return merged;
    }

    // 임시 파일을 완성한 후 교체한다. 저장 실패 시에도 메모리 분석은 계속 사용할 수 있다.
    private async Task SaveCacheAsync(string cachePath, string folder,
        Dictionary<string, CachedFile> next, List<string> warnings, CancellationToken token)
    {
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            temporary = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new Cache(Version, folder, next)), token);
            File.Move(temporary, cachePath, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { warnings.Add("Disk cache could not be saved; memory index remains available."); }
        finally { CleanupTemporaryCache(temporary, warnings); }
    }

    private void PublishSnapshot(string folder, Dictionary<string, CachedFile> next,
        SqlFileObject[] merged, int parsed, int reused, List<string> warnings)
    {
        files = next; objects = merged;
        state = new("SqlFiles", warnings.Count == 0 ? "Ready" : "Partial", folder, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
            next.Count, Objects.Count(), parsed, reused, warnings);
    }

    // 감지 이벤트는 분석을 직접 실행하지 않고 다음 요청의 갱신 여부만 표시한다.
    private void ConfigureWatcher(string folder)
    {
        if (watcher is null || !string.Equals(watcher.Path, folder, StringComparison.OrdinalIgnoreCase))
        {
            watcher?.Dispose(); watcher = null;
            if (options.WatchFiles)
            {
                watcher = new(folder) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
                watcher.Changed += Changed; watcher.Created += Changed; watcher.Deleted += Changed; watcher.Renamed += Changed;
                watcher.Error += (_, _) => Interlocked.Exchange(ref dirty, 1);
                watcher.EnableRaisingEvents = true;
            }
        }
    }


    private static SqlFileObject ApplyTablePatch(SqlFileObject o, SqlFileObject[] definitions,
        Dictionary<string, SqlFileObject[]> patches)
    {
        if (o.Kind != "Table" || !patches.TryGetValue(Key(o), out var additions) || definitions.Count(d => Key(d).Equals(Key(o), StringComparison.OrdinalIgnoreCase)) != 1) return o;
        return o with
        {
            Members = o.Members.Concat(additions.SelectMany(a => a.Members)).ToArray(),
            References = o.References.Concat(additions.SelectMany(a => a.References)).ToArray(),
            Constraints = (o.Constraints ?? []).Concat(additions.SelectMany(a => a.Constraints ?? [])).ToArray(),
            Warnings = o.Warnings.Concat(additions.SelectMany(a => a.Warnings)).ToArray()
        };
    }

    private static void CleanupTemporaryCache(string? temporary, List<string> warnings)
    {
        try
        {
            if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            warnings.Add("Temporary cache cleanup requires verification.");
        }
    }

}
