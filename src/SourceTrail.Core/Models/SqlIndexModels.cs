namespace SourceTrail.Core.Models;
public sealed record SqlMember(string Name, string DataType, bool? Nullable = null, bool IsOutput = false);
public sealed record SqlReference(string? Server, string? Database, string? Schema, string Name, string Access, int Line);
public sealed record SqlFileObject(string? Database, string? Schema, string Name, string Kind, string File, int Line,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Definition, IReadOnlyList<SqlMember> Members, IReadOnlyList<SqlReference> References, IReadOnlyList<string> Warnings, IReadOnlyList<string>? Constraints = null);
public sealed record SqlObjectSearchResult(IReadOnlyList<SqlFileObject> Items, int Total, int Offset, int Limit,
    bool Truncated, string? SnapshotId, IReadOnlyList<string> Warnings, string IndexStatus, int IndexWarningCount);
public sealed record SqlSourceState(string Mode, string Status, string? Folder, string? SnapshotId,
    DateTimeOffset? AnalyzedAt, int Files, int Objects, int ParsedFiles, int ReusedFiles, IReadOnlyList<string> Warnings);
// A SQL object that references the searched object. Name/Schema/Kind describe the referencing object;
// Operation is INSERT, UPDATE, DELETE, MERGE, EXEC, SELECT, ForeignKey or Unknown; File/Line locate the reference.
public sealed record SqlReferenceSite(string? Database, string? Schema, string Name, string Kind, string Operation, string File, int Line);
public sealed record SqlReferenceSearchResult(IReadOnlyList<SqlReferenceSite> Items, int Total, int Offset, int Limit,
    bool Truncated, IReadOnlyDictionary<string, int> OperationCounts, string? SnapshotId, IReadOnlyList<string> Warnings,
    string IndexStatus, int IndexWarningCount);
