using SourceTrail.Core.Models;

namespace SourceTrail.Core.Contracts;

public interface ICodeAnalyzer : IDisposable
{
    Task<SolutionState> LoadAsync(string solutionPath, CancellationToken cancellationToken);
    SolutionState Status();
    Task<SolutionState> ReloadAsync(CancellationToken cancellationToken);
    Task<Page<SymbolInfo>> FindSymbolsAsync(string query, int offset, int limit, CancellationToken cancellationToken);
    Task<SymbolOverview> OverviewAsync(string symbolOrFile, CancellationToken cancellationToken);
    Task<Page<ReferenceInfo>> ReferencesAsync(string symbol, int offset, int limit, CancellationToken cancellationToken);
    Task<Page<TextMatch>> SearchTextAsync(string text, int offset, int limit, CancellationToken cancellationToken,
        string? directory = null, string? extension = null);
    Task<Page<EventHandlerMatch>> FindEventHandlersAsync(string file, string? query, int offset, int limit,
        CancellationToken cancellationToken);
    Task<Page<ProcedureUsage>> ProcedureUsagesAsync(string procedure, int offset, int limit, CancellationToken cancellationToken);
    Task<CodeExpansion> ExpandAsync(string symbol, bool callers, CancellationToken cancellationToken);
}

public interface IProcedureAnalyzer
{
    Task<ProcedureAnalysis> AnalyzeAsync(string procedure, bool includeDefinition, CancellationToken cancellationToken);
}

public interface IDatabaseCallAnalyzer
{
    Task<DatabaseCallSummary> FindDatabaseCallsAsync(string symbol, int maxDepth, int maxMethods,
        CancellationToken cancellationToken);
}
