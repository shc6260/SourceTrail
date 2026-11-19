using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SourceTrail.Core.Models;

namespace SourceTrail.Roslyn.Workspace;

public sealed partial class RoslynAnalyzer
{
    public async Task<DatabaseCallSummary> FindDatabaseCallsAsync(string symbol, int limit, CancellationToken token)
    {
        ValidatePage(0, limit); await _gate.WaitAsync(token);
        try
        {
            var declaration = Resolve(symbol);
            if (declaration.SymbolKind != "Method") throw new ArgumentException("Choose a method declaration from find_symbol.");
            var file = _sourceFiles.First(file => file.File == declaration.File);
            var root = await RootAsync(file, token);
            var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().First(node =>
                SourceId(file.File, file.Hash, node.SpanStart) == declaration.Id);
            var nodes = method.DescendantNodes(node => node is not LocalFunctionStatementSyntax).ToArray();
            var calls = new List<DatabaseCall>(); var unresolved = new List<DatabaseCallSite>();
            var methods = nodes.OfType<InvocationExpressionSyntax>().Select(call => new MethodCallCandidate(
                InvocationName(call) ?? call.Expression.ToString(), call.Expression.ToString(), file.File, Line(call))).ToArray();
            DatabaseCallSite Site(SyntaxNode node) => new(declaration.FullName, file.File, Line(node));
            foreach (var creation in nodes.OfType<ObjectCreationExpressionSyntax>().Where(node =>
                SimpleType(node.Type.ToString()) is "SqlCommand" or "DbCommand"))
            {
                var argument = creation.ArgumentList?.Arguments.FirstOrDefault()?.Expression;
                var names = LiteralNames(argument, method);
                if (names.Length == 0) unresolved.Add(Site(creation));
                foreach (var name in names)
                    calls.Add(new("ProcedureCandidate", [name], "CommandConfiguration", Site(creation),
                        "Command name configured in this method; CommandType, later mutations, collection forwarding and execution must be checked in source."));
            }
            foreach (var assignment in nodes.OfType<AssignmentExpressionSyntax>().Where(node => SimpleName(node.Left) == "CommandText"))
                foreach (var name in LiteralNames(assignment.Right, method))
                    calls.Add(new("ProcedureCandidate", [name], "CommandConfiguration", Site(assignment), "CommandText assignment; not proof of execution."));
            foreach (var call in nodes.OfType<InvocationExpressionSyntax>())
            {
                var name = InvocationName(call) ?? "";
                var rule = _options.ProcedureCallRules.FirstOrDefault(rule => rule.MethodName == name);
                if (name.StartsWith("Execute", StringComparison.Ordinal) || rule is not null)
                {
                    var arguments = rule is null ? call.ArgumentList.Arguments : call.ArgumentList.Arguments.Skip(rule.ArgumentIndex).Take(1);
                    foreach (var argument in arguments)
                        foreach (var procedure in LiteralNames(argument.Expression, method))
                            calls.Add(new("ProcedureCandidate", [procedure], name, Site(call),
                                "Literal/local name passed to an execution-named method; receiver identity and overload are not bound."));
                }
                if (name != "Update" || call.Expression is not MemberAccessExpressionSyntax receiver) continue;
                var receiverName = SimpleName(receiver.Expression);
                var variable = nodes.OfType<VariableDeclaratorSyntax>().FirstOrDefault(node => node.Identifier.ValueText == receiverName);
                var type = receiver.Expression is ObjectCreationExpressionSyntax direct ? direct.Type.ToString() :
                    variable?.Initializer?.Value is ObjectCreationExpressionSyntax newAdapter ? newAdapter.Type.ToString() :
                    (variable?.Parent as VariableDeclarationSyntax)?.Type.ToString();
                if (type is null || !type.EndsWith("Adapter", StringComparison.Ordinal)) continue;
                var stem = Regex.Replace(SimpleType(type), "(Table)?Adapter$", "");
                bool foundAdapter = false;
                foreach (var adapter in _sourceFiles.Where(source => source.Symbols.Any(s => s.Name == SimpleType(type) && s.SymbolKind == "Class") ||
                    stem.Length >= 3 && Path.GetFileName(source.File).Contains(stem, StringComparison.OrdinalIgnoreCase)))
                {
                    var text = await File.ReadAllTextAsync(adapter.File, token);
                    if (!text.Contains("CommandText", StringComparison.Ordinal) || !text.Contains(SimpleType(type), StringComparison.Ordinal)) continue;
                    var adapterRoot = await RootAsync(adapter, token);
                    foreach (var assignment in adapterRoot.DescendantNodes().OfType<AssignmentExpressionSyntax>().Where(node =>
                        SimpleName(node.Left) == "CommandText" && node.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText == SimpleType(type)))
                    {
                        string left = assignment.Left.ToString();
                        var operation = left.Contains("DeleteCommand") ? "Deleted" : left.Contains("InsertCommand") ? "Added" : left.Contains("UpdateCommand") ? "Modified" : null;
                        if (operation is null) continue;
                        foundAdapter = true;
                        foreach (var procedure in LiteralNames(assignment.Right, null))
                            calls.Add(new("AdapterCommandCandidate", [procedure], "Update", new(SimpleType(type), adapter.File, Line(assignment)),
                                $"Named adapter {type} configures {operation} rows. Verify adapter identity, inheritance and the row-state subset supplied at {file.File}:{Line(call)}."));
                    }
                }
                if (!foundAdapter) unresolved.Add(Site(call));
            }
            var distinct = calls.DistinctBy(call => (call.Kind, Name: string.Join("|", call.Targets), call.Site.File, call.Site.Line)).ToArray();
            await PersistParsedAsync(token);
            return new("Candidates", _state.SnapshotId, Site(method), distinct.Take(limit).ToArray(), unresolved.Distinct().ToArray(),
                methods.Take(limit).ToArray(), distinct.Length > limit || methods.Length > limit);
        }
        finally { _gate.Release(); }
    }
    private static string SimpleType(string name) => name.Split('.').Last();
    private static string[] LiteralNames(ExpressionSyntax? expression, MethodDeclarationSyntax? method)
    {
        IEnumerable<string> values = expression switch
        {
            LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression) => [literal.Token.ValueText],
            IdentifierNameSyntax name when method is not null => method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                .Where(node => node.Identifier.ValueText == name.Identifier.ValueText)
                .Select(node => node.Initializer?.Value).OfType<LiteralExpressionSyntax>()
                .Where(node => node.IsKind(SyntaxKind.StringLiteralExpression)).Select(node => node.Token.ValueText),
            _ => []
        };
        return values.Where(value => Regex.IsMatch(value, @"^(?:\[?[\w]+\]?\.){0,2}\[?[\w]+\]?$" )).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
