namespace SourceTrail.Core.Models;

public sealed record AnalysisDiagnostic(string Severity, string Message, string? Project = null);
public sealed record ProjectState(string Name, string? File, int Documents, int CompilationErrors);
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
public sealed record ProcedureUsage(string Procedure, string File, int Line,
    SymbolInfo? ContainingSymbol, string RelationType, bool IsVerifiedCall, string Evidence);
public sealed record ProcedureCallRule(string TypeName, string MethodName, int ArgumentIndex);
public sealed record CodeRelation(SymbolInfo From, SymbolInfo To, string RelationType,
    string File, int Line, string Evidence);
public sealed record CodeExpansion(SymbolInfo Symbol, IReadOnlyList<CodeRelation> Relations,
    IReadOnlyList<ProcedureUsage> ProcedureUsages, IReadOnlyList<string> Warnings);
public sealed record SqlObject(string? Server, string? Database, string? Schema, string Name,
    string Kind, string Access, bool Resolved, bool CallerDependent, bool Ambiguous, string? File = null, int? Line = null);
public sealed record ProcedureAnalysis(string Status, string Procedure, string? Schema,
    string? Database, string? Definition, IReadOnlyList<SqlObject> Dependencies,
    IReadOnlyList<string> Warnings, string Source = "LiveDatabase", string? SourcePath = null, DateTimeOffset? AnalyzedAt = null);
public sealed record FlowNode(string Id, string Name, string Kind, string? Project = null,
    string? File = null, int? Line = null, string? Schema = null, string? Database = null);
public sealed record FlowEdge(string From, string To, string RelationType, string Evidence,
    string? File = null, int? Line = null);
public sealed record FlowResult(string Status, string Direction, string? SnapshotId,
    IReadOnlyList<FlowNode> Nodes, IReadOnlyList<FlowEdge> Edges, bool Truncated,
    IReadOnlyList<string> Warnings);

public sealed class AnalysisOptions
{
    public string SolutionPath { get; set; } = "";
    public string[] AdditionalSearchRoots { get; set; } = [];
    public int MaxDepth { get; set; } = 5;
    public int MaxNodes { get; set; } = 200;
    public int MaxResults { get; set; } = 100;
    public ProcedureCallRule[] ProcedureCallRules { get; set; } = [];
}
public sealed class DatabaseOptions
{
    public string Mode { get; set; } = "LiveDatabase";
    public string SqlFolder { get; set; } = "";
    public string CacheDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SourceTrail", "cache");
    public bool WatchFiles { get; set; } = true;
    public string ConnectionString { get; set; } = "";
    public int CommandTimeoutSeconds { get; set; } = 15;
}
