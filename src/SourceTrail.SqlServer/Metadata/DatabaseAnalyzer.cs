using SourceTrail.Core.Contracts;
using SourceTrail.Core.Models;
namespace SourceTrail.SqlServer.Metadata;

public sealed class DatabaseAnalyzer : IProcedureAnalyzer, IDisposable
{
    private readonly DatabaseOptions options;
    private readonly SqlProcedureAnalyzer live;
    private readonly SqlFolderIndex folder;
    private readonly SemaphoreSlim gate = new(1, 1);
    private string mode;
    public DatabaseAnalyzer(DatabaseOptions options, Action<string>? log = null)
    {
        this.options = options; mode = ValidateMode(options.Mode);
        live = new(options, log); folder = new(options);
    }
    private static string ValidateMode(string mode) => mode switch
    {
        "SqlFiles" => mode, "LiveDatabase" => mode,
        _ => throw new ArgumentException("mode must be SqlFiles or LiveDatabase.")
    };
    public SqlSourceState Status() => mode == "SqlFiles" ? folder.Status : new("LiveDatabase",
        string.IsNullOrWhiteSpace(options.ConnectionString) ? "NotConfigured" : "Configured",
        null, null, null, 0, 0, 0, 0, ["Configured does not prove connectivity or metadata visibility. Live definitions are queried on each request."]);
    public async Task<SqlSourceState> SelectAsync(string selectedMode, string? sqlFolder, CancellationToken token)
    {
        selectedMode = ValidateMode(selectedMode);
        await gate.WaitAsync(token);
        try
        {
            if (selectedMode == "SqlFiles")
            {
                var path = sqlFolder ?? options.SqlFolder;
                await folder.LoadAsync(path, token); // Publish selected mode only after a successful load.
                options.SqlFolder = path;
            }
            mode = selectedMode;
            return Status();
        }
        finally { gate.Release(); }
    }
    public async Task<SqlSourceState> RefreshAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (mode == "SqlFiles") await folder.LoadAsync(folder.Status.Folder ?? options.SqlFolder, token);
            return Status();
        }
        finally { gate.Release(); }
    }
    public async Task<Page<SqlFileObject>> FindAsync(string query, int offset, int limit, bool includeDefinition, CancellationToken token)
    {
        if (offset < 0 || limit is < 1 or > 1000) throw new ArgumentException("offset >= 0 and limit 1..1000 required.");
        await gate.WaitAsync(token);
        try
        {
            if (mode != "SqlFiles") throw new InvalidOperationException("find_sql_object uses SqlFiles mode; use analyze_procedure for LiveDatabase.");
            await folder.EnsureAsync(token);
            var matches = folder.Objects.Where(o => SqlFolderIndex.Key(o).Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(SqlFolderIndex.Key).ToArray();
            return new(matches.Skip(offset).Take(limit).Select(o => includeDefinition ? o : o with { Definition = "" }).ToArray(),
                matches.Length, offset, limit, offset + limit < matches.Length, folder.Status.SnapshotId, folder.Status.Warnings);
        }
        finally { gate.Release(); }
    }
    public async Task<ProcedureAnalysis> AnalyzeAsync(string procedure, bool includeDefinition, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (mode == "LiveDatabase")
                return (await live.AnalyzeAsync(procedure, includeDefinition, token)) with { Source = mode, AnalyzedAt = DateTimeOffset.UtcNow };
            await folder.EnsureAsync(token);
            var parts = procedure.Replace("[", "").Replace("]", "").Split('.');
            if (parts.Length is < 1 or > 3 || parts.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Use name, schema.name or database.schema.name.");
            var candidates = folder.Objects.Where(o => o.Kind == "Procedure" && o.Name.Equals(parts[^1], StringComparison.OrdinalIgnoreCase)
                && (parts.Length < 2 || string.Equals(o.Schema, parts[^2], StringComparison.OrdinalIgnoreCase))
                && (parts.Length < 3 || string.Equals(o.Database, parts[^3], StringComparison.OrdinalIgnoreCase))).ToArray();
            if (candidates.Length != 1)
                return new(candidates.Length == 0 ? "NotFoundInProvidedFiles" : "Ambiguous", procedure, null, null, null, [],
                    [candidates.Length == 0 ? "Not found in provided scripts; this does not prove absence in the database." : "Multiple definitions match; select the database/schema/export."], mode, folder.Status.Folder, folder.Status.AnalyzedAt);
            var selected = candidates[0];
            var warnings = new List<string>(selected.Warnings);
            warnings.AddRange(folder.Status.Warnings);
            var dependencies = selected.References.Select(r =>
            {
                var matches = folder.Objects.Where(o => r.Server is null && o.Name.Equals(r.Name, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(o.Database, r.Database ?? selected.Database, StringComparison.OrdinalIgnoreCase)
                    && (r.Schema is null || string.Equals(o.Schema, r.Schema, StringComparison.OrdinalIgnoreCase))).ToArray();
                var resolved = matches.Length == 1 && r.Schema is not null;
                return new SqlObject(r.Server, r.Database ?? selected.Database, r.Schema ?? matches.FirstOrDefault()?.Schema,
                    r.Name, matches.Length == 1 ? matches[0].Kind : "Unknown", r.Access, resolved, r.Schema is null, matches.Length > 1, selected.File, r.Line);
            }).Distinct().ToArray();
            if (dependencies.Any(d => !d.Resolved)) warnings.Add("Unresolved references may be missing scripts, CTEs, aliases, temporary objects or cross-database references; verification required.");
            warnings.Add("Static script dependencies do not prove execution; column binding, aliases, dynamic SQL and constraints require further verification.");
            return new(selected.Warnings.Count == 0 && folder.Status.Status == "Ready" && dependencies.All(d => d.Resolved) ? "Ready" : "Partial",
                string.Join(".", selected.Schema, selected.Name), selected.Schema, selected.Database, includeDefinition ? selected.Definition : null,
                dependencies, warnings.Distinct().ToArray(), mode, selected.File, folder.Status.AnalyzedAt);
        }
        finally { gate.Release(); }
    }
    public void Dispose() { folder.Dispose(); gate.Dispose(); }
}
