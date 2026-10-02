namespace SourceTrail.Core.Models;
public sealed record SqlMember(string Name, string DataType, bool? Nullable = null, bool IsOutput = false);
public sealed record SqlReference(string? Server, string? Database, string? Schema, string Name, string Access, int Line);
public sealed record SqlFileObject(string? Database, string? Schema, string Name, string Kind, string File, int Line,
    string Definition, IReadOnlyList<SqlMember> Members, IReadOnlyList<SqlReference> References, IReadOnlyList<string> Warnings, IReadOnlyList<string>? Constraints = null);
public sealed record SqlSourceState(string Mode, string Status, string? Folder, string? SnapshotId,
    DateTimeOffset? AnalyzedAt, int Files, int Objects, int ParsedFiles, int ReusedFiles, IReadOnlyList<string> Warnings);
