using SourceTrail.Core.Contracts;
using SourceTrail.Core.Models;

namespace SourceTrail.Core.Flow;

/// <summary>C# 호출과 SQL 참조를 연결하고 깊이·노드 제한 안에서 흐름을 탐색한다.</summary>
public sealed class FlowAnalyzer(ICodeAnalyzer code, IProcedureAnalyzer sql)
{
    public async Task<FlowResult> TraceCodeAsync(string symbol, int maxDepth, int maxNodes, CancellationToken cancellationToken)
    {
        ValidateLimits(maxDepth, maxNodes);
        var initial = await code.ExpandAsync(symbol, false, cancellationToken);
        var graph = new TraversalState(maxDepth, maxNodes, code.Status().Status != "Ready");
        var pending = new Queue<(string Id, int Depth)>();
        var visited = new HashSet<string>();
        graph.AddSymbol(initial.Symbol);
        pending.Enqueue((initial.Symbol.Id, 0));

        while (pending.TryDequeue(out var next))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(next.Id)) continue;
            var expansion = next.Id == initial.Symbol.Id
                ? initial : await code.ExpandAsync(next.Id, false, cancellationToken);
            graph.AddAnalysisWarnings(expansion.Warnings);
            if (next.Depth >= maxDepth)
            {
                if (expansion.Relations.Count > 0 || expansion.ProcedureUsages.Count > 0) graph.Truncated = true;
                continue;
            }
            AddForwardRelations(expansion, next.Depth, graph, pending);
            await AddProcedureUsagesAsync(expansion, next.Depth, graph, cancellationToken);
        }

        AddForwardWarnings(graph);
        return graph.BuildResult("CodeToDatabase", code.Status().SnapshotId);
    }

    // 구현 후보는 인터페이스 노드를 경유하므로 일반 호출보다 한 단계 더 깊다.
    private static void AddForwardRelations(CodeExpansion expansion, int currentDepth,
        TraversalState graph, Queue<(string Id, int Depth)> pending)
    {
        foreach (var relation in expansion.Relations)
        {
            int depth = currentDepth + (relation.From.Id == expansion.Symbol.Id ? 1 : 2);
            if (depth > graph.MaxDepth) { graph.Truncated = true; continue; }
            if (!graph.AddSymbol(relation.From) || !graph.AddSymbol(relation.To)) continue;
            graph.AddRelation(relation);
            pending.Enqueue((relation.To.Id, depth));
        }
    }

    private async Task AddProcedureUsagesAsync(CodeExpansion expansion, int currentDepth,
        TraversalState graph, CancellationToken cancellationToken)
    {
        foreach (var usage in expansion.ProcedureUsages)
        {
            string id = ProcedureId(usage.Procedure);
            if (!graph.AddNode(new(id, usage.Procedure, "Procedure"))) continue;
            graph.Edges.Add(new(expansion.Symbol.Id, id, usage.RelationType, usage.Evidence, usage.File, usage.Line));
            if (usage.RelationType == "ProcedureCommandConfiguration") graph.Partial = true;
            if (currentDepth + 1 >= graph.MaxDepth) { graph.Truncated = true; continue; }
            await TraceSqlDependenciesAsync(usage.Procedure, id, currentDepth + 1, graph, cancellationToken);
        }
    }

    // SQL 호출도 방문 집합과 깊이 제한을 적용해 재귀 프로시저에서 무한 순환하지 않는다.
    private async Task TraceSqlDependenciesAsync(string procedure, string id, int depth,
        TraversalState graph, CancellationToken cancellationToken)
    {
        var pending = new Queue<(string Name, string Id, int Depth)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Enqueue((procedure, id, depth));
        while (pending.TryDequeue(out var next))
        {
            if (!visited.Add(next.Id)) continue;
            var analyzed = await sql.AnalyzeAsync(next.Name, false, cancellationToken);
            if (analyzed.Status != "Ready") graph.Partial = true;
            foreach (var warning in analyzed.Warnings) graph.Warnings.Add(warning);
            if (next.Depth >= graph.MaxDepth)
            {
                if (analyzed.Dependencies.Count > 0) graph.Truncated = true;
                continue;
            }
            foreach (var dependency in analyzed.Dependencies)
                AddSqlDependency(analyzed, dependency, next.Id, next.Depth, graph, pending);
        }
    }

    private static void AddSqlDependency(ProcedureAnalysis analyzed, SqlObject dependency,
        string parentId, int depth, TraversalState graph, Queue<(string Name, string Id, int Depth)> pending)
    {
        string id = ObjectId(dependency);
        if (!graph.AddNode(new(id, dependency.Name, dependency.Kind, File: dependency.File,
            Line: dependency.Line, Schema: dependency.Schema, Database: dependency.Database))) return;
        graph.Edges.Add(new(parentId, id, "SqlDependency",
            $"Source: {analyzed.Source}; access: {dependency.Access}; resolved: {dependency.Resolved}.", dependency.File, dependency.Line));
        if (!dependency.Resolved || dependency.CallerDependent || dependency.Ambiguous)
        { graph.Partial = true; return; }
        if (dependency.Kind is not "Procedure" and not "SQL_STORED_PROCEDURE") return;
        if (analyzed.Source == "LiveDatabase" && dependency.Database != analyzed.Database)
        {
            graph.Partial = true;
            graph.Warnings.Add("Cross-database procedure traversal requires separate connection configuration.");
            return;
        }
        var name = analyzed.Source == "SqlFiles" && dependency.Database is not null
            ? $"{dependency.Database}.{dependency.Schema}.{dependency.Name}" : $"{dependency.Schema}.{dependency.Name}";
        pending.Enqueue((name, id, depth + 1));
    }

    private void AddForwardWarnings(TraversalState graph)
    {
        graph.Warnings.Add("Static source relationships do not prove runtime execution. Unregistered DB wrappers and dynamic calls may leave gaps.");
        if (code.Status().Status != "Ready") graph.Warnings.Add("Solution is partially loaded; missing calls are not proof of absence.");
        if (graph.Truncated) graph.Warnings.Add("Traversal stopped at maxDepth or maxNodes.");
    }

    public async Task<FlowResult> TraceProcedureAsync(string procedure, int maxDepth, int maxNodes, CancellationToken cancellationToken)
    {
        ValidateLimits(maxDepth, maxNodes);
        var usages = await code.ProcedureUsagesAsync(procedure, 0, 1000, cancellationToken);
        var graph = new TraversalState(maxDepth, maxNodes, code.Status().Status != "Ready") { Truncated = usages.Truncated };
        foreach (var warning in usages.Warnings) graph.Warnings.Add(warning);
        string procId = ProcedureId(procedure);
        graph.AddNode(new(procId, procedure, "Procedure"));
        var pending = CreateCallerQueue(usages, procId, graph);
        await TraceCallersAsync(pending, graph, cancellationToken);
        graph.Warnings.Add("Event subscriptions and static calls do not prove event firing or runtime execution. Remote WCF implementation mapping requires verification.");
        if (graph.Truncated) graph.Warnings.Add("Traversal stopped at maxDepth, maxNodes or the procedure usage result limit.");
        return graph.BuildResult("ProcedureToUi", code.Status().SnapshotId);
    }

    // 문자열 후보를 실제 호출자와 섞지 않는다. 역추적은 검증된 호출부터 시작한다.
    private static Queue<(string Id, int Depth)> CreateCallerQueue(Page<ProcedureUsage> usages,
        string procId, TraversalState graph)
    {
        var pending = new Queue<(string Id, int Depth)>();
        foreach (var usage in usages.Items.Where(u => u.IsVerifiedCall && u.ContainingSymbol is not null))
        {
            if (graph.MaxDepth < 1) { graph.Truncated = true; continue; }
            if (!graph.AddSymbol(usage.ContainingSymbol!)) continue;
            graph.Edges.Add(new(usage.ContainingSymbol!.Id, procId, usage.RelationType, usage.Evidence, usage.File, usage.Line));
            pending.Enqueue((usage.ContainingSymbol.Id, 1));
        }
        if (usages.Items.Any(u => !u.IsVerifiedCall))
            graph.Warnings.Add("Text/constant candidates are available in find_procedure_usage; they are excluded from the verified flow.");
        if (pending.Count == 0)
        {
            graph.Partial = true;
            graph.Warnings.Add("No recognized procedure call/configuration found. Register the project-specific DB wrapper or inspect candidates.");
        }
        return pending;
    }

    private async Task TraceCallersAsync(Queue<(string Id, int Depth)> pending,
        TraversalState graph, CancellationToken cancellationToken)
    {
        var visited = new HashSet<string>();
        while (pending.TryDequeue(out var next))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(next.Id)) continue;
            var expansion = await code.ExpandAsync(next.Id, true, cancellationToken);
            graph.AddAnalysisWarnings(expansion.Warnings);
            if (next.Depth >= graph.MaxDepth)
            {
                if (expansion.Relations.Count > 0) graph.Truncated = true;
                continue;
            }
            foreach (var relation in expansion.Relations)
            {
                if (!graph.AddSymbol(relation.From) || !graph.AddSymbol(relation.To)) continue;
                graph.AddRelation(relation);
                pending.Enqueue((relation.From.Id, next.Depth + 1));
            }
        }
    }

    // 한 번의 탐색에만 속하는 상태다. MCP 요청 사이에 그래프를 공유하지 않는다.
    private sealed class TraversalState(int maxDepth, int maxNodes, bool partial)
    {
        public int MaxDepth { get; } = maxDepth;
        public Dictionary<string, FlowNode> Nodes { get; } = new();
        public HashSet<FlowEdge> Edges { get; } = [];
        public HashSet<string> Warnings { get; } = [];
        public bool Partial { get; set; } = partial;
        public bool Truncated { get; set; }

        public bool AddSymbol(SymbolInfo symbol) => AddNode(ToNode(symbol));
        public bool AddNode(FlowNode node)
        {
            if (Nodes.ContainsKey(node.Id)) return true;
            if (Nodes.Count >= maxNodes) { Truncated = true; return false; }
            Nodes.Add(node.Id, node);
            return true;
        }
        public void AddRelation(CodeRelation relation)
        {
            Edges.Add(new(relation.From.Id, relation.To.Id, relation.RelationType, relation.Evidence, relation.File, relation.Line));
            if (relation.RelationType == "DispatchCandidate") Partial = true;
        }
        public void AddAnalysisWarnings(IEnumerable<string> warnings)
        {
            foreach (var warning in warnings) { Warnings.Add(warning); Partial = true; }
        }
        public FlowResult BuildResult(string direction, string? snapshotId) =>
            new(Partial || Truncated ? "Partial" : "Ready", direction, snapshotId,
                Nodes.Values.ToArray(), Edges.ToArray(), Truncated, Warnings.ToArray());
    }

    private static FlowNode ToNode(SymbolInfo symbol) => new(symbol.Id, symbol.FullName,
        symbol.IsUi ? "Ui" + symbol.SymbolKind : symbol.SymbolKind, symbol.Project, symbol.File, symbol.Line);
    private static string ProcedureId(string name) => "procedure:" + name.Replace("[", "").Replace("]", "").ToLowerInvariant();
    private static string ObjectId(SqlObject obj) => string.Join(":", "sql", obj.Server, obj.Database, obj.Schema, obj.Name).ToLowerInvariant();
    private static void ValidateLimits(int depth, int nodes)
    {
        if (depth is < 0 or > 20 || nodes is < 1 or > 2000)
            throw new ArgumentException("maxDepth must be 0..20 and maxNodes 1..2000.");
    }
}
