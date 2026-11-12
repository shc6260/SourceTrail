using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SourceTrail.Core.Models;

namespace SourceTrail.Roslyn.Workspace;

public sealed partial class RoslynAnalyzer
{
    private static readonly HashSet<string> SqlExecutionMethods = new(StringComparer.Ordinal)
    {
        "ExecuteReader", "ExecuteReaderAsync", "ExecuteNonQuery", "ExecuteNonQueryAsync",
        "ExecuteScalar", "ExecuteScalarAsync"
    };

    public async Task<DatabaseCallSummary> FindDatabaseCallsAsync(string symbol, int maxDepth, int maxMethods,
        CancellationToken cancellationToken)
    {
        RequireText(symbol, nameof(symbol));
        if (maxDepth is < 0 or > 8 || maxMethods is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(maxMethods), "Use maxDepth 0–8 and maxMethods 1–100.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var cacheKey = $"database-calls:{maxDepth}:{maxMethods}:{symbol}";
            if (await ReadDiskItemAsync<DatabaseCallSummary>(cacheKey, cancellationToken) is { } cached)
                return cached;

            await EnsureSymbolsAsync(cancellationToken);
            var root = ResolveDatabaseMethod(symbol);
            var summary = await WalkDatabaseCallsAsync(root.Method, root.Project, maxDepth, maxMethods,
                cancellationToken);
            await SaveDiskItemAsync(cacheKey, summary, cancellationToken);
            return summary;
        }
        finally { _gate.Release(); }
    }

    private (IMethodSymbol Method, ProjectId Project) ResolveDatabaseMethod(string query)
    {
        if (_symbols.TryGetValue(query, out var exact) && exact.Symbol is IMethodSymbol exactMethod)
            return (exactMethod, exact.Project);
        var matches = _symbols.Values.Where(entry => entry.Symbol is IMethodSymbol method &&
            (method.ToDisplayString() == query || method.GetDocumentationCommentId() == query ||
                method.ContainingType.ToDisplayString() + "." + method.Name == query)).Take(2).ToArray();
        return matches.Length switch
        {
            1 => ((IMethodSymbol)matches[0].Symbol, matches[0].Project),
            0 => throw new ArgumentException("Method not found. Provide its qualified name or a symbol ID."),
            _ => throw new ArgumentException("Ambiguous method. Use find_symbol and pass an exact symbol ID.")
        };
    }

    private async Task<DatabaseCallSummary> WalkDatabaseCallsAsync(IMethodSymbol entry, ProjectId entryProject,
        int maxDepth, int maxMethods, CancellationToken token)
    {
        var pending = new Queue<(IMethodSymbol Method, ProjectId Project, DatabaseCallSite[] Path)>();
        pending.Enqueue((entry, entryProject, []));
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var calls = new List<DatabaseCall>();
        var unresolved = new List<DatabaseCallSite>();
        bool truncated = false;
        while (pending.TryDequeue(out var current))
        {
            token.ThrowIfCancellationRequested();
            var methodId = Id(current.Method, current.Project);
            if (visited.Contains(methodId)) continue;
            if (visited.Count >= maxMethods) { truncated = true; break; }
            visited.Add(methodId);
            var method = current.Method.PartialImplementationPart ?? current.Method;
            var described = Describe(current.Method, current.Project);
            var mappings = await TableAdapterUsagesAsync(method, described, token);
            foreach (var mapping in mappings)
            {
                var site = new DatabaseCallSite(described.FullName, mapping.File, mapping.Line);
                calls.Add(new("TableAdapterProcedure", [mapping.Procedure], "Update", site, current.Path,
                    mapping.Evidence));
            }
            if (mappings.Count != 0) continue;
            var configured = await VerifiedUsagesAsync(current.Method, described, token);
            foreach (var usage in configured.Where(usage => usage.RelationType == "ConfiguredProcedureCall"))
            {
                var site = new DatabaseCallSite(described.FullName, usage.File, usage.Line);
                calls.Add(new("ConfiguredProcedure", [usage.Procedure], "Wrapper", site, current.Path));
            }

            foreach (var reference in method.DeclaringSyntaxReferences)
            {
                var document = RequireSolution().GetDocument(reference.SyntaxTree);
                if (document is null) continue;
                var model = await document.GetSemanticModelAsync(token);
                if (model is null) continue;
                var body = await reference.GetSyntaxAsync(token);
                foreach (var syntax in body.DescendantNodes(node => node is not LocalFunctionStatementSyntax)
                    .OfType<InvocationExpressionSyntax>())
                {
                    token.ThrowIfCancellationRequested();
                    if (model.GetOperation(syntax, token) is not IInvocationOperation invocation) continue;
                    var site = CallSite(described.FullName, syntax.GetLocation());
                    var target = invocation.TargetMethod;
                    if (SqlExecutionMethods.Contains(target.Name) && IsDbCommand(invocation.Instance?.Type))
                    {
                        if (ReadSqlExecution(invocation, syntax, model, site, current.Path, token) is { } call)
                            calls.Add(call);
                        else
                            unresolved.Add(site);
                        continue;
                    }

                    // Only follow bound source declarations. Delegate and runtime dispatch remain unresolved.
                    if (target.IsAbstract || target.ContainingType.TypeKind == TypeKind.Interface) continue;
                    if (FindSourceEntry((target.ReducedFrom ?? target).OriginalDefinition) is not
                        { Symbol: IMethodSymbol next, Project: var nextProject } ||
                        visited.Contains(Id(next, nextProject))) continue;
                    if (current.Path.Length >= maxDepth) { truncated = true; continue; }
                    pending.Enqueue((next, nextProject, [.. current.Path, site]));
                }
            }
        }

        var root = CallSite(entry.ToDisplayString(), entry.Locations.First(location => location.IsInSource));
        var status = _state.Status == "Ready" && !truncated && unresolved.Count == 0 ? "Ready" : "Partial";
        return new(status, _state.SnapshotId, root, calls, unresolved.Distinct().ToArray(),
            visited.Count, truncated, _state.Diagnostics.Count);
    }

    private static DatabaseCallSite CallSite(string method, Location location)
    {
        var span = location.GetLineSpan();
        return new(method, span.Path, span.StartLinePosition.Line + 1);
    }

    private static DatabaseCall? ReadSqlExecution(IInvocationOperation invocation, InvocationExpressionSyntax syntax,
        SemanticModel model, DatabaseCallSite site, IReadOnlyList<DatabaseCallSite> path, CancellationToken token)
    {
        if (invocation.Instance is not ILocalReferenceOperation receiver ||
            receiver.Local.DeclaringSyntaxReferences.SingleOrDefault()?.GetSyntax(token) is not
                VariableDeclaratorSyntax { Initializer.Value: { } initializer } declaration ||
            declaration.SyntaxTree != syntax.SyntaxTree ||
            model.GetOperation(initializer, token) is not IObjectCreationOperation creation ||
            !IsDbCommand(creation.Type))
            return null;

        object? text = creation.Arguments.FirstOrDefault(argument => argument.Parameter?.Ordinal == 0 &&
            argument.Parameter.Type.SpecialType == SpecialType.System_String)?.Value.ConstantValue.Value;
        object? kind = (int)System.Data.CommandType.Text;
        foreach (var member in creation.Initializer?.Initializers.OfType<ISimpleAssignmentOperation>() ?? [])
            if (member.Target is IPropertyReferenceOperation property)
                SetCommandMember(property.Property.Name, member.Value, ref text, ref kind);

        // A single local, without escapes or conditional command mutations, has a stable command at this site.
        var scope = (SyntaxNode?)declaration.FirstAncestorOrSelf<UsingStatementSyntax>() ??
            declaration.FirstAncestorOrSelf<BlockSyntax>();
        if (scope is null) return null;
        var executeBlock = syntax.FirstAncestorOrSelf<BlockSyntax>();
        foreach (var reference in scope.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Where(node => node.SpanStart > declaration.Span.End && node.SpanStart < syntax.SpanStart &&
                node.Identifier.ValueText == receiver.Local.Name))
        {
            if (!SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(reference, token).Symbol, receiver.Local))
                continue;
            if (reference.Parent is not MemberAccessExpressionSyntax member || member.Expression != reference)
                return null;
            if (member.Parent is not AssignmentExpressionSyntax assignment || assignment.Left != member) continue;
            if (member.Name.Identifier.ValueText is not ("CommandText" or "CommandType")) continue;
            if (assignment.Parent is not ExpressionStatementSyntax statement || statement.Parent != executeBlock ||
                model.GetOperation(assignment, token) is not ISimpleAssignmentOperation mutation)
                return null;
            SetCommandMember(member.Name.Identifier.ValueText, mutation.Value, ref text, ref kind);
        }

        if (text is not string commandText || string.IsNullOrWhiteSpace(commandText) || kind is not int commandType)
            return null;
        if (commandType == (int)System.Data.CommandType.StoredProcedure)
            return new("DirectProcedure", [commandText], invocation.TargetMethod.Name, site, path);
        if (commandType != (int)System.Data.CommandType.Text) return null;

        var script = new TSql170Parser(true).Parse(new StringReader(commandText), out var errors);
        if (errors.Count != 0) return null;
        var targets = new SqlCommandTargets();
        script.Accept(targets);
        return new("SqlText", targets.Names.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            invocation.TargetMethod.Name, site, path);
    }

    private static void SetCommandMember(string name, IOperation value, ref object? text, ref object? kind)
    {
        if (name == "CommandText") text = value.ConstantValue.Value;
        if (name == "CommandType") kind = value.ConstantValue.Value;
    }

    private sealed class SqlCommandTargets : TSqlFragmentVisitor
    {
        internal readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase);

        public override void ExplicitVisit(NamedTableReference node)
        {
            Names.Add(string.Join(".", node.SchemaObject.Identifiers.Select(part => part.Value)));
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(ExecutableProcedureReference node)
        {
            var name = node.ProcedureReference?.ProcedureReference?.Name;
            if (name is not null) Names.Add(string.Join(".", name.Identifiers.Select(part => part.Value)));
            base.ExplicitVisit(node);
        }
    }
}
