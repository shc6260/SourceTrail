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
                var adapterType = SimpleType(type);
                var stem = Regex.Replace(adapterType, "(Table)?Adapter$", "");
                bool foundAdapter = false;
                var searched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                async Task ReadAdapterAsync(SourceFile adapter)
                {
                    if (!searched.Add(adapter.File)) return;
                    var text = await File.ReadAllTextAsync(adapter.File, token);
                    if (!text.Contains("CommandText", StringComparison.Ordinal) || !text.Contains(adapterType, StringComparison.Ordinal)) return;
                    var adapterRoot = await RootAsync(adapter, token);
                    foreach (var assignment in adapterRoot.DescendantNodes().OfType<AssignmentExpressionSyntax>().Where(node =>
                        SimpleName(node.Left) == "CommandText" && node.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText == adapterType))
                    {
                        string left = assignment.Left.ToString();
                        var operation = left.Contains("DeleteCommand") ? "Deleted" : left.Contains("InsertCommand") ? "Added" : left.Contains("UpdateCommand") ? "Modified" : null;
                        if (operation is null) continue;
                        foundAdapter = true;
                        var procedures = LiteralNames(assignment.Right, null);
                        foreach (var procedure in procedures)
                            calls.Add(new("AdapterCommandCandidate", [procedure], "Update", new(adapterType, adapter.File, Line(assignment)),
                                $"Named adapter {type} configures {operation} rows. Verify adapter identity, inheritance and the row-state subset supplied at {file.File}:{Line(call)}."));
                        // Designer-generated adapters often carry inline SQL instead of a procedure name.
                        if (procedures.Length == 0 && SqlStatementTarget(assignment.Right) is { } statement)
                            calls.Add(new("AdapterSqlCandidate", [statement], "Update", new(adapterType, adapter.File, Line(assignment)),
                                $"Named adapter {type} configures inline SQL for {operation} rows (no stored procedure). Verify adapter identity and the row-state subset supplied at {file.File}:{Line(call)}."));
                    }
                }
                foreach (var adapter in _sourceFiles.Where(source => source.Symbols.Any(s => s.Name == adapterType && s.SymbolKind == "Class") ||
                    stem.Length >= 3 && Path.GetFileName(source.File).Contains(stem, StringComparison.OrdinalIgnoreCase)).ToArray())
                    await ReadAdapterAsync(adapter);
                // Generated adapters share one DataSet Designer file whose name rarely contains the adapter's stem
                // (InfoResultImageTableAdapter lives in DSInfoResult.Designer.cs). Fall back to a text search for the class.
                if (!foundAdapter)
                {
                    var candidates = await SourceCandidatesAsync("class " + adapterType, token);
                    foreach (var adapter in _sourceFiles.Where(source => !searched.Contains(source.File) && !candidates.Skip(source.File)).ToArray())
                    {
                        var text = await File.ReadAllTextAsync(adapter.File, token);
                        if (Regex.IsMatch(text, @"\bclass\s+" + Regex.Escape(adapterType) + @"\b")) await ReadAdapterAsync(adapter);
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
    private static readonly Regex SqlStatement = new(@"^\s*(INSERT\s+INTO|UPDATE|DELETE\s+FROM)\s+(\[?\w+\]?(?:\.\[?\w+\]?)?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    // "INSERT INTO [InfoTestResultImage] ..." → "INSERT INTO InfoTestResultImage". Reads the leading literal of a
    // concatenation, which is how the Designer splits long SQL.
    private static string? SqlStatementTarget(ExpressionSyntax expression)
    {
        while (expression is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression } concat) expression = concat.Left;
        if (expression is not LiteralExpressionSyntax literal || !literal.IsKind(SyntaxKind.StringLiteralExpression)) return null;
        var match = SqlStatement.Match(literal.Token.ValueText);
        return match.Success
            ? Regex.Replace(match.Groups[1].Value, @"\s+", " ").ToUpperInvariant() + " " + match.Groups[2].Value.Replace("[", "").Replace("]", "")
            : null;
    }
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
