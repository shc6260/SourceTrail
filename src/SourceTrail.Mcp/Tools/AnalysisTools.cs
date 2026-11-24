using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using SourceTrail.Core.Contracts;
using SourceTrail.Core;
using SourceTrail.Core.Models;
using SourceTrail.SqlServer.Metadata;

namespace SourceTrail.Mcp.Tools;

[McpServerToolType]
public sealed class AnalysisTools(ICodeAnalyzer code,
    ILogger<AnalysisTools> logger, AnalysisOptions options, SemaphoreSlim executionGate, ServerOptions server,
    DatabaseAnalyzer? database = null)
{
    [McpServerTool(Name = "load_solution", ReadOnly = true, Destructive = false), Description("Indexes an existing absolute source folder or .sln directory using files and C# syntax only. No compiler, MSBuild, project evaluation or reference DLL loading. Returns source-index state; CompilationErrors is not evaluated. Disabled on the shared HTTP server unless Server:AllowTargetChanges=true, because it changes the target for every client.")]
    public Task<SolutionState> LoadSolution(string solutionPath, CancellationToken cancellationToken)
    {
        TargetChangeGuard.EnsureAllowed(server, "load_solution", "Analysis:SolutionPath");
        return Run(cancellationToken, "load_solution", () => code.LoadAsync(solutionPath, cancellationToken));
    }

    [McpServerTool(Name = "get_analysis_status", ReadOnly = true, Destructive = false), Description("Returns source-index state and syntax/read diagnostics. Ready means indexing completed, not successful compilation. The configured folder/solution is indexed on first declaration search.")]
    public SolutionState GetAnalysisStatus() => code.Status();

    [McpServerTool(Name = "reload_solution", ReadOnly = true, Destructive = false), Description("Reconciles source files with the index. IDs for unchanged declarations remain stable. No compiler or MSBuild.")]
    public Task<SolutionState> ReloadSolution(CancellationToken cancellationToken) =>
        Run(cancellationToken, "reload_solution", () => code.ReloadAsync(cancellationToken));

    [McpServerTool(Name = "find_symbol", ReadOnly = true, Destructive = false), Description("Finds C# declaration candidates by syntax-based name/qualified-name substring, parsing files on demand. Qualified names prioritize matching filenames and report a non-exhaustive scope warning; if none match, scans source candidates more broadly. Names/overloads are not semantically bound. Use names verified in source, not a task phrase; start UI discovery with find_ui_actions. Pass the returned exact source ID to subsequent tools.")]
    public Task<Page<SymbolInfo>> FindSymbol(string query, CancellationToken cancellationToken, int offset = 0, int? limit = null) =>
        Run(cancellationToken, "find_symbol", () => code.FindSymbolsAsync(query, offset, limit ?? options.MaxResults, cancellationToken));

    [McpServerTool(Name = "get_symbol_overview", ReadOnly = true, Destructive = false), Description("Returns type/member structure without source bodies. symbol accepts an exact type ID or an absolute file path. By default each member has only id, name, symbolKind, line and signature (file is stated once, and per member only when it differs); set detail=true for the full declaration records (namespace, fullName, project, file, containingType, isUi). baseTypes lists the parents written in source; derivedTypes lists types (id, name, file, line) whose source base list names this type, matched by simple name from syntax only (returned when symbol is a type ID or a file that declares one type). memberQuery keeps only members whose name contains the text (case-insensitive); types are not filtered.")]
    public Task<object> GetSymbolOverview(string symbol, CancellationToken cancellationToken, bool detail = false, string? memberQuery = null) =>
        Run<object>(cancellationToken, "get_symbol_overview", async () =>
        {
            var overview = await code.OverviewAsync(symbol, cancellationToken, memberQuery);
            return detail ? overview : overview.ToBrief();
        }, requiresCode: !Path.IsPathFullyQualified(symbol));

    [McpServerTool(Name = "find_reference_candidates", ReadOnly = true, Destructive = false), Description("Finds same-name identifier uses in source syntax. These are name-reference candidates, NOT bound symbol references or proven invocations. Read source to disambiguate types and overloads.")]
    public Task<Page<ReferenceInfo>> FindReferences(string symbol, CancellationToken cancellationToken, int offset = 0, int? limit = null) =>
        Run(cancellationToken, "find_references", () => code.ReferencesAsync(symbol, offset, limit ?? options.MaxResults, cancellationToken));

    [McpServerTool(Name = "search_text", ReadOnly = true, Destructive = false), Description("Literal case-insensitive substring search in current disk files; does not tokenize task phrases or expand synonyms. For screen/action discovery use find_ui_actions first. Narrow directory to a known solution-relative folder and extension to .cs, .Designer.cs, .config, .xml or .sql. No wildcards. With no directory, scans the solution and additional roots in full even when zero matches. Results are text candidates.")]
    public Task<Page<TextMatch>> SearchText(string text, CancellationToken cancellationToken, int offset = 0, int? limit = null,
        string? directory = null, string? extension = null) =>
        Run(cancellationToken, "search_text",
            () => code.SearchTextAsync(text, offset, limit ?? options.MaxResults, cancellationToken, directory, extension), requiresCode: false);

    [McpServerTool(Name = "find_event_handlers", ReadOnly = true, Destructive = false), Description("Finds WinForms Designer event subscription candidates in InitializeComponent for a form's C# file. Use query to filter by control, event or handler name. Returns binding and handler source locations and a symbol ID when the existing symbol index identifies a unique handler. Can read source locations without opening Roslyn's solution workspace.")]
    public Task<Page<EventHandlerMatch>> FindEventHandlers(string file, CancellationToken cancellationToken,
        string? query = null, int offset = 0, int? limit = null) =>
        Run(cancellationToken, "find_event_handlers",
            () => code.FindEventHandlersAsync(file, query, offset, limit ?? Math.Min(options.MaxResults, 20), cancellationToken), requiresCode: false);

    [McpServerTool(Name = "find_ui_actions", ReadOnly = true, Destructive = false), Description("Start here for WinForms screen/action discovery. Supply a short screen + action phrase such as '접수 저장' (also accepts '접수저장'), not a whole investigation prompt. Matches menu/form/control captions, identifiers, supported action aliases and Designer bindings. Read returned handler source to verify service names/contracts, then use find_symbol and exact IDs for server/DB tracing. Static candidates with evidence; no C# compilation or runtime proof.")]
    public Task<UiActionSearchResult> FindUiActions(string query, CancellationToken cancellationToken, int limit = 5) =>
        Run(cancellationToken, "find_ui_actions",
            () => ((IUiActionAnalyzer)code).FindUiActionsAsync(query, limit, cancellationToken), requiresCode: false);

    [McpServerTool(Name = "find_procedure_usage", ReadOnly = true, Destructive = false), Description("Finds procedure-name string literals and containing source declarations. All matches are candidates; comments are excluded and execution is not inferred. sqlCallers lists the SQL procedures that EXEC this procedure (name, kind, file, line) from the SQL index, with the same offset/limit; if the SQL index is unavailable only a warning is added.")]
    public Task<ProcedureUsagePage> FindProcedureUsage(string procedure, CancellationToken cancellationToken, int offset = 0, int? limit = null) =>
        Run(cancellationToken, "find_procedure_usage", async () =>
        {
            var page = await code.ProcedureUsagesAsync(procedure, offset, limit ?? options.MaxResults, cancellationToken);
            IReadOnlyList<SqlReferenceSite> callers = []; int callerTotal = 0; var warnings = page.Warnings;
            try
            {
                if (database is null) throw new InvalidOperationException("SQL index is not configured.");
                var sql = await database.FindReferencesAsync(procedure, offset, limit ?? options.MaxResults, cancellationToken, "EXEC");
                callers = sql.Items; callerTotal = sql.Total;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogWarning("find_procedure_usage SQL callers unavailable ({ErrorType}).", error.GetType().Name);
                warnings = warnings.Append("SQL callers unavailable: " + (error is InvalidOperationException or ArgumentException ? error.Message : error.GetType().Name)).ToArray();
            }
            return new ProcedureUsagePage(page.Items, page.Total, page.Offset, page.Limit, page.Truncated, page.SnapshotId, warnings, callers, callerTotal);
        });

    [McpServerTool(Name = "find_database_calls", ReadOnly = true, Destructive = false), Description("Returns DB command/configuration candidates and direct method-call keywords from ONE method, using source syntax only. Does not recursively traverse calls, bind receivers/overloads or infer WCF dispatch. Adapter names and row-state configurations are candidates. Read returned source and search the next method explicitly. No DB connection.")]
    public Task<DatabaseCallSummary> FindDatabaseCalls(string symbol, CancellationToken cancellationToken, int limit = 20) =>
        Run(cancellationToken, "find_database_calls", () => ((IDatabaseCallAnalyzer)code).FindDatabaseCallsAsync(symbol, limit, cancellationToken));
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
        catch (ArgumentException error)
        {
            logger.LogWarning("{Tool} rejected invalid input ({ErrorType}).", tool, error.GetType().Name);
            throw new ModelContextProtocol.McpException(error.Message);
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
