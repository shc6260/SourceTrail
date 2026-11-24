using SourceTrail.Core.Contracts;
using SourceTrail.Core.Models;
namespace SourceTrail.SqlServer.Metadata;

/// <summary>선택한 SQL 자료를 공통 분석 계약으로 제공하고 모드 전환을 직렬화한다.</summary>
public sealed class DatabaseAnalyzer : IProcedureAnalyzer, IDisposable
{
    private readonly DatabaseOptions options;
    private readonly SqlFolderIndex folder;
    private readonly SemaphoreSlim gate = new(1, 1);
    private string mode;
    public DatabaseAnalyzer(DatabaseOptions options, Action<string>? log = null)
    {
        this.options = options; mode = ValidateMode(options.Mode);
        folder = new(options);
    }
    private static string ValidateMode(string mode) => mode switch
    {
        "SqlFiles" => mode,
        _ => throw new ArgumentException("Only SqlFiles is supported. Provide a local SQL folder; live DB access has been removed.")
    };
    public SqlSourceState Status() => folder.Status;
    public async Task<SqlSourceState> SelectAsync(string selectedMode, string? sqlFolder, CancellationToken token)
    {
        selectedMode = ValidateMode(selectedMode);
        await gate.WaitAsync(token);
        try
        {
            if (selectedMode == "SqlFiles")
            {
                var path = sqlFolder ?? options.SqlFolder;
                await folder.LoadAsync(path, token); // 로딩 성공 후에만 활성 모드를 교체한다.
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
            if (mode == "SqlFiles") await folder.LoadAsync(folder.Status.Folder ?? options.SqlFolder, token, verifyContent: true);
            return Status();
        }
        finally { gate.Release(); }
    }
    public async Task<SqlObjectSearchResult> FindAsync(string query, int offset, int limit, bool includeDefinition, CancellationToken token)
    {
        if (offset < 0 || limit is < 1 or > 1000) throw new ArgumentException("offset >= 0 and limit 1..1000 required.");
        await gate.WaitAsync(token);
        try
        {
            await folder.EnsureAsync(token);
            var matches = folder.Objects.Where(o => SqlFolderIndex.Key(o).Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(SqlFolderIndex.Key).ToArray();
            var warnings = new List<string>();
            // A name such as usp_update_InfoResult also prefixes usp_update_InfoResultImage, ...
            // When the exact name exists, return only it and report how many substring matches were left out.
            var exact = matches.Where(o => IsExactMatch(o, query)).ToArray();
            if (exact.Length > 0 && exact.Length < matches.Length)
            {
                warnings.Add($"Exact-name objects only; {matches.Length - exact.Length} substring matches omitted. Query a longer or different name to see them.");
                matches = exact;
            }
            var items = matches.Skip(offset).Take(limit).Select(o => includeDefinition ? o : o with { Definition = null }).ToArray();
            var itemKeys = items.Select(SqlFolderIndex.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var duplicates = matches.Where(o => itemKeys.Contains(SqlFolderIndex.Key(o)))
                .GroupBy(SqlFolderIndex.Key, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Skip(1).Any())
                .Select(group => SqlFolderIndex.DuplicateDefinitionWarning(group.Key));
            var allWarnings = warnings.Concat(items.Select(o => o.File).Distinct(StringComparer.OrdinalIgnoreCase)
                .SelectMany(folder.FileWarnings)).Concat(duplicates).Distinct(StringComparer.Ordinal).ToArray();
            var status = folder.Status;
            return new(items, matches.Length, offset, limit, items.Length < matches.Length - offset,
                status.SnapshotId, allWarnings, status.Status, status.Warnings.Count);
        }
        finally { gate.Release(); }
    }
    // Objects whose scripts reference the given name (table, view, procedure or function), found from parsed reference
    // evidence, so the target does not need a script of its own. operation filters to one operation (for example EXEC).
    public async Task<SqlReferenceSearchResult> FindReferencesAsync(string name, int offset, int limit,
        CancellationToken token, string? operation = null)
    {
        if (offset < 0 || limit is < 1 or > 1000) throw new ArgumentException("offset >= 0 and limit 1..1000 required.");
        var parts = (name ?? "").Replace("[", "").Replace("]", "").Trim().Split('.');
        if (parts.Length is < 1 or > 3 || parts.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Use name, schema.name or database.schema.name.");
        await gate.WaitAsync(token);
        try
        {
            await folder.EnsureAsync(token);
            var sites = new List<(SqlFileObject Owner, SqlReference Reference, string Operation)>();
            foreach (var owner in folder.Objects)
                foreach (var reference in owner.References)
                {
                    if (reference.Server is not null || !reference.Name.Equals(parts[^1], StringComparison.OrdinalIgnoreCase)) continue;
                    if (parts.Length > 1 && reference.Schema is not null && !reference.Schema.Equals(parts[^2], StringComparison.OrdinalIgnoreCase)) continue;
                    if (parts.Length > 2 && reference.Database is not null && !reference.Database.Equals(parts[^3], StringComparison.OrdinalIgnoreCase)) continue;
                    var kind = OperationOf(reference.Access);
                    if (operation is null || kind.Equals(operation, StringComparison.OrdinalIgnoreCase)) sites.Add((owner, reference, kind));
                }
            var ordered = sites.Select(site => new SqlReferenceSite(site.Owner.Database, site.Owner.Schema, site.Owner.Name,
                    site.Owner.Kind, site.Operation, site.Owner.File, site.Reference.Line)).Distinct()
                .OrderBy(site => OperationOrder(site.Operation)).ThenBy(site => site.Schema, StringComparer.OrdinalIgnoreCase)
                .ThenBy(site => site.Name, StringComparer.OrdinalIgnoreCase).ThenBy(site => site.File, StringComparer.OrdinalIgnoreCase)
                .ThenBy(site => site.Line).ToArray();
            var warnings = new List<string>();
            var unknown = ordered.Count(site => site.Operation == "Unknown");
            if (unknown > 0) warnings.Add($"{unknown} reference(s) have an operation that could not be determined (Unknown); read the source line.");
            // 별칭 대상을 FROM에서 확정하지 못한 UPDATE/DELETE가 결과 개체에 있을 때만 경고한다.
            var unresolved = sites.Where(site => site.Operation is "UPDATE" or "DELETE").Select(site => site.Owner)
                .Where(owner => owner.Warnings.Any(w => w.Contains("Write target alias"))).Distinct().Count();
            if (unresolved > 0) warnings.Add($"{unresolved} object(s) contain an UPDATE/DELETE whose alias target could not be resolved to a table; read the source.");
            var status = folder.Status;
            if (status.Status != "Ready") warnings.Add("The SQL index is Partial: unreadable or unparsable scripts may hide references. Use get_database_status for details.");
            var counts = ordered.GroupBy(site => site.Operation).OrderBy(group => OperationOrder(group.Key))
                .ToDictionary(group => group.Key, group => group.Count());
            var items = ordered.Skip(offset).Take(limit).ToArray();
            return new(items, ordered.Length, offset, limit, (long)offset + limit < ordered.Length, counts,
                status.SnapshotId, warnings, status.Status, status.Warnings.Count);
        }
        finally { gate.Release(); }
    }
    // Reference evidence from the SQL parser: Insert/UpdateCandidate/DeleteCandidate/MergeCandidate are statement targets,
    // ReadCandidate is any FROM/JOIN table, Execute is EXEC or a schema-qualified function call.
    private static string OperationOf(string access) => access switch
    {
        "Insert" => "INSERT",
        "UpdateCandidate" => "UPDATE",
        "DeleteCandidate" => "DELETE",
        "MergeCandidate" => "MERGE",
        "Execute" => "EXEC",
        "ReadCandidate" => "SELECT",
        "ForeignKey" => "ForeignKey",
        _ => "Unknown"
    };
    private static int OperationOrder(string operation) => operation switch
    {
        "INSERT" => 0, "UPDATE" => 1, "DELETE" => 2, "MERGE" => 3, "EXEC" => 4, "SELECT" => 5, "ForeignKey" => 6, _ => 7
    };
    // Accepts name, schema.name or database.schema.name (brackets ignored, case-insensitive); an omitted schema matches any.
    private static bool IsExactMatch(SqlFileObject o, string query)
    {
        var parts = query.Replace("[", "").Replace("]", "").Trim().Split('.');
        if (parts.Length is < 1 or > 3 || parts.Any(string.IsNullOrWhiteSpace)) return false;
        return o.Name.Equals(parts[^1], StringComparison.OrdinalIgnoreCase)
            && (parts.Length < 2 || string.Equals(o.Schema, parts[^2], StringComparison.OrdinalIgnoreCase))
            && (parts.Length < 3 || string.Equals(o.Database, parts[^3], StringComparison.OrdinalIgnoreCase));
    }
    public async Task<ProcedureAnalysis> AnalyzeAsync(string procedure, bool includeDefinition, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            return await AnalyzeFilesAsync(procedure, includeDefinition, token);
        }
        finally { gate.Release(); }
    }
    public void Dispose() { folder.Dispose(); gate.Dispose(); }

    // 선택한 자료만 분석하며, 파일 분석 실패 시 실제 DB로 자동 전환하지 않는다.
    private async Task<ProcedureAnalysis> AnalyzeFilesAsync(string procedure, bool includeDefinition, CancellationToken token)
    {
        await folder.EnsureAsync(token);
        var index = folder.Status;
        var candidates = FindProcedures(procedure);
        if (candidates.Length != 1)
            return new(candidates.Length == 0 ? "NotFoundInProvidedFiles" : "Ambiguous", procedure, null, null, null, [],
                [candidates.Length == 0 ? "Not found in provided scripts; this does not prove absence in the database." : "Multiple definitions match; select the database/schema/export."], mode, index.Folder, index.AnalyzedAt)
            {
                SnapshotId = index.SnapshotId, IndexStatus = index.Status, IndexWarningCount = index.Warnings.Count
            };
        var selected = candidates[0];
        var warnings = new List<string>(selected.Warnings);
        warnings.AddRange(folder.FileWarnings(selected.File));
        var dependencies = selected.References.Select(reference => ResolveDependency(selected, reference))
            .Distinct().ToArray();
        if (dependencies.Any(d => !d.Resolved)) warnings.Add("Unresolved references may be missing scripts, CTEs, aliases, temporary objects or cross-database references; verification required.");
        warnings.Add("Static script dependencies do not prove execution; column binding, aliases, dynamic SQL and constraints require further verification.");
        return new(selected.Warnings.Count == 0 && index.Status == "Ready" && dependencies.All(d => d.Resolved) ? "Ready" : "Partial",
            string.Join(".", selected.Schema, selected.Name), selected.Schema, selected.Database, includeDefinition ? selected.Definition : null,
            dependencies, warnings.Distinct().ToArray(), mode, selected.File, index.AnalyzedAt)
        {
            SnapshotId = index.SnapshotId, IndexStatus = index.Status, IndexWarningCount = index.Warnings.Count
        };
    }


    private SqlFileObject[] FindProcedures(string procedure)
    {
        var parts = procedure.Replace("[", "").Replace("]", "").Split('.');
        if (parts.Length is < 1 or > 3 || parts.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Use name, schema.name or database.schema.name.");
        return folder.Objects.Where(o => o.Kind == "Procedure" && o.Name.Equals(parts[^1], StringComparison.OrdinalIgnoreCase)
            && (parts.Length < 2 || string.Equals(o.Schema, parts[^2], StringComparison.OrdinalIgnoreCase))
            && (parts.Length < 3 || string.Equals(o.Database, parts[^3], StringComparison.OrdinalIgnoreCase))).ToArray();
    }

    // 이름이 하나만 맞아도 스키마가 생략되었다면 확정하지 않고 호출자 의존 후보로 남긴다.
    private SqlObject ResolveDependency(SqlFileObject selected, SqlReference r)
    {
        var matches = folder.Objects.Where(o => r.Server is null && o.Name.Equals(r.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(o.Database, r.Database ?? selected.Database, StringComparison.OrdinalIgnoreCase)
            && (r.Schema is null || string.Equals(o.Schema, r.Schema, StringComparison.OrdinalIgnoreCase))).ToArray();
        var resolved = matches.Length == 1 && r.Schema is not null;
        return new SqlObject(r.Server, r.Database ?? selected.Database, r.Schema ?? matches.FirstOrDefault()?.Schema,
            r.Name, matches.Length == 1 ? matches[0].Kind : "Unknown", r.Access, resolved, r.Schema is null, matches.Length > 1, selected.File, r.Line);
    }

}
