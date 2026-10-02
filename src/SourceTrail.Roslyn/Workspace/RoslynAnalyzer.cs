using System.Collections.Concurrent;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Operations;
using SourceTrail.Core.Contracts;
using SourceTrail.Core.Models;
using SymbolInfo = SourceTrail.Core.Models.SymbolInfo;
using RSymbol = Microsoft.CodeAnalysis.ISymbol;

namespace SourceTrail.Roslyn.Workspace;

/// <summary>C# 솔루션의 분석 상태를 유지하고 심볼·참조·호출 근거를 제공한다.</summary>
public sealed class RoslynAnalyzer(AnalysisOptions? options = null, Action<string>? log = null) : ICodeAnalyzer, IRefreshableCodeAnalyzer
{
    private readonly AnalysisOptions _options = options ?? new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly object LocatorGate = new();
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", ".git", ".svn", ".vs", "node_modules" };
    private static readonly HashSet<string> SearchExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".config", ".xml", ".sql" };
    private MSBuildWorkspace? _workspace;
    private Solution? _solution;
    private readonly Dictionary<string, (RSymbol Symbol, ProjectId Project)> _symbols = new();
    private readonly Dictionary<string, CodeExpansion> _expansions = new();
    private IReadOnlyList<ProcedureUsage>? _usages;
    private SolutionInputTracker _inputs = new();
    private bool _reloadRequired;
    private SolutionState _state = new("NotLoaded", null, null, null, [], []);

    public SolutionState Status() => _state;

    public Task<SolutionState> ReloadAsync(CancellationToken cancellationToken) =>
        LoadAsync(_state.SolutionPath ?? throw new InvalidOperationException("Load a solution first."), cancellationToken);

    public async Task<SolutionState> LoadAsync(string solutionPath, CancellationToken cancellationToken)
    {
        string path = ValidateSolutionPath(solutionPath);
        await _gate.WaitAsync(cancellationToken);
        MSBuildWorkspace? pending = null;
        try
        {
            log?.Invoke($"Loading solution: {path}");
            RegisterMsBuild();
            var diagnostics = new ConcurrentQueue<AnalysisDiagnostic>();
            pending = CreateWorkspace(diagnostics);
            var solution = await pending.OpenSolutionAsync(path, cancellationToken: cancellationToken);
            var projects = await CollectProjectDiagnosticsAsync(solution, diagnostics, cancellationToken);
            AddMissingProjectDiagnostics(path, solution, diagnostics);
            var collected = diagnostics.ToArray();
            var nextState = new SolutionState(collected.Any(d => d.Severity is "Failure" or "Error") || projects.Count == 0
                ? "Partial" : "Ready", path, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, projects, collected);
            var nextInputs = new SolutionInputTracker();
            await nextInputs.ChangedAsync(nextState, InputPaths(solution), cancellationToken);
            // 준비에 실패하면 기존 스냅샷을 유지하고, 성공한 결과만 공개한다.
            PublishSolution(pending, solution, nextState, nextInputs);
            pending = null;
            log?.Invoke($"Solution loaded: {projects.Count} projects, status {_state.Status}.");
            return _state;
        }
        finally { pending?.Dispose(); _gate.Release(); }
    }

    private IEnumerable<string> InputPaths(Solution? solution = null) => (solution ?? _solution)?.Projects.SelectMany(p =>
        p.Documents.Select(d => d.FilePath).Concat(p.AdditionalDocuments.Select(d => d.FilePath))
        .Concat(p.AnalyzerConfigDocuments.Select(d => d.FilePath))
        .Concat(p.AnalyzerReferences.Select(r => r.FullPath))
        .Concat(p.FilePath is null ? [] : new[] { p.FilePath,
            Path.Combine(Path.GetDirectoryName(p.FilePath)!, "obj", "project.assets.json"),
            Path.Combine(Path.GetDirectoryName(p.FilePath)!, "obj", Path.GetFileName(p.FilePath) + ".nuget.g.props"),
            Path.Combine(Path.GetDirectoryName(p.FilePath)!, "obj", Path.GetFileName(p.FilePath) + ".nuget.g.targets") })
        .Concat(p.MetadataReferences.OfType<PortableExecutableReference>().Select(r => r.FilePath)))
        .Where(p => p is not null).Cast<string>() ?? [];
    public async Task EnsureFreshAsync(CancellationToken cancellationToken)
    {
        if (_state.SolutionPath is null) return;
        if (_reloadRequired || await _inputs.ChangedAsync(_state, InputPaths(), cancellationToken))
        {
            try { await ReloadAsync(cancellationToken); _reloadRequired = false; }
            catch { _reloadRequired = true; throw; }
        }
    }

    private static void RegisterMsBuild()
    {
        lock (LocatorGate)
        {
            if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterDefaults();
        }
    }

    private static IEnumerable<string> ReadSolutionProjectPaths(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            if (!line.StartsWith("Project(", StringComparison.Ordinal)) continue;
            var parts = line.Split('"');
            if (parts.Length < 6) continue;
            var relative = parts[5];
            if (!relative.EndsWith("proj", StringComparison.OrdinalIgnoreCase)) continue;
            yield return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, relative));
        }
    }

    public async Task<Page<SymbolInfo>> FindSymbolsAsync(string query, int offset, int limit, CancellationToken cancellationToken)
    {
        RequireText(query, nameof(query)); ValidatePage(offset, limit);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureSymbolsAsync(cancellationToken);
            var matches = _symbols.Values
                .Where(s => s.Symbol.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    s.Symbol.ToDisplayString().Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(s => Describe(s.Symbol, s.Project)).OrderBy(s => s.Project)
                .ThenBy(s => s.FullName).ToArray();
            return Paginate(matches, offset, limit);
        }
        finally { _gate.Release(); }
    }

    public async Task<SymbolOverview> OverviewAsync(string symbolOrFile, CancellationToken cancellationToken)
    {
        RequireText(symbolOrFile, nameof(symbolOrFile));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureSymbolsAsync(cancellationToken);
            var entries = FindOverviewEntries(symbolOrFile);
            var items = entries.Select(s => Describe(s.Symbol, s.Project)).OrderBy(s => s.Line).ToArray();
            return new(items.Where(s => s.SymbolKind == "NamedType").ToArray(),
                items.Where(s => s.SymbolKind == "Field").ToArray(),
                items.Where(s => s.SymbolKind == "Property").ToArray(),
                items.Where(s => s.SymbolKind is "Method" or "LocalFunction").ToArray(),
                items.Where(s => s.SymbolKind == "Constructor").ToArray(),
                items.Where(s => s.SymbolKind == "Event").ToArray(), _state.SnapshotId, Warnings());
        }
        finally { _gate.Release(); }
    }

    private static bool IsInside(RSymbol symbol, INamedTypeSymbol type)
    {
        for (var owner = symbol.ContainingType; owner is not null; owner = owner.ContainingType)
            if (SymbolEqualityComparer.Default.Equals(owner, type)) return true;
        return false;
    }

    public async Task<Page<ReferenceInfo>> ReferencesAsync(string symbol, int offset, int limit, CancellationToken cancellationToken)
    {
        ValidatePage(offset, limit);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureSymbolsAsync(cancellationToken);
            var selected = Resolve(symbol);
            var found = await SymbolFinder.FindReferencesAsync(selected.Symbol, RequireSolution(), cancellationToken);
            var results = new List<ReferenceInfo>();
            foreach (var reference in found.SelectMany(r => r.Locations))
            {
                var document = reference.Document;
                var model = await document.GetSemanticModelAsync(cancellationToken);
                var containing = model?.GetEnclosingSymbol(reference.Location.SourceSpan.Start, cancellationToken);
                containing = EnclosingDeclaration(containing);
                var span = reference.Location.GetLineSpan();
                results.Add(new(containing is null ? null : Describe(containing, document.Project.Id),
                    document.Project.Name, span.Path, span.StartLinePosition.Line + 1,
                    span.StartLinePosition.Character + 1, "RoslynReference"));
            }
            return Paginate(results.DistinctBy(r => (r.Project, r.File, r.Line, r.Column)).ToArray(), offset, limit);
        }
        finally { _gate.Release(); }
    }

    public async Task<Page<TextMatch>> SearchTextAsync(string text, int offset, int limit, CancellationToken cancellationToken)
    {
        RequireText(text, nameof(text)); ValidatePage(offset, limit);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            RequireSolution();
            var matches = new List<TextMatch>();
            var roots = new[] { Path.GetDirectoryName(_state.SolutionPath!)! }.Concat(_options.AdditionalSearchRoots);
            foreach (var file in roots.SelectMany(EnumerateSearchFiles).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                CollectTextMatches(file, text, matches, cancellationToken);
            }
            return Paginate(matches.ToArray(), offset, limit);
        }
        finally { _gate.Release(); }
    }

    private static IEnumerable<string> EnumerateSearchFiles(string root)
    {
        if (!Directory.Exists(root)) yield break;
        var pending = new Stack<string>(); pending.Push(Path.GetFullPath(root));
        while (pending.TryPop(out var current))
        {
            string[] files; string[] directories;
            try { files = Directory.GetFiles(current); directories = Directory.GetDirectories(current); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var file in files)
                if (SearchExtensions.Contains(Path.GetExtension(file))) yield return file;
            foreach (var directory in directories)
                if (!ExcludedDirectories.Contains(Path.GetFileName(directory)) &&
                    (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0) pending.Push(directory);
        }
    }

    public async Task<Page<ProcedureUsage>> ProcedureUsagesAsync(string procedure, int offset, int limit, CancellationToken cancellationToken)
    {
        RequireText(procedure, nameof(procedure)); ValidatePage(offset, limit);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureUsagesAsync(cancellationToken);
            var results = _usages!.Where(u => ProcedureMatches(u.Procedure, procedure)).ToArray();
            return Paginate(results, offset, limit);
        }
        finally { _gate.Release(); }
    }

    private static bool ProcedureMatches(string candidate, string requested)
    {
        static string Normalize(string value) => value.Replace("[", "").Replace("]", "").Trim();
        candidate = Normalize(candidate); requested = Normalize(requested);
        return requested.Contains('.') ? candidate.Equals(requested, StringComparison.OrdinalIgnoreCase)
            : candidate.Split('.').Last().Equals(requested, StringComparison.OrdinalIgnoreCase);
    }

    private async Task EnsureUsagesAsync(CancellationToken cancellationToken)
    {
        if (_usages is not null) return;
        await EnsureSymbolsAsync(cancellationToken);
        var usages = new List<ProcedureUsage>();
        foreach (var project in RequireSolution().Projects)
            foreach (var document in project.Documents)
            {
                var root = await document.GetSyntaxRootAsync(cancellationToken);
                var model = await document.GetSemanticModelAsync(cancellationToken);
                if (root is null || model is null) continue;
                CollectConfiguredCalls(document, root, model, usages, cancellationToken);
                CollectCommandConfigurations(document, root, model, usages, cancellationToken);
                CollectStringCandidates(document, root, model, usages, cancellationToken);
            }
        _usages = usages.DistinctBy(u => (u.Procedure, u.File, u.Line, u.ContainingSymbol?.Id, u.RelationType)).ToArray();
    }

    private static bool IsDbCommand(ITypeSymbol? type)
    {
        for (var current = type as INamedTypeSymbol; current is not null; current = current.BaseType)
            if (current.ToDisplayString() == "System.Data.Common.DbCommand") return true;
        return false;
    }

    public async Task<CodeExpansion> ExpandAsync(string symbol, bool callers, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureUsagesAsync(cancellationToken);
            var selected = Resolve(symbol);
            string key = Id(selected.Symbol, selected.Project) + (callers ? ":in" : ":out");
            if (_expansions.TryGetValue(key, out var cached)) return cached;
            var described = Describe(selected.Symbol, selected.Project);
            var relations = callers
                ? await IncomingAsync(selected.Symbol, cancellationToken)
                : await OutgoingAsync(selected.Symbol, selected.Project, cancellationToken);
            var warnings = new List<string>();
            if (selected.Symbol.ContainingType?.GetAttributes().Any(a => a.AttributeClass?.Name == "ServiceContractAttribute") == true)
                warnings.Add("WCF contract boundary: a remote implementation is not inferred from method names.");
            var result = new CodeExpansion(described, relations,
                _usages!.Where(u => u.ContainingSymbol?.Id == described.Id && u.IsVerifiedCall).ToArray(), warnings);
            _expansions[key] = result;
            return result;
        }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<CodeRelation>> OutgoingAsync(RSymbol symbol, ProjectId projectId, CancellationToken cancellationToken)
    {
        var relations = new List<CodeRelation>();
        foreach (var syntaxReference in symbol.DeclaringSyntaxReferences)
        {
            var node = await syntaxReference.GetSyntaxAsync(cancellationToken);
            var document = RequireSolution().GetDocument(node.SyntaxTree);
            if (document is null) continue;
            var model = await document.GetSemanticModelAsync(cancellationToken);
            if (model is null) continue;
            foreach (var call in node.DescendantNodes().Where(n => n is InvocationExpressionSyntax or ObjectCreationExpressionSyntax))
            {
                await AddOutgoingCallAsync(call, symbol, projectId, document, model, relations, cancellationToken);
            }
        }
        return relations.DistinctBy(r => (r.From.Id, r.To.Id, r.RelationType, r.File, r.Line)).ToArray();
    }

    private async Task<IReadOnlyList<CodeRelation>> IncomingAsync(RSymbol symbol, CancellationToken cancellationToken)
    {
        var relations = new List<CodeRelation>();
        var definitions = FindIncomingDefinitions(symbol);
        foreach (var definition in definitions)
        {
            var found = await SymbolFinder.FindReferencesAsync(definition, RequireSolution(), cancellationToken);
            foreach (var reference in found.SelectMany(r => r.Locations))
            {
                await AddIncomingReferenceAsync(reference, symbol, definition, relations, cancellationToken);
            }
        }
        return relations.DistinctBy(r => (r.From.Id, r.To.Id, r.RelationType, r.File, r.Line)).ToArray();
    }

    private static bool SameSymbol(RSymbol left, RSymbol right) =>
        SymbolEqualityComparer.Default.Equals(left.OriginalDefinition, right.OriginalDefinition) ||
        left.OriginalDefinition.GetDocumentationCommentId() is string id &&
        id == right.OriginalDefinition.GetDocumentationCommentId() &&
        left.ContainingAssembly?.Identity.Equals(right.ContainingAssembly?.Identity) == true;
    private (RSymbol Symbol, ProjectId Project)? FindSourceEntry(RSymbol symbol)
    {
        string? docId = symbol.OriginalDefinition.GetDocumentationCommentId();
        var entries = _symbols.Values.Where(s => SymbolEqualityComparer.Default.Equals(s.Symbol, symbol.OriginalDefinition) ||
            docId is not null && s.Symbol.GetDocumentationCommentId() == docId &&
            s.Symbol.ContainingAssembly?.Identity.Equals(symbol.ContainingAssembly?.Identity) == true).ToArray();
        return entries.Length == 1 ? entries[0] : null;
    }

    private async Task EnsureSymbolsAsync(CancellationToken cancellationToken)
    {
        var solution = RequireSolution();
        if (_symbols.Count > 0) return;
        foreach (var project in solution.Projects)
            foreach (var document in project.Documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var root = await document.GetSyntaxRootAsync(cancellationToken);
                var model = await document.GetSemanticModelAsync(cancellationToken);
                if (root is null || model is null) continue;
                foreach (var node in root.DescendantNodes().Where(n => n is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax or
                    BaseMethodDeclarationSyntax or PropertyDeclarationSyntax or IndexerDeclarationSyntax or EventDeclarationSyntax or
                    LocalFunctionStatementSyntax or EnumMemberDeclarationSyntax ||
                    n is VariableDeclaratorSyntax { Parent.Parent: FieldDeclarationSyntax or EventFieldDeclarationSyntax }))
                {
                    var symbol = model.GetDeclaredSymbol(node, cancellationToken);
                    if (symbol is null) continue;
                    _symbols.TryAdd(Id(symbol, project.Id), (symbol, project.Id));
                }
            }
    }

    private (RSymbol Symbol, ProjectId Project) Resolve(string query)
    {
        RequireText(query, nameof(query));
        if (_symbols.TryGetValue(query, out var symbol)) return symbol;
        var candidates = _symbols.Values.Where(s => s.Symbol.Name.Equals(query, StringComparison.Ordinal) ||
            s.Symbol.ToDisplayString().Equals(query, StringComparison.Ordinal) ||
            s.Symbol.GetDocumentationCommentId() == query).ToArray();
        return candidates.Length switch
        {
            1 => candidates[0],
            0 => throw new ArgumentException("Symbol not found. Use find_symbol first."),
            _ => throw new ArgumentException("Ambiguous symbol. Use find_symbol and pass an exact symbol ID.")
        };
    }

    private SymbolInfo Describe(RSymbol symbol, ProjectId projectId)
    {
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        var kind = symbol is IMethodSymbol method ? method.MethodKind switch
        {
            MethodKind.Constructor or MethodKind.StaticConstructor => "Constructor",
            MethodKind.LocalFunction => "LocalFunction",
            _ => "Method"
        } : symbol.Kind.ToString();
        bool isUi = false;
        for (var type = symbol as INamedTypeSymbol ?? symbol.ContainingType; type is not null; type = type.BaseType)
            if (type.ToDisplayString() == "System.Windows.Forms.Form") isUi = true;
        return new(Id(symbol, projectId), symbol.Name, kind, symbol.ContainingNamespace?.ToDisplayString() ?? "",
            symbol.ToDisplayString(), symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            RequireSolution().GetProject(projectId)!.Name, location?.SourceTree?.FilePath,
            location is null ? null : location.GetLineSpan().StartLinePosition.Line + 1,
            symbol.ContainingType?.ToDisplayString(), isUi);
    }

    private static string Id(RSymbol symbol, ProjectId projectId) =>
        projectId.Id.ToString("N") + "|" + (symbol.GetDocumentationCommentId() ??
            symbol.ToDisplayString() + "@" + symbol.Locations.FirstOrDefault(l => l.IsInSource)?.SourceSpan.Start);
    private static RSymbol? EnclosingDeclaration(RSymbol? symbol)
    {
        while (symbol is not null && (symbol.Kind is SymbolKind.Local or SymbolKind.Parameter ||
            symbol is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction }))
            symbol = symbol.ContainingSymbol;
        if (symbol is IMethodSymbol { AssociatedSymbol: not null } method) return method.AssociatedSymbol;
        return symbol;
    }
    private Solution RequireSolution() => _solution ?? throw new InvalidOperationException("No solution loaded. Call load_solution first.");
    private string[] Warnings() => _state.Status == "Partial"
        ? ["Solution is partially loaded. Missing results do not prove absence of references or calls."] : [];
    private Page<T> Paginate<T>(IReadOnlyList<T> items, int offset, int limit) =>
        new(items.Skip(offset).Take(limit).ToArray(), items.Count, offset, limit,
            (long)offset + limit < items.Count, _state.SnapshotId, Warnings());
    private static void RequireText(string text, string name)
    { if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException($"{name} is required."); }
    private static void ValidatePage(int offset, int limit)
    { if (offset < 0 || limit is < 1 or > 1000) throw new ArgumentException("offset >= 0 and limit between 1 and 1000 are required."); }
    public void Dispose() { _workspace?.Dispose(); _gate.Dispose(); }

    // 프로젝트 오류는 제한된 상세 진단과 전체 오류 건수로 보존한다.
    private static async Task<List<ProjectState>> CollectProjectDiagnosticsAsync(Solution solution,
        ConcurrentQueue<AnalysisDiagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var projects = new List<ProjectState>();
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            var errors = compilation?.GetDiagnostics(cancellationToken)
                .Where(d => d.Severity == DiagnosticSeverity.Error).ToArray() ?? [];
            projects.Add(new(project.Name, project.FilePath, project.DocumentIds.Count, errors.Length));
            foreach (var error in errors.Take(20))
                diagnostics.Enqueue(new("Error", error.ToString(), project.Name));
            if (compilation is null)
                diagnostics.Enqueue(new("Error", "Compilation could not be created.", project.Name));
        }
        return projects;
    }

    private static void AddMissingProjectDiagnostics(string path, Solution solution,
        ConcurrentQueue<AnalysisDiagnostic> diagnostics)
    {
        // Unsupported/skipped projects must be visible rather than silently treated as analyzed.
        foreach (var projectPath in ReadSolutionProjectPaths(path))
        {
            if (!solution.Projects.Any(p => p.FilePath is not null &&
                Path.GetFullPath(p.FilePath).Equals(projectPath, StringComparison.OrdinalIgnoreCase)))
                diagnostics.Enqueue(new("Error", $"Project missing from loaded solution: {projectPath}"));
        }
    }

    // 로딩과 입력 대조가 모두 끝난 뒤 교체한다. 이전 심볼/관계 캐시는 새 스냅샷에 넘기지 않는다.
    private void PublishSolution(MSBuildWorkspace workspace, Solution solution,
        SolutionState nextState, SolutionInputTracker nextInputs)
    {
        _workspace?.Dispose();
        _workspace = workspace;
        _solution = solution;
        _symbols.Clear();
        _expansions.Clear();
        _usages = null;
        _state = nextState;
        _inputs = nextInputs;
    }


    // 등록한 DB 래퍼의 상수 인자만 검증된 프로시저 호출로 수집한다.
    private void CollectConfiguredCalls(Document document, SyntaxNode root, SemanticModel model,
        List<ProcedureUsage> usages, CancellationToken cancellationToken)
    {
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (model.GetOperation(invocation, cancellationToken) is not IInvocationOperation operation) continue;
            var rule = _options.ProcedureCallRules.FirstOrDefault(r =>
                operation.TargetMethod.ContainingType.ToDisplayString() == r.TypeName &&
                operation.TargetMethod.Name == r.MethodName);
            if (rule is null) continue;
            var argument = operation.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == rule.ArgumentIndex);
            if (argument?.Value.ConstantValue is not { HasValue: true, Value: string name }) continue;
            var symbol = EnclosingDeclaration(model.GetEnclosingSymbol(invocation.SpanStart, cancellationToken));
            if (symbol is null) continue;
            usages.Add(new(name, document.FilePath!, invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                Describe(symbol, document.Project.Id), "ConfiguredProcedureCall", true,
                $"Roslyn resolved {operation.TargetMethod.ToDisplayString()}; configured procedure argument {rule.ArgumentIndex}."));
        }

    }

    // 명령 초기화는 실행 증거가 아니므로 호출 흐름에 넣지 않는 후보로 수집한다.
    private void CollectCommandConfigurations(Document document, SyntaxNode root, SemanticModel model,
        List<ProcedureUsage> usages, CancellationToken cancellationToken)
    {
        // Only a DbCommand initializer with both a constant CommandText and StoredProcedure
        // is recognized automatically. Runtime assignments/concatenations are not guessed.
        foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            if (model.GetOperation(creation, cancellationToken) is not IObjectCreationOperation operation ||
                !IsDbCommand(operation.Type)) continue;
            var assignments = operation.Initializer?.Initializers.OfType<ISimpleAssignmentOperation>().ToArray() ?? [];
            bool storedProcedure = assignments.Any(a => a.Target is IPropertyReferenceOperation p &&
                p.Property.Name == "CommandType" && a.Value.ConstantValue.HasValue &&
                Convert.ToInt32(a.Value.ConstantValue.Value) == 4);
            if (!storedProcedure) continue;
            string? name = assignments.FirstOrDefault(a => a.Target is IPropertyReferenceOperation p &&
                p.Property.Name == "CommandText")?.Value.ConstantValue.Value as string;
            name ??= operation.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == 0)?.Value.ConstantValue.Value as string;
            if (name is null) continue;
            var symbol = EnclosingDeclaration(model.GetEnclosingSymbol(creation.SpanStart, cancellationToken));
            if (symbol is null) continue;
            usages.Add(new(name, document.FilePath!, creation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                Describe(symbol, document.Project.Id), "ProcedureCommandConfiguration", false,
                "DbCommand creation with a constant name and CommandType.StoredProcedure initializer; execution is not proven."));
        }

    }

    // 주석을 제외한 문자열/상수는 검색 후보이며 DB 호출로 확정하지 않는다.
    private void CollectStringCandidates(Document document, SyntaxNode root, SemanticModel model,
        List<ProcedureUsage> usages, CancellationToken cancellationToken)
    {
        foreach (var node in root.DescendantNodes().Where(n => n is LiteralExpressionSyntax or IdentifierNameSyntax or MemberAccessExpressionSyntax))
        {
            // Keep string/constant candidates even if they do not reach a registered execution API.
            var value = model.GetConstantValue(node, cancellationToken);
            if (!value.HasValue || value.Value is not string name || name.Length > 256 || name.Length == 0) continue;
            var symbol = EnclosingDeclaration(model.GetEnclosingSymbol(node.SpanStart, cancellationToken));
            usages.Add(new(name, document.FilePath!, node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                symbol is null ? null : Describe(symbol, document.Project.Id), "TextMatch", false,
                "String/constant candidate only. DB execution is not verified."));
        }
    }



    private async Task AddOutgoingCallAsync(SyntaxNode call, RSymbol symbol, ProjectId projectId,
        Document document, SemanticModel model, List<CodeRelation> relations, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var owner = EnclosingDeclaration(model.GetEnclosingSymbol(call.SpanStart, cancellationToken));
        if (owner is null || !SymbolEqualityComparer.Default.Equals(owner, symbol)) return;
        IMethodSymbol? target = model.GetOperation(call, cancellationToken) switch
        {
            IInvocationOperation invocation => invocation.TargetMethod,
            IObjectCreationOperation creation => creation.Constructor,
            _ => null
        };
        if (target is null) return;
        target = (target.ReducedFrom ?? target).OriginalDefinition;
        var targetEntry = FindSourceEntry(target);
        if (targetEntry is null) return;
        relations.Add(new(Describe(symbol, projectId), Describe(targetEntry.Value.Symbol, targetEntry.Value.Project),
            "RoslynInvocation", document.FilePath!, call.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            "Invocation target resolved by Roslyn. Static call site; runtime execution is not proven."));
        await AddDispatchCandidatesAsync(target, targetEntry.Value, document, call, relations, cancellationToken);
    }

    // 인터페이스/가상 메서드 구현은 정적 후보다. 실제 실행 대상으로 확정하지 않는다.
    private async Task AddDispatchCandidatesAsync(IMethodSymbol target, (RSymbol Symbol, ProjectId Project) targetEntry,
        Document document, SyntaxNode call, List<CodeRelation> relations, CancellationToken cancellationToken)
    {
        if (target.ContainingType.TypeKind == TypeKind.Interface || target.IsAbstract || target.IsVirtual)
        {
            var implementations = await SymbolFinder.FindImplementationsAsync(targetEntry.Symbol, RequireSolution(), cancellationToken: cancellationToken);
            foreach (var implementation in implementations)
            {
                var entry = FindSourceEntry(implementation);
                if (entry is not null && !SymbolEqualityComparer.Default.Equals(entry.Value.Symbol, targetEntry.Symbol))
                    relations.Add(new(Describe(targetEntry.Symbol, targetEntry.Project),
                        Describe(entry.Value.Symbol, entry.Value.Project), "DispatchCandidate",
                        document.FilePath!, call.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        "Implementation candidate; runtime dispatch target requires verification."));
            }
        }
    }


    private static List<RSymbol> FindIncomingDefinitions(RSymbol symbol)
    {
        var definitions = new List<RSymbol> { symbol };
        if (symbol is IMethodSymbol method)
            foreach (var contract in method.ContainingType.AllInterfaces)
                foreach (var member in contract.GetMembers().OfType<IMethodSymbol>())
                    if (SymbolEqualityComparer.Default.Equals(method.ContainingType.FindImplementationForInterfaceMember(member), method))
                        definitions.Add(member);
        return definitions;
    }

    private async Task AddIncomingReferenceAsync(ReferenceLocation reference, RSymbol symbol, RSymbol definition,
        List<CodeRelation> relations, CancellationToken cancellationToken)
    {
        var document = reference.Document;
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (root is null || model is null) return;
        var tokenNode = root.FindNode(reference.Location.SourceSpan, getInnermostNodeForTie: true);
        var owner = EnclosingDeclaration(model.GetEnclosingSymbol(tokenNode.SpanStart, cancellationToken));
        if (owner is null) return;
        var relation = ClassifyIncomingReference(tokenNode, model, definition, cancellationToken);
        if (relation is null) return; // A reference or delegate value alone is not a call.
        var targetEntry = FindSourceEntry(symbol);
        if (targetEntry is null) return;
        if (!SymbolEqualityComparer.Default.Equals(definition, symbol)) relation = "DispatchCandidate";
        relations.Add(new(Describe(owner, document.Project.Id), Describe(targetEntry.Value.Symbol, targetEntry.Value.Project),
            relation, document.FilePath!, reference.Location.GetLineSpan().StartLinePosition.Line + 1,
            relation == "DispatchCandidate" ? "Interface caller; runtime dispatch to this implementation requires verification."
            : relation == "EventSubscription" ? "Event handler subscription resolved by Roslyn; event firing is not proven."
            : "Caller invocation resolved by Roslyn."));
    }

    // 단순 참조/델리게이트 대입과 실제 호출/이벤트 등록을 구분한다.
    private static string? ClassifyIncomingReference(SyntaxNode tokenNode, SemanticModel model,
        RSymbol definition, CancellationToken cancellationToken)
    {
        var call = tokenNode.AncestorsAndSelf().FirstOrDefault(n => n is InvocationExpressionSyntax or ObjectCreationExpressionSyntax);
        IMethodSymbol? target = call is null ? null : model.GetOperation(call, cancellationToken) switch
        {
            IInvocationOperation invocation => invocation.TargetMethod,
            IObjectCreationOperation creation => creation.Constructor,
            _ => null
        };
        string? relation = target is not null && SameSymbol(
            (target.ReducedFrom ?? target).OriginalDefinition, definition.OriginalDefinition) ? "RoslynInvocation" : null;
        var subscription = tokenNode.AncestorsAndSelf().OfType<AssignmentExpressionSyntax>()
            .FirstOrDefault(a => a.IsKind(SyntaxKind.AddAssignmentExpression) && a.Right.Span.Contains(tokenNode.Span) &&
                model.GetSymbolInfo(a.Left, cancellationToken).Symbol is IEventSymbol &&
                SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(a.Right, cancellationToken).Symbol, definition));
        if (subscription is not null) relation = "EventSubscription";
        return relation;
    }


    private static void CollectTextMatches(string file, string text,
        List<TextMatch> matches, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int lineNumber = 0;
        try
        {
            foreach (var line in File.ReadLines(file))
            {
                cancellationToken.ThrowIfCancellationRequested();
                lineNumber++;
                int position = line.IndexOf(text, StringComparison.OrdinalIgnoreCase);
                if (position >= 0) matches.Add(new(file, lineNumber, position + 1, line.Trim()[..Math.Min(line.Trim().Length, 240)]));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }


    private static string ValidateSolutionPath(string solutionPath)
    {
        if (string.IsNullOrWhiteSpace(solutionPath)) throw new ArgumentException("solutionPath is required.");
        string path = Path.GetFullPath(solutionPath);
        if (!Path.IsPathFullyQualified(solutionPath) || !File.Exists(path) || !Path.GetExtension(path).Equals(".sln", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Provide an existing .sln file.");
        return path;
    }

    private MSBuildWorkspace CreateWorkspace(ConcurrentQueue<AnalysisDiagnostic> diagnostics)
    {
        var workspace = MSBuildWorkspace.Create();
        workspace.RegisterWorkspaceFailedHandler(e =>
        {
            diagnostics.Enqueue(new(e.Diagnostic.Kind.ToString(), e.Diagnostic.Message));
            log?.Invoke(e.Diagnostic.Message);
        });
        return workspace;
    }


    private IEnumerable<(RSymbol Symbol, ProjectId Project)> FindOverviewEntries(string symbolOrFile)
    {
        IEnumerable<(RSymbol Symbol, ProjectId Project)> entries;
        if (Path.IsPathFullyQualified(symbolOrFile))
        {
            var file = Path.GetFullPath(symbolOrFile);
            entries = _symbols.Values.Where(s => s.Symbol.Locations.Any(l => l.IsInSource &&
                string.Equals(l.SourceTree?.FilePath, file, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (!entries.Any()) throw new ArgumentException("File is not an analyzed document in the loaded solution.");
        }
        else
        {
            var selected = Resolve(symbolOrFile);
            if (selected.Symbol is not INamedTypeSymbol type) throw new ArgumentException("Provide a type symbol ID or an absolute file path.");
            entries = _symbols.Values.Where(s => s.Project == selected.Project &&
                (SymbolEqualityComparer.Default.Equals(s.Symbol, type) || IsInside(s.Symbol, type))).ToArray();
        }
        return entries;
    }

}
