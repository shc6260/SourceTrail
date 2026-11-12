using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using SourceTrail.Core.Contracts;
using SourceTrail.Core;
using SourceTrail.Core.Flow;
using SourceTrail.Core.Models;

namespace SourceTrail.Mcp.Tools;

[McpServerToolType]
public sealed class AnalysisTools(ICodeAnalyzer code, IProcedureAnalyzer sql, FlowAnalyzer flow,
    ILogger<AnalysisTools> logger, AnalysisOptions options, SemaphoreSlim executionGate)
{
    [McpServerTool(Name = "load_solution", ReadOnly = true, Destructive = false), Description("Loads an existing absolute .sln path. Returns project diagnostics and a snapshot ID; source files are not changed.")]
    public Task<SolutionState> LoadSolution(string solutionPath, CancellationToken cancellationToken) =>
        Run(cancellationToken, "load_solution", () => code.LoadAsync(solutionPath, cancellationToken));

    [McpServerTool(Name = "get_analysis_status", ReadOnly = true, Destructive = false), Description("Returns the active solution snapshot and load diagnostics. A configured default solution loads on the first C# analysis request.")]
    public SolutionState GetAnalysisStatus() => code.Status();

    [McpServerTool(Name = "reload_solution", ReadOnly = true, Destructive = false), Description("Reloads the active solution from disk and invalidates cached symbols and relationships.")]
    public Task<SolutionState> ReloadSolution(CancellationToken cancellationToken) =>
        Run(cancellationToken, "reload_solution", () => code.ReloadAsync(cancellationToken));

    [McpServerTool(Name = "find_symbol", ReadOnly = true, Destructive = false), Description("Finds declared C# symbols. Pass the returned exact ID to other tools to disambiguate projects and overloads.")]
    public Task<Page<SymbolInfo>> FindSymbol(string query, CancellationToken cancellationToken, int offset = 0, int? limit = null) =>
        Run(cancellationToken, "find_symbol", () => code.FindSymbolsAsync(query, offset, limit ?? options.MaxResults, cancellationToken));

    [McpServerTool(Name = "get_symbol_overview", ReadOnly = true, Destructive = false), Description("Returns type/member structure without source bodies. symbol accepts an exact type ID or an absolute file path.")]
    public Task<SymbolOverview> GetSymbolOverview(string symbol, CancellationToken cancellationToken) =>
        Run(cancellationToken, "get_symbol_overview", () => code.OverviewAsync(symbol, cancellationToken));

    [McpServerTool(Name = "find_references", ReadOnly = true, Destructive = false), Description("Uses Roslyn to find actual symbol references. A reference alone is not an invocation.")]
    public Task<Page<ReferenceInfo>> FindReferences(string symbol, CancellationToken cancellationToken, int offset = 0, int? limit = null) =>
        Run(cancellationToken, "find_references", () => code.ReferencesAsync(symbol, offset, limit ?? options.MaxResults, cancellationToken));

    [McpServerTool(Name = "search_text", ReadOnly = true, Destructive = false), Description("Searches current disk files. Set directory to a solution-relative folder and extension (e.g. .cs) to narrow the scan. With no directory, searches the solution and configured additional roots. Results are text candidates.")]
    public Task<Page<TextMatch>> SearchText(string text, CancellationToken cancellationToken, int offset = 0, int? limit = null,
        string? directory = null, string? extension = null) =>
        Run(cancellationToken, "search_text",
            () => code.SearchTextAsync(text, offset, limit ?? options.MaxResults, cancellationToken, directory, extension), requiresCode: false);

    [McpServerTool(Name = "find_event_handlers", ReadOnly = true, Destructive = false), Description("Finds WinForms Designer event subscription candidates in InitializeComponent for a form's C# file. Use query to filter by control, event or handler name. Returns binding and handler source locations and a symbol ID when the existing symbol index identifies a unique handler. Can read source locations without opening Roslyn's solution workspace.")]
    public Task<Page<EventHandlerMatch>> FindEventHandlers(string file, CancellationToken cancellationToken,
        string? query = null, int offset = 0, int? limit = null) =>
        Run(cancellationToken, "find_event_handlers",
            () => code.FindEventHandlersAsync(file, query, offset, limit ?? Math.Min(options.MaxResults, 20), cancellationToken));

    [McpServerTool(Name = "find_procedure_usage", ReadOnly = true, Destructive = false), Description("Maps procedure string/constant candidates to containing Roslyn symbols and distinguishes configured calls from unverified candidates. Comments are not treated as calls.")]
    public Task<Page<ProcedureUsage>> FindProcedureUsage(string procedure, CancellationToken cancellationToken, int offset = 0, int? limit = null) =>
        Run(cancellationToken, "find_procedure_usage", () => code.ProcedureUsagesAsync(procedure, offset, limit ?? options.MaxResults, cancellationToken));

    [McpServerTool(Name = "analyze_procedure", ReadOnly = true, Destructive = false), Description("Analyzes a procedure from the selected SQL source (SqlFiles or LiveDatabase). Returns provenance and direct dependencies; definition is opt-in. Does not execute SQL.")]
    public Task<ProcedureAnalysis> AnalyzeProcedure(string procedure, CancellationToken cancellationToken, bool includeDefinition = false) =>
        Run(cancellationToken, "analyze_procedure", () => sql.AnalyzeAsync(procedure, includeDefinition, cancellationToken), requiresCode: false);

    [McpServerTool(Name = "find_database_calls", ReadOnly = true, Destructive = false), Description("From one C# method, returns source-proven SQL execution sites and target names. Use a qualified method name or exact symbol ID. Locations are [zero-based index into files, one-based line]; relative paths are under solutionRoot. One static path per discovered method; unresolved sites and traversal limits are reported. Does not access the database.")]
    public Task<CompactDatabaseCallSummary> FindDatabaseCalls(string symbol, CancellationToken cancellationToken,
        int maxDepth = 3, int maxMethods = 32) =>
        Run(cancellationToken, "find_database_calls", async () =>
        {
            var result = await ((IDatabaseCallAnalyzer)code)
                .FindDatabaseCallsAsync(symbol, maxDepth, maxMethods, cancellationToken);
            var root = Path.GetDirectoryName(code.Status().SolutionPath);
            var files = new List<string>();
            var indexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int[] Location(DatabaseCallSite site)
            {
                if (!indexes.TryGetValue(site.File, out int index))
                {
                    indexes[site.File] = index = files.Count;
                    var relative = root is null ? site.File : Path.GetRelativePath(root, site.File);
                    files.Add(relative.StartsWith("..", StringComparison.Ordinal) ? site.File : relative);
                }
                return [index, site.Line];
            }
            var entry = Location(result.Entry);
            var calls = result.Calls.Select(call => new CompactDatabaseCall(call.Kind, call.Targets, call.Execution,
                Location(call.Site), call.Path.Select(Location).ToArray(), call.Condition)).ToArray();
            var unresolved = result.Unresolved.Select(Location).ToArray();
            return new CompactDatabaseCallSummary(result.Status, result.SnapshotId, root, files, entry,
                calls, unresolved, result.MethodsVisited, result.Truncated, result.DiagnosticCount);
        });

    [McpServerTool(Name = "trace_code_to_database", ReadOnly = true, Destructive = false), Description("Traces resolved source invocations and recognized procedure usages forward to SQL metadata. Returns a bounded static graph with evidence.")]
    public Task<FlowResult> TraceCodeToDatabase(string symbol, CancellationToken cancellationToken, int? maxDepth = null, int? maxNodes = null) =>
        Run(cancellationToken, "trace_code_to_database", () => flow.TraceCodeAsync(symbol, maxDepth ?? options.MaxDepth, maxNodes ?? options.MaxNodes, cancellationToken));

    [McpServerTool(Name = "trace_procedure_to_ui", ReadOnly = true, Destructive = false), Description("Traces recognized procedure usage back to caller symbols and event subscriptions. Does not infer a remote WCF implementation.")]
    public Task<FlowResult> TraceProcedureToUi(string procedure, CancellationToken cancellationToken, int? maxDepth = null, int? maxNodes = null) =>
        Run(cancellationToken, "trace_procedure_to_ui", () => flow.TraceProcedureAsync(procedure, maxDepth ?? options.MaxDepth, maxNodes ?? options.MaxNodes, cancellationToken));

    [McpServerTool(Name = "trace_code_to_database_compact", ReadOnly = true, Destructive = false), Description("Same bounded static trace as trace_code_to_database, with lossless dictionary references for edges. SchemaVersion 1. Nodes retain exact symbol IDs; relation, evidence and file indexes are zero-based. No call sites or warnings are removed.")]
    public Task<CompactFlowResult> TraceCodeToDatabaseCompact(string symbol, CancellationToken cancellationToken, int? maxDepth = null, int? maxNodes = null) =>
        Run(cancellationToken, "trace_code_to_database_compact", async () =>
            FlowCompactor.Compact(await flow.TraceCodeAsync(symbol, maxDepth ?? options.MaxDepth, maxNodes ?? options.MaxNodes, cancellationToken)));

    [McpServerTool(Name = "trace_procedure_to_ui_compact", ReadOnly = true, Destructive = false), Description("Same bounded static trace as trace_procedure_to_ui, with lossless dictionary references for edges. SchemaVersion 1. Nodes retain exact symbol IDs; relation, evidence and file indexes are zero-based. Does not infer remote WCF implementations.")]
    public Task<CompactFlowResult> TraceProcedureToUiCompact(string procedure, CancellationToken cancellationToken, int? maxDepth = null, int? maxNodes = null) =>
        Run(cancellationToken, "trace_procedure_to_ui_compact", async () =>
            FlowCompactor.Compact(await flow.TraceProcedureAsync(procedure, maxDepth ?? options.MaxDepth, maxNodes ?? options.MaxNodes, cancellationToken)));

    [McpServerTool(Name = "trace_procedure_path", ReadOnly = true, Destructive = false), Description("Given an exact C# symbol ID and qualified procedure name, returns one static path with call-site evidence. If needed, restarts bounded traversal from discovered WCF host candidates with separate limits and joins on the server symbol. Direct SQL dependencies retain writes, executions and unclassified references; read-only entries are capped at eight. NotConnected keeps SQL evidence separate. Runtime dispatch and execution require verification.")]
    public Task<ProcedurePathResult> TraceProcedurePath(string symbol, string procedure, CancellationToken cancellationToken,
        int? maxDepth = null, int? maxNodes = null) =>
        Run(cancellationToken, "trace_procedure_path", () => flow.TraceProcedurePathAsync(symbol, procedure,
            maxDepth ?? options.MaxDepth, maxNodes ?? Math.Min(80, options.MaxNodes), cancellationToken));

    private async Task<T> Run<T>(CancellationToken cancellationToken, string tool, Func<Task<T>> operation, bool requiresCode = true)
    {
        var queuedAt = Stopwatch.GetTimestamp();
        using (AnalysisTelemetry.Measure("tool.queue")) await executionGate.WaitAsync(cancellationToken);
        var queueMs = Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds;
        var timer = Stopwatch.StartNew();
        try
        {
            if (requiresCode && tool is not "load_solution" and not "reload_solution")
            {
                if (code.Status().SolutionPath is null && !string.IsNullOrWhiteSpace(options.SolutionPath))
                {
                    using var load = AnalysisTelemetry.Measure("tool.code.default_load");
                    await code.LoadAsync(options.SolutionPath, cancellationToken);
                }
                else if (code is IRefreshableCodeAnalyzer refreshable)
                {
                    using var freshness = AnalysisTelemetry.Measure("tool.code.freshness");
                    await refreshable.EnsureFreshAsync(cancellationToken);
                }
            }
            using var analysis = AnalysisTelemetry.Measure("tool.operation");
            return await operation();
        }
        catch (Exception error)
        {
            logger.LogWarning("{Tool} failed ({ErrorType}).", tool, error.GetType().Name);
            throw;
        }
        finally
        {
            var executionMs = timer.Elapsed.TotalMilliseconds;
            executionGate.Release();
            logger.LogInformation("Tool {Tool} timing: queue {QueueMs:F3} ms, execution {ExecutionMs:F3} ms.",
                tool, queueMs, executionMs);
        }
    }
}
