using System.Diagnostics;
using System.Text.Json;
using SourceTrail.Core.Models;
using SourceTrail.Roslyn.Workspace;
using SourceTrail.SqlServer.Metadata;

namespace SourceTrail.Mcp.Indexing;

/// <summary>별도 세대에서 전체 색인을 준비하고 완료된 세대만 원자적으로 활성화한다.</summary>
public sealed class IndexPreparationService(AnalysisOptions analysis, DatabaseOptions database,
    IndexPreparationOptions options)
{
    public sealed record ActiveIndex(int Version, string Generation, string SourcePath, string SqlFolder,
        DateTimeOffset PreparedAt);
    private const int Version = 1;
    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;

    public static ActiveIndex? ReadActive(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)) return null;
        try
        {
            var active = JsonSerializer.Deserialize<ActiveIndex>(File.ReadAllText(Path.Combine(directory, "active.json")));
            if (active?.Version != Version || !Guid.TryParseExact(active.Generation, "N", out _)) return null;
            var generation = Path.Combine(directory, "generations", active.Generation);
            return Directory.Exists(Path.Combine(generation, "code")) && Directory.Exists(Path.Combine(generation, "sql")) ? active : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public static bool ApplyActive(AnalysisOptions analysis, DatabaseOptions database, IndexPreparationOptions options)
    {
        var active = ReadActive(options.Directory);
        if (active is null || !Path.IsPathFullyQualified(analysis.SolutionPath) || !Path.IsPathFullyQualified(database.SqlFolder) ||
            !Normalize(analysis.SolutionPath).Equals(active.SourcePath, StringComparison.OrdinalIgnoreCase) ||
            !Normalize(database.SqlFolder).Equals(active.SqlFolder, StringComparison.OrdinalIgnoreCase)) return false;
        var generation = Path.Combine(options.Directory, "generations", active.Generation);
        analysis.SolutionPath = active.SourcePath;
        database.SqlFolder = active.SqlFolder;
        analysis.CodeCacheDirectory = Path.Combine(generation, "code");
        database.CacheDirectory = Path.Combine(generation, "sql");
        return true;
    }

    public async Task<IndexPreparationResult> PrepareAsync(bool rebuild = false,
        Action<IndexProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var token = cancellationToken;
        token.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(analysis.SolutionPath) ||
            !(Directory.Exists(analysis.SolutionPath) || File.Exists(analysis.SolutionPath) && analysis.SolutionPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Configure an existing absolute Analysis.SolutionPath folder or .sln.");
        if (!Path.IsPathFullyQualified(database.SqlFolder) || !Directory.Exists(database.SqlFolder))
            throw new ArgumentException("Configure an existing absolute Database.SqlFolder.");
        if (!Path.IsPathFullyQualified(options.Directory))
            throw new ArgumentException("Indexing.Directory must be an absolute cache directory.");
        if (database.Mode != "SqlFiles") throw new ArgumentException("Only SqlFiles can be prepared.");
        var root = Normalize(options.Directory);
        var source = Normalize(analysis.SolutionPath);
        var sqlFolder = Normalize(database.SqlFolder);
        Directory.CreateDirectory(root);
        // 다른 준비 작업과의 중복 게시를 방지한다. 일반 MCP 조회는 기존 세대를 계속 사용한다.
        using var preparationLock = new FileStream(Path.Combine(root, "prepare.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var previous = ReadActive(root);
        var generation = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(root, "generations", generation);
        var temporaryPointer = Path.Combine(root, "active." + generation + ".tmp");
        var codeOptions = Clone(analysis);
        var sqlOptions = Clone(database);
        var seedCode = Clone(analysis);
        var seedSql = Clone(database);
        ApplyActive(seedCode, seedSql, options);
        codeOptions.SolutionPath = source;
        codeOptions.CodeCacheDirectory = Path.Combine(staging, "code");
        codeOptions.WatchCodeFiles = false;
        sqlOptions.SqlFolder = sqlFolder;
        sqlOptions.CacheDirectory = Path.Combine(staging, "sql");
        sqlOptions.WatchFiles = false;
        var stages = new List<IndexStage>();
        var elapsed = Stopwatch.StartNew();
        bool published = false;
        CodePreparation? code = null;
        UiPreparation? ui = null;
        SqlSourceState? sql = null;
        async Task Stage(string name, Func<Task> action)
        {
            var timer = Stopwatch.StartNew();
            try { await action(); }
            finally { stages.Add(new(name, timer.ElapsedMilliseconds)); }
        }
        try
        {
            Directory.CreateDirectory(codeOptions.CodeCacheDirectory);
            Directory.CreateDirectory(sqlOptions.CacheDirectory);
            if (!rebuild)
                await Stage("ReuseCache", async () =>
                {
                    await RoslynAnalyzer.CopyPreparationCacheAsync(seedCode, source, codeOptions.CodeCacheDirectory, token);
                    using var seed = new SqlFolderIndex(seedSql);
                    await seed.CopyPreparationCacheAsync(sqlFolder, sqlOptions.CacheDirectory, token);
                });
            using var analyzer = new RoslynAnalyzer(codeOptions, requireCacheWrites: true);
            await Stage("CSharp", async () => code = await analyzer.PrepareDeclarationsAsync(source, progress, token));
            progress?.Invoke(new("UI", 0, 1, 0, 0));
            await Stage("UI", async () => ui = await analyzer.PrepareUiIndexAsync(token));
            progress?.Invoke(new("UI", 1, 1, ui!.IndexState == "Built" ? 1 : 0, ui.IndexState == "Disk" ? 1 : 0));
            progress?.Invoke(new("SQL", 0, 1, 0, 0));
            using var sqlIndex = new SqlFolderIndex(sqlOptions);
            await Stage("SQL", async () => sql = await sqlIndex.LoadAsync(sqlFolder, token));
            if (sql!.Warnings.Any(w => w.StartsWith("Unable to read ", StringComparison.Ordinal) ||
                w.StartsWith("Directory unreadable:", StringComparison.Ordinal) ||
                w.StartsWith("Disk cache could not be saved", StringComparison.Ordinal)))
                throw new IOException("SQL preparation could not read/save all inputs. The existing active index is retained.");
            progress?.Invoke(new("SQL", sql.Files, sql.Files, sql.ParsedFiles, sql.ReusedFiles));
            token.ThrowIfCancellationRequested();
            await Stage("Publish", async () =>
            {
                await analyzer.VerifyPreparationInputsAsync(token);
                sqlIndex.VerifyPreparationInputs(token);
                var pointer = new ActiveIndex(Version, generation, source, sqlFolder, DateTimeOffset.UtcNow);
                await File.WriteAllTextAsync(temporaryPointer, JsonSerializer.Serialize(pointer), token);
                token.ThrowIfCancellationRequested();
                File.Move(temporaryPointer, Path.Combine(root, "active.json"), overwrite: true);
                published = true;
            });
            return new(true, rebuild ? "Rebuild" : "Incremental", generation, code, ui, sql, stages, elapsed.ElapsedMilliseconds, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return new(false, rebuild ? "Rebuild" : "Incremental", previous?.Generation, code, ui, sql, stages, elapsed.ElapsedMilliseconds, e.Message, ReconnectRequired: false);
        }
        finally
        {
            // 기존 세대는 삭제하지 않는다. 이 요청에서 만든 미게시 세대만 정리한다.
            try
            {
                File.Delete(temporaryPointer);
                if (!published && Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
