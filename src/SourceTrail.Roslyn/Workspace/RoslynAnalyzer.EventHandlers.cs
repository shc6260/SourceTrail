using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SourceTrail.Core.Models;
using SymbolInfo = SourceTrail.Core.Models.SymbolInfo;

namespace SourceTrail.Roslyn.Workspace;

public sealed partial class RoslynAnalyzer
{
    private const string DesignerSuffix = ".Designer.cs";

    public async Task<Page<EventHandlerMatch>> FindEventHandlersAsync(string file, string? query, int offset, int limit,
        CancellationToken cancellationToken)
    {
        RequireText(file, nameof(file)); ValidatePage(offset, limit);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var solutionRoot = GetSolutionRoot();
            var path = ResolveSolutionPath(solutionRoot, file);
            if (!Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                throw new ArgumentException("Specify an existing C# form or Designer file.", nameof(file));
            var codeFile = path.EndsWith(DesignerSuffix, StringComparison.OrdinalIgnoreCase)
                ? path[..^DesignerSuffix.Length] + ".cs" : path;
            var designerFile = path.EndsWith(DesignerSuffix, StringComparison.OrdinalIgnoreCase)
                ? path : Path.ChangeExtension(path, DesignerSuffix);
            if (!File.Exists(designerFile)) return Paginate<EventHandlerMatch>([], offset, limit);
            ResolveSolutionPath(solutionRoot, designerFile);

            var designer = await ParseSourceAsync(designerFile, cancellationToken);
            var bindings = ReadBindings(designer, query).ToArray();
            if (bindings.Length == 0) return Paginate<EventHandlerMatch>([], offset, limit);

            var declarations = designer.DescendantNodes().OfType<MethodDeclarationSyntax>();
            if (File.Exists(codeFile))
            {
                ResolveSolutionPath(solutionRoot, codeFile);
                var source = await ParseSourceAsync(codeFile, cancellationToken);
                declarations = declarations.Concat(source.DescendantNodes().OfType<MethodDeclarationSyntax>());
            }
            var methods = declarations.ToLookup(method =>
                (method.FirstAncestorOrSelf<TypeDeclarationSyntax>()?.Identifier.ValueText, method.Identifier.ValueText));
            if (_diskIndex is not null && _indexedSymbols is null)
                _indexedSymbols = await ReadDiskItemAsync<SymbolInfo[]>("symbols", cancellationToken);
            var handlerNames = bindings.Select(binding => binding.Handler).ToHashSet(StringComparer.Ordinal);
            var availableSymbols = _indexedSymbols ?? (_symbolsReady
                ? _symbols.Values.Where(item => item.Symbol is IMethodSymbol && handlerNames.Contains(item.Symbol.Name))
                    .Select(item => Describe(item.Symbol, item.Project)).ToArray()
                : []);
            var indexed = availableSymbols.Where(symbol => symbol.SymbolKind == "Method" &&
                    (StringComparer.OrdinalIgnoreCase.Equals(symbol.File, codeFile) ||
                     StringComparer.OrdinalIgnoreCase.Equals(symbol.File, designerFile)))
                .ToLookup(symbol => (symbol.Name, symbol.Line));
            var matches = new List<EventHandlerMatch>();
            foreach (var binding in bindings)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var handlers = methods[(binding.Type, binding.Handler)].ToArray();
                if (handlers.Length == 0)
                    matches.Add(new(binding.Control, binding.Event, binding.Handler, designerFile, binding.Line, null, null, null));
                foreach (var handler in handlers)
                {
                    var span = handler.Identifier.GetLocation().GetLineSpan();
                    int line = span.StartLinePosition.Line + 1;
                    var symbols = indexed[(binding.Handler, line)]
                        .Where(symbol => StringComparer.OrdinalIgnoreCase.Equals(symbol.File, span.Path)).Take(2).ToArray();
                    matches.Add(new(binding.Control, binding.Event, binding.Handler, designerFile, binding.Line,
                        span.Path, line, symbols is { Length: 1 } && handlers.Length == 1 ? symbols[0].Id : null));
                }
            }
            return Paginate(matches.ToArray(), offset, limit);
        }
        finally { _gate.Release(); }
    }

    private static IEnumerable<(string? Type, string Control, string Event, string Handler, int Line)> ReadBindings(
        SyntaxNode designer, string? query)
    {
        foreach (var assignment in designer.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (!assignment.IsKind(SyntaxKind.AddAssignmentExpression) ||
                assignment.FirstAncestorOrSelf<MethodDeclarationSyntax>()?.Identifier.ValueText != "InitializeComponent" ||
                assignment.Left is not MemberAccessExpressionSyntax access) continue;
            var handler = assignment.Right is ObjectCreationExpressionSyntax { ArgumentList.Arguments: [var argument] }
                ? argument.Expression : assignment.Right;
            string? name = handler switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } member => member.Name.Identifier.ValueText,
                _ => null,
            };
            if (name is null) continue;
            string control = access.Expression is MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } field
                ? field.Name.Identifier.ValueText : access.Expression.ToString();
            string eventName = access.Name.Identifier.ValueText;
            if (!string.IsNullOrWhiteSpace(query) &&
                !control.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !eventName.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            yield return (assignment.FirstAncestorOrSelf<TypeDeclarationSyntax>()?.Identifier.ValueText,
                control, eventName, name, assignment.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
        }
    }

    private static async Task<SyntaxNode> ParseSourceAsync(string path, CancellationToken cancellationToken)
    {
        var text = await File.ReadAllTextAsync(path, cancellationToken);
        return CSharpSyntaxTree.ParseText(text, path: path, cancellationToken: cancellationToken).GetRoot(cancellationToken);
    }

    private string GetSolutionRoot()
    {
        var path = _state.SolutionPath;
        if (path is null && !string.IsNullOrWhiteSpace(_options.SolutionPath))
            path = ValidateSolutionPath(_options.SolutionPath);
        return path is not null ? Path.GetDirectoryName(path)! :
            throw new InvalidOperationException("No solution loaded. Call load_solution first.");
    }

    private static string ResolveSolutionPath(string root, string input)
    {
        var path = Path.GetFullPath(input, root);
        var relative = Path.GetRelativePath(root, path);
        var current = root;
        foreach (var component in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (component == ".") continue;
            if (component == ".." || component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                ExcludedDirectories.Contains(component))
                throw new ArgumentException("Path must be inside a searchable folder of the loaded solution.", nameof(input));
            current = Path.Combine(current, component);
            if ((Directory.Exists(current) || File.Exists(current)) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new ArgumentException("Path must not traverse a reparse point.", nameof(input));
        }
        if (Path.IsPathRooted(relative))
            throw new ArgumentException("Path must be inside the loaded solution.", nameof(input));
        return path;
    }
}
