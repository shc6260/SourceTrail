using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using SourceTrail.Core.Contracts;
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

    [McpServerTool(Name = "get_analysis_status", ReadOnly = true, Destructive = false), Description("Returns the active solution snapshot and load diagnostics.")]
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

    [McpServerTool(Name = "search_text", ReadOnly = true, Destructive = false), Description("Searches .cs/.config/.xml/.sql under the loaded solution and configured additional roots, excluding bin/obj/version-control folders. Results are text candidates only.")]
    public Task<Page<TextMatch>> SearchText(string text, CancellationToken cancellationToken, int offset = 0, int? limit = null) =>
        Run(cancellationToken, "search_text", () => code.SearchTextAsync(text, offset, limit ?? options.MaxResults, cancellationToken));

    [McpServerTool(Name = "find_procedure_usage", ReadOnly = true, Destructive = false), Description("Maps procedure string/constant candidates to containing Roslyn symbols and distinguishes configured calls from unverified candidates. Comments are not treated as calls.")]
    public Task<Page<ProcedureUsage>> FindProcedureUsage(string procedure, CancellationToken cancellationToken, int offset = 0, int? limit = null) =>
        Run(cancellationToken, "find_procedure_usage", () => code.ProcedureUsagesAsync(procedure, offset, limit ?? options.MaxResults, cancellationToken));

    [McpServerTool(Name = "analyze_procedure", ReadOnly = true, Destructive = false), Description("Reads SQL Server procedure metadata and direct dependencies. READ/WRITE remains Unknown. Definition is opt-in; does not execute the procedure.")]
    public Task<ProcedureAnalysis> AnalyzeProcedure(string procedure, CancellationToken cancellationToken, bool includeDefinition = false) =>
        Run(cancellationToken, "analyze_procedure", () => sql.AnalyzeAsync(procedure, includeDefinition, cancellationToken));

    [McpServerTool(Name = "trace_code_to_database", ReadOnly = true, Destructive = false), Description("Traces resolved source invocations and recognized procedure usages forward to SQL metadata. Returns a bounded static graph with evidence.")]
    public Task<FlowResult> TraceCodeToDatabase(string symbol, CancellationToken cancellationToken, int? maxDepth = null, int? maxNodes = null) =>
        Run(cancellationToken, "trace_code_to_database", () => flow.TraceCodeAsync(symbol, maxDepth ?? options.MaxDepth, maxNodes ?? options.MaxNodes, cancellationToken));

    [McpServerTool(Name = "trace_procedure_to_ui", ReadOnly = true, Destructive = false), Description("Traces recognized procedure usage back to caller symbols and event subscriptions. Does not infer a remote WCF implementation.")]
    public Task<FlowResult> TraceProcedureToUi(string procedure, CancellationToken cancellationToken, int? maxDepth = null, int? maxNodes = null) =>
        Run(cancellationToken, "trace_procedure_to_ui", () => flow.TraceProcedureAsync(procedure, maxDepth ?? options.MaxDepth, maxNodes ?? options.MaxNodes, cancellationToken));

    private async Task<T> Run<T>(CancellationToken cancellationToken, string tool, Func<Task<T>> operation)
    {
        await executionGate.WaitAsync(cancellationToken);
        var timer = Stopwatch.StartNew();
        try { return await operation(); }
        catch (Exception error)
        {
            logger.LogWarning("{Tool} failed ({ErrorType}).", tool, error.GetType().Name);
            throw;
        }
        finally { logger.LogInformation("{Tool} completed in {ElapsedMs} ms.", tool, timer.ElapsedMilliseconds); executionGate.Release(); }
    }
}
