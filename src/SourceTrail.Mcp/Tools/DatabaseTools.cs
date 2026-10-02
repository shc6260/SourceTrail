using System.ComponentModel;
using ModelContextProtocol.Server;
using SourceTrail.Core.Models;
using SourceTrail.SqlServer.Metadata;
namespace SourceTrail.Mcp.Tools;
[McpServerToolType]
public sealed class DatabaseTools(DatabaseAnalyzer database, SemaphoreSlim executionGate)
{
    [McpServerTool(Name = "select_database_source", ReadOnly = true, Destructive = false), Description("Selects SqlFiles or LiveDatabase. SqlFiles accepts an absolute sqlFolder; LiveDatabase uses server-configured credentials, never tool arguments. Source files and DB are read-only; local cache files may be written.")]
    public Task<SqlSourceState> SelectDatabaseSource(string mode, CancellationToken cancellationToken, string? sqlFolder = null) =>
        Run(() => database.SelectAsync(mode, sqlFolder, cancellationToken), cancellationToken);
    [McpServerTool(Name = "get_database_status", ReadOnly = true, Destructive = false), Description("Reports active SQL source and last index diagnostics; configured live mode does not prove connectivity.")]
    public SqlSourceState GetDatabaseStatus() => database.Status();
    [McpServerTool(Name = "refresh_database_source", ReadOnly = true, Destructive = false), Description("Reconciles SQL files against cache, including additions/changes/deletions. Live definitions are queried fresh by analyze_procedure.")]
    public Task<SqlSourceState> RefreshDatabaseSource(CancellationToken cancellationToken) => Run(() => database.RefreshAsync(cancellationToken), cancellationToken);
    [McpServerTool(Name = "find_sql_object", ReadOnly = true, Destructive = false), Description("Finds parsed file objects with members, references, source file/line and warnings in SqlFiles mode. Definition is opt-in; supports table/procedure/view/function, not every DDL object type.")]
    public Task<Page<SqlFileObject>> FindSqlObject(string query, CancellationToken cancellationToken, int offset = 0, int limit = 100, bool includeDefinition = false) =>
        Run(() => database.FindAsync(query, offset, limit, includeDefinition, cancellationToken), cancellationToken);
    private async Task<T> Run<T>(Func<Task<T>> operation, CancellationToken token)
    { await executionGate.WaitAsync(token); try { return await operation(); } finally { executionGate.Release(); } }
}
