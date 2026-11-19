using System.ComponentModel;
using ModelContextProtocol.Server;
using SourceTrail.Core.Models;
using SourceTrail.Mcp.Indexing;

namespace SourceTrail.Mcp.Tools;

[McpServerToolType]
public sealed class IndexPreparationTools(IndexPreparationService preparation)
{
    [McpServerTool(Name = "prepare_indexes", ReadOnly = false, Destructive = false),
     Description("Explicitly prepares all C# declarations, UI and local SQL indexes for the configured paths. Default reuses unchanged cached files; rebuild=true ignores old caches. Builds a separate generation and publishes only after completion; old indexes and source files are retained. Returns counts, warnings and stage timings. Reconnect MCP after publishing to use the new generation.")]
    public Task<IndexPreparationResult> PrepareIndexes(CancellationToken cancellationToken, bool rebuild = false) =>
        preparation.PrepareAsync(rebuild, cancellationToken: cancellationToken);
}
