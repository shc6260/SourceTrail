using SourceTrail.Core.Models;

namespace SourceTrail.Core.Flow;

/// <summary>Lossless wire representation; exact symbol IDs and all call sites remain recoverable.</summary>
public sealed record CompactFlowResult(int SchemaVersion, string Status, string Direction, string? SnapshotId,
    IReadOnlyList<FlowNode> Nodes, IReadOnlyList<string> Relations, IReadOnlyList<string> Evidence,
    IReadOnlyList<string> Files, IReadOnlyList<CompactFlowEdge> Edges, bool Truncated, IReadOnlyList<string> Warnings);

public sealed record CompactFlowEdge(int From, int To, int Relation, int Evidence, int? File, int? Line);

public static class FlowCompactor
{
    public static CompactFlowResult Compact(FlowResult result)
    {
        var nodes = result.Nodes.ToArray();
        var ids = nodes.Select((n, i) => (n.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var relations = new Dictionary<string, int>(StringComparer.Ordinal);
        var evidence = new Dictionary<string, int>(StringComparer.Ordinal);
        var files = new Dictionary<string, int>(StringComparer.Ordinal);
        static int Intern(Dictionary<string, int> values, string value)
        {
            if (!values.TryGetValue(value, out var index)) values.Add(value, index = values.Count);
            return index;
        }
        var edges = result.Edges.Select(e => new CompactFlowEdge(ids[e.From], ids[e.To],
            Intern(relations, e.RelationType), Intern(evidence, e.Evidence),
            e.File is null ? null : Intern(files, e.File), e.Line)).ToArray();
        return new(1, result.Status, result.Direction, result.SnapshotId, nodes,
            relations.OrderBy(p => p.Value).Select(p => p.Key).ToArray(),
            evidence.OrderBy(p => p.Value).Select(p => p.Key).ToArray(),
            files.OrderBy(p => p.Value).Select(p => p.Key).ToArray(), edges, result.Truncated, result.Warnings.ToArray());
    }

    public static FlowResult Expand(CompactFlowResult result)
    {
        if (result.SchemaVersion != 1) throw new ArgumentException("Unsupported compact flow schema version.");
        static T At<T>(IReadOnlyList<T> values, int index) => index >= 0 && index < values.Count
            ? values[index] : throw new ArgumentException("Invalid compact flow reference.");
        var edges = result.Edges.Select(e => new FlowEdge(At(result.Nodes, e.From).Id, At(result.Nodes, e.To).Id,
            At(result.Relations, e.Relation), At(result.Evidence, e.Evidence),
            e.File is null ? null : At(result.Files, e.File.Value), e.Line)).ToArray();
        return new(result.Status, result.Direction, result.SnapshotId, result.Nodes.ToArray(), edges,
            result.Truncated, result.Warnings.ToArray());
    }
}
