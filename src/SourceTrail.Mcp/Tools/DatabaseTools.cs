using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using SourceTrail.Core.Models;
using SourceTrail.SqlServer.Metadata;
namespace SourceTrail.Mcp.Tools;
[McpServerToolType]
public sealed class DatabaseTools(DatabaseAnalyzer database, SemaphoreSlim executionGate, ILogger<DatabaseTools> logger)
{
    [McpServerTool(Name = "select_database_source", ReadOnly = true, Destructive = false), Description("Selects SqlFiles or LiveDatabase. SqlFiles accepts an absolute sqlFolder; LiveDatabase uses server-configured credentials, never tool arguments. Source files and DB are read-only; local cache files may be written.")]
    public Task<SqlSourceState> SelectDatabaseSource(string mode, CancellationToken cancellationToken, string? sqlFolder = null) =>
        Run("select_database_source", () => database.SelectAsync(mode, sqlFolder, cancellationToken), cancellationToken);
    [McpServerTool(Name = "get_database_status", ReadOnly = true, Destructive = false), Description("Reports active SQL source and last index diagnostics; configured live mode does not prove connectivity.")]
    public SqlSourceState GetDatabaseStatus() => database.Status();
    [McpServerTool(Name = "refresh_database_source", ReadOnly = true, Destructive = false), Description("Reconciles SQL files against cache, including additions/changes/deletions. Live definitions are queried fresh by analyze_procedure.")]
    public Task<SqlSourceState> RefreshDatabaseSource(CancellationToken cancellationToken) =>
        Run("refresh_database_source", () => database.RefreshAsync(cancellationToken), cancellationToken);
    [McpServerTool(Name = "find_sql_object", ReadOnly = true, Destructive = false), Description("Finds SQL file objects with source locations, members, references and relevant warnings. Set locationsOnly=true when only source locations and warnings are needed. Use get_database_status for all index warnings. Definition is opt-in and cannot be combined with locationsOnly.")]
    public Task<object> FindSqlObject(string query, CancellationToken cancellationToken, int offset = 0, int limit = 100,
        bool includeDefinition = false, bool locationsOnly = false)
    {
        if (locationsOnly && includeDefinition)
            throw new ArgumentException("locationsOnly cannot be combined with includeDefinition.");

        return Run<object>("find_sql_object", async () =>
        {
            var result = await database.FindAsync(query, offset, limit, includeDefinition, cancellationToken);
            if (!locationsOnly) return result;

            return new
            {
                Items = result.Items.Select(item => new
                    { item.Database, item.Schema, item.Name, item.Kind, item.File, item.Line, item.Warnings }).ToArray(),
                result.Total, result.Offset, result.Limit, result.Truncated, result.SnapshotId,
                result.Warnings, result.IndexStatus, result.IndexWarningCount
            };
        }, cancellationToken);
    }

    private async Task<T> Run<T>(string tool, Func<Task<T>> operation, CancellationToken token)
    {
        var queuedAt = Stopwatch.GetTimestamp();
        await executionGate.WaitAsync(token);
        var startedAt = Stopwatch.GetTimestamp();
        try { return await operation(); }
        finally
        {
            var executionMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            executionGate.Release();
            logger.LogInformation("Tool {Tool} timing: queue {QueueMs:F3} ms, execution {ExecutionMs:F3} ms.",
                tool, Stopwatch.GetElapsedTime(queuedAt, startedAt).TotalMilliseconds, executionMs);
        }
    }
}
