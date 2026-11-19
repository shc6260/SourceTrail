using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using SourceTrail.Core.Models;
using SourceTrail.SqlServer.Metadata;
namespace SourceTrail.Mcp.Tools;
[McpServerToolType]
public sealed class DatabaseTools(DatabaseAnalyzer database, ILogger<DatabaseTools> logger)
{
    [McpServerTool(Name = "select_database_source", ReadOnly = true, Destructive = false), Description("Selects a local SQL script folder. Only mode SqlFiles is supported; never connects to SQL Server. Source files are read-only; index caches may be written.")]
    public Task<SqlSourceState> SelectDatabaseSource(string mode, CancellationToken cancellationToken, string? sqlFolder = null) =>
        Run("select_database_source", () => database.SelectAsync(mode, sqlFolder, cancellationToken), cancellationToken);
    [McpServerTool(Name = "get_database_status", ReadOnly = true, Destructive = false), Description("Reports the local SQL folder and index diagnostics. No DB connection.")]
    public SqlSourceState GetDatabaseStatus() => database.Status();
    [McpServerTool(Name = "refresh_database_source", ReadOnly = true, Destructive = false), Description("Reconciles local SQL files against cache, including additions/changes/deletions.")]
    public Task<SqlSourceState> RefreshDatabaseSource(CancellationToken cancellationToken) =>
        Run("refresh_database_source", () => database.RefreshAsync(cancellationToken), cancellationToken);
    [McpServerTool(Name = "find_sql_object", ReadOnly = true, Destructive = false), Description("Finds SQL file objects with source locations, members, references and relevant warnings. Set locationsOnly=true when only source locations and warnings are needed. Use get_database_status for all index warnings. Definition is opt-in and cannot be combined with locationsOnly.")]
    public Task<object> FindSqlObject(string query, CancellationToken cancellationToken, int offset = 0, int limit = 20,
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

    [McpServerTool(Name = "analyze_procedure", ReadOnly = true, Destructive = false), Description("Reads a procedure from local SqlFiles and returns definition provenance and direct dependencies. Definition is opt-in. Does not traverse dependencies recursively or connect to a DB.")]
    public Task<ProcedureAnalysis> AnalyzeProcedure(string procedure, CancellationToken cancellationToken, bool includeDefinition = false) =>
        Run("analyze_procedure", () => database.AnalyzeAsync(procedure, includeDefinition, cancellationToken), cancellationToken);

    private async Task<T> Run<T>(string tool, Func<Task<T>> operation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var startedAt = Stopwatch.GetTimestamp();
        try { return await operation(); }
        finally
        {
            var executionMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            logger.LogInformation("Tool {Tool} timing: {ExecutionMs:F3} ms (SQL analyzer serializes its own state).", tool, executionMs);
        }
    }
}
