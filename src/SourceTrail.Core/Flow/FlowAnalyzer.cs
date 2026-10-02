using SourceTrail.Core.Contracts;
using SourceTrail.Core.Models;

namespace SourceTrail.Core.Flow;

public sealed class FlowAnalyzer(ICodeAnalyzer code, IProcedureAnalyzer sql)
{
    public async Task<FlowResult> TraceCodeAsync(string symbol, int maxDepth, int maxNodes, CancellationToken cancellationToken)
    {
        ValidateLimits(maxDepth, maxNodes);
        var initial = await code.ExpandAsync(symbol, false, cancellationToken);
        var nodes = new Dictionary<string, FlowNode>();
        var edges = new HashSet<FlowEdge>();
        var warnings = new HashSet<string>();
        var visited = new HashSet<string>();
        var pending = new Queue<(string Id, int Depth)>();
        bool truncated = false;
        bool partial = code.Status().Status != "Ready";
        AddSymbol(initial.Symbol);
        pending.Enqueue((initial.Symbol.Id, 0));
        while (pending.TryDequeue(out var next))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(next.Id)) continue;
            var expansion = next.Id == initial.Symbol.Id ? initial : await code.ExpandAsync(next.Id, false, cancellationToken);
            foreach (var warning in expansion.Warnings) { warnings.Add(warning); partial = true; }
            if (next.Depth >= maxDepth)
            {
                if (expansion.Relations.Count > 0 || expansion.ProcedureUsages.Count > 0) truncated = true;
                continue;
            }
            foreach (var relation in expansion.Relations)
            {
                // DispatchCandidate endpoints are one extra logical step from the interface node.
                int depth = next.Depth + (relation.From.Id == expansion.Symbol.Id ? 1 : 2);
                if (depth > maxDepth) { truncated = true; continue; }
                if (!AddSymbol(relation.From) || !AddSymbol(relation.To)) { truncated = true; continue; }
                edges.Add(new(relation.From.Id, relation.To.Id, relation.RelationType, relation.Evidence, relation.File, relation.Line));
                if (relation.RelationType == "DispatchCandidate") partial = true;
                pending.Enqueue((relation.To.Id, depth));
            }
            foreach (var usage in expansion.ProcedureUsages)
            {
                string id = ProcedureId(usage.Procedure);
                if (!AddNode(new(id, usage.Procedure, "Procedure"))) { truncated = true; continue; }
                edges.Add(new(expansion.Symbol.Id, id, usage.RelationType, usage.Evidence, usage.File, usage.Line));
                if (usage.RelationType == "ProcedureCommandConfiguration") partial = true;
                if (next.Depth + 1 >= maxDepth) { truncated = true; continue; }
                var analyzed = await sql.AnalyzeAsync(usage.Procedure, false, cancellationToken);
                if (analyzed.Status != "Ready") partial = true;
                foreach (var warning in analyzed.Warnings) warnings.Add(warning);
                foreach (var dependency in analyzed.Dependencies)
                {
                    string dependencyId = ObjectId(dependency);
                    if (!AddNode(new(dependencyId, dependency.Name, dependency.Kind,
                        Schema: dependency.Schema, Database: dependency.Database)))
                    { truncated = true; continue; }
                    edges.Add(new(id, dependencyId, "SqlDependency", $"Access: {dependency.Access}; resolved: {dependency.Resolved}."));
                }
            }
        }
        return Result("CodeToDatabase");

        bool AddSymbol(SymbolInfo value) => AddNode(ToNode(value));
        bool AddNode(FlowNode node)
        {
            if (nodes.ContainsKey(node.Id)) return true;
            if (nodes.Count >= maxNodes) return false;
            nodes.Add(node.Id, node); return true;
        }
        FlowResult Result(string direction)
        {
            AddWarnings();
            return new(partial || truncated ? "Partial" : "Ready", direction, code.Status().SnapshotId,
                nodes.Values.ToArray(), edges.ToArray(), truncated, warnings.ToArray());
        }
        void AddWarnings()
        {
            warnings.Add("Static source relationships do not prove runtime execution. Unregistered DB wrappers and dynamic calls may leave gaps.");
            if (code.Status().Status != "Ready") warnings.Add("Solution is partially loaded; missing calls are not proof of absence.");
            if (truncated) warnings.Add("Traversal stopped at maxDepth or maxNodes.");
        }
    }

    public async Task<FlowResult> TraceProcedureAsync(string procedure, int maxDepth, int maxNodes, CancellationToken cancellationToken)
    {
        ValidateLimits(maxDepth, maxNodes);
        var usages = await code.ProcedureUsagesAsync(procedure, 0, 1000, cancellationToken);
        var nodes = new Dictionary<string, FlowNode>();
        var edges = new HashSet<FlowEdge>();
        var warnings = new HashSet<string>(usages.Warnings);
        bool truncated = usages.Truncated;
        bool partial = code.Status().Status != "Ready";
        string procId = ProcedureId(procedure);
        AddNode(new(procId, procedure, "Procedure"));
        var pending = new Queue<(string Id, int Depth)>();
        foreach (var usage in usages.Items.Where(u => u.IsVerifiedCall && u.ContainingSymbol is not null))
        {
            if (maxDepth < 1 || !AddNode(ToNode(usage.ContainingSymbol!))) { truncated = true; continue; }
            edges.Add(new(usage.ContainingSymbol!.Id, procId, usage.RelationType, usage.Evidence, usage.File, usage.Line));
            pending.Enqueue((usage.ContainingSymbol.Id, 1));
        }
        if (usages.Items.Any(u => !u.IsVerifiedCall))
            warnings.Add("Text/constant candidates are available in find_procedure_usage; they are excluded from the verified flow.");
        if (pending.Count == 0)
        {
            partial = true;
            warnings.Add("No recognized procedure call/configuration found. Register the project-specific DB wrapper or inspect candidates.");
        }
        var visited = new HashSet<string>();
        while (pending.TryDequeue(out var next))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(next.Id)) continue;
            var expansion = await code.ExpandAsync(next.Id, true, cancellationToken);
            foreach (var warning in expansion.Warnings) { warnings.Add(warning); partial = true; }
            if (next.Depth >= maxDepth)
            {
                if (expansion.Relations.Count > 0) truncated = true;
                continue;
            }
            foreach (var relation in expansion.Relations)
            {
                if (!AddNode(ToNode(relation.From)) || !AddNode(ToNode(relation.To))) { truncated = true; continue; }
                edges.Add(new(relation.From.Id, relation.To.Id, relation.RelationType, relation.Evidence, relation.File, relation.Line));
                if (relation.RelationType == "DispatchCandidate") partial = true;
                pending.Enqueue((relation.From.Id, next.Depth + 1));
            }
        }
        warnings.Add("Event subscriptions and static calls do not prove event firing or runtime execution. Remote WCF implementation mapping requires verification.");
        if (truncated) warnings.Add("Traversal stopped at maxDepth, maxNodes or the procedure usage result limit.");
        return new(partial || truncated ? "Partial" : "Ready", "ProcedureToUi", code.Status().SnapshotId,
            nodes.Values.ToArray(), edges.ToArray(), truncated, warnings.ToArray());

        bool AddNode(FlowNode node)
        {
            if (nodes.ContainsKey(node.Id)) return true;
            if (nodes.Count >= maxNodes) return false;
            nodes.Add(node.Id, node); return true;
        }
    }

    private static FlowNode ToNode(SymbolInfo symbol) => new(symbol.Id, symbol.FullName,
        symbol.IsUi ? "Ui" + symbol.SymbolKind : symbol.SymbolKind, symbol.Project, symbol.File, symbol.Line);
    private static string ProcedureId(string name) => "procedure:" + name.Replace("[", "").Replace("]", "").ToLowerInvariant();
    private static string ObjectId(SqlObject obj) => string.Join(":", "sql", obj.Server, obj.Database, obj.Schema, obj.Name).ToLowerInvariant();
    private static void ValidateLimits(int depth, int nodes)
    { if (depth is < 0 or > 20 || nodes is < 1 or > 2000) throw new ArgumentException("maxDepth must be 0..20 and maxNodes 1..2000."); }
}
