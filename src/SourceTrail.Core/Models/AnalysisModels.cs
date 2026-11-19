using System.Text.Json.Serialization;

namespace SourceTrail.Core.Models;

public sealed record AnalysisDiagnostic(string Severity, string Message, string? Project = null);
public sealed record ProjectState(string Name, string? File, int Documents, int? CompilationErrors = null);
public sealed record SolutionState(string Status, string? SolutionPath, string? SnapshotId,
    DateTimeOffset? LoadedAt, IReadOnlyList<ProjectState> Projects,
    IReadOnlyList<AnalysisDiagnostic> Diagnostics);
public sealed record SymbolInfo(string Id, string Name, string SymbolKind, string Namespace,
    string FullName, string Signature, string Project, string? File, int? Line,
    string? ContainingType, bool IsUi);
public sealed record Page<T>(IReadOnlyList<T> Items, int Total, int Offset, int Limit,
    bool Truncated, string? SnapshotId, IReadOnlyList<string> Warnings);
public sealed record ReferenceInfo(SymbolInfo? ReferencingSymbol, string Project, string File,
    int Line, int Column, string RelationType);
public sealed record SymbolOverview(IReadOnlyList<SymbolInfo> Types, IReadOnlyList<SymbolInfo> Fields,
    IReadOnlyList<SymbolInfo> Properties, IReadOnlyList<SymbolInfo> Methods,
    IReadOnlyList<SymbolInfo> Constructors, IReadOnlyList<SymbolInfo> Events,
    string? SnapshotId, IReadOnlyList<string> Warnings);
public sealed record TextMatch(string File, int Line, int Column, string Preview);
public sealed record EventHandlerMatch(string Control, string Event, string Handler, string BindingFile,
    int BindingLine, string? HandlerFile, int? HandlerLine, string? SymbolId);
public sealed record UiActionEvidence(string Kind, string Text, string File, int Line);
public sealed record UiActionCandidate(string Screen, string? Title, string Control, string? Caption,
    string Event, string Handler, IReadOnlyList<UiActionEvidence> Evidence);
public sealed record UiActionSearchResult(string Status, string IndexState, int IndexedScreens,
    int Total, bool Truncated, IReadOnlyList<UiActionCandidate> Items);
public sealed record ProcedureUsage(string Procedure, string File, int Line,
    SymbolInfo? ContainingSymbol, string RelationType, bool IsVerifiedCall, string Evidence);
public sealed record ProcedureCallRule(string TypeName, string MethodName, int ArgumentIndex);
public sealed record DatabaseCallSite(string Method, string File, int Line);
public sealed record MethodCallCandidate(string Name, string Expression, string File, int Line);
public sealed record DatabaseCall(string Kind, IReadOnlyList<string> Targets, string Execution,
    DatabaseCallSite Site, string? Condition = null);
public sealed record DatabaseCallSummary(string Status, string? SnapshotId, DatabaseCallSite Entry,
    IReadOnlyList<DatabaseCall> Calls, IReadOnlyList<DatabaseCallSite> Unresolved,
    IReadOnlyList<MethodCallCandidate> MethodCandidates, bool Truncated);
public sealed record SqlObject(string? Server, string? Database, string? Schema, string Name,
    string Kind, string Access, bool Resolved, bool CallerDependent, bool Ambiguous, string? File = null, int? Line = null);
public sealed record ProcedureAnalysis(string Status, string Procedure, string? Schema,
    string? Database, string? Definition, IReadOnlyList<SqlObject> Dependencies,
    IReadOnlyList<string> Warnings, string Source = "SqlFiles", string? SourcePath = null, DateTimeOffset? AnalyzedAt = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SnapshotId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IndexStatus { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? IndexWarningCount { get; init; }
}
public sealed class AnalysisOptions
{
    public string SolutionPath { get; set; } = "";
    public string CodeCacheDirectory { get; set; } = "";
    // Opt-in: notifications may arrive after a request; false preserves per-request scans.
    public bool WatchCodeFiles { get; set; }
    public int CodeRescanIntervalSeconds { get; set; } = 30;
    public string[] AdditionalSearchRoots { get; set; } = [];
    public int MaxResults { get; set; } = 20;
    public ProcedureCallRule[] ProcedureCallRules { get; set; } = [];
}
public sealed class DatabaseOptions
{
    public string Mode { get; set; } = "SqlFiles";
    public string SqlFolder { get; set; } = "";
    public string CacheDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SourceTrail", "cache");
    public bool WatchFiles { get; set; } = true;
}
