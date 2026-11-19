using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SourceTrail.Core;
using SourceTrail.Core.Contracts;
using SourceTrail.Core.Models;
using SymbolInfo = SourceTrail.Core.Models.SymbolInfo;

namespace SourceTrail.Roslyn.Workspace;

// Name retained for clients; only the C# syntax parser is used, never a compiler/workspace.
public sealed partial class RoslynAnalyzer(AnalysisOptions? options = null, Action<string>? log = null, bool requireCacheWrites = false)
    : ICodeAnalyzer, IRefreshableCodeAnalyzer, IDatabaseCallAnalyzer, IUiActionAnalyzer
{
    private readonly AnalysisOptions _options = options ?? new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", ".git", ".svn", ".vs", "node_modules" };
    private static readonly HashSet<string> SearchExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".config", ".xml", ".sql" };
    private const string CandidateWarning = "Text/syntax candidates only: receiver types, overload binding, imported build conditions and runtime dispatch are not resolved. Verify connections in source.";
    private SolutionState _state = new("NotLoaded", null, null, null, [], []);
    private DiskCodeIndex? _diskIndex;
    private SymbolInfo[] _indexedSymbols = [];
    private SourceFile[] _sourceFiles = [];
    private SolutionInputTracker.FileStamp[] _stamps = [];
    private SolutionInputMonitor? _inputMonitor;
    private bool _parsedDirty;
    private readonly Dictionary<string, SyntaxNode> _roots = new(StringComparer.OrdinalIgnoreCase);
    internal Action? SymbolDocumentIndexed { get; set; }
    private sealed record SourceFile(string File, string Hash, string Stamp, string Project, string? ProjectFile,
        string[] Defines, SymbolInfo[] Symbols, AnalysisDiagnostic[] Diagnostics);

    public SolutionState Status() => _state;
    public async Task<SolutionState> LoadAsync(string solutionPath, CancellationToken token)
    {
        var path = ValidateSolutionPath(solutionPath);
        await _gate.WaitAsync(token);
        try { return await LoadIndexAsync(path, false, token); }
        finally { _gate.Release(); }
    }
    public async Task<SolutionState> ReloadAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { return await LoadIndexAsync(_state.SolutionPath ?? throw new InvalidOperationException("Load a source folder or solution first."), true, token); }
        finally { _gate.Release(); }
    }
    private async Task<SolutionState> LoadIndexAsync(string path, bool forceHash, CancellationToken token)
    {
        using var measurement = AnalysisTelemetry.Measure("source.catalog.refresh");
        var disk = string.IsNullOrWhiteSpace(_options.CodeCacheDirectory) ? null : new DiskCodeIndex(_options.CodeCacheDirectory, path, _options, log, requireCacheWrites);
        var manifest = disk is null ? null : await disk.ReadManifestAsync(token);
        var projectPaths = ReadSolutionProjectPaths(path).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var seed = new SolutionState("Ready", path, null, null, projectPaths.Select(p => new ProjectState(Path.GetFileNameWithoutExtension(p), p, 0, null)).ToArray(), []);
        var nextMonitor = _options.WatchCodeFiles ? new SolutionInputMonitor(seed, [], TimeSpan.FromSeconds(Math.Clamp(_options.CodeRescanIntervalSeconds, 1, 3600))) : null;
        long observed = nextMonitor?.Version ?? 0; bool published = false;
        try
        {
            var stamps = await SolutionInputTracker.CaptureAsync(seed, [], manifest?.Files ?? [], token, hashContent: false);
            var oldFiles = disk is null || manifest?.State.SnapshotId is not { } snapshot ? _sourceFiles : await disk.ReadItemAsync<SourceFile[]>(snapshot, "source-files", token) ?? [];
            var old = oldFiles.ToDictionary(file => file.File, StringComparer.OrdinalIgnoreCase);
            var diagnostics = new List<AnalysisDiagnostic>();
            var projects = projectPaths.Select(project => (File: project, Folder: Path.GetDirectoryName(project)!, Defines: ReadDefines(project, diagnostics))).ToArray();
            var files = new List<SourceFile>(); int reused = 0; bool invalidated = false;
            foreach (var stamp in stamps.Where(file => file.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
            {
                token.ThrowIfCancellationRequested();
                var owner = projects.Where(project => IsInside(stamp.Path, project.Folder)).OrderByDescending(project => project.Folder.Length).FirstOrDefault();
                var defines = owner.Defines ?? [];
                bool reusable = old.TryGetValue(stamp.Path, out var cached) && cached.Stamp == stamp.Hash && cached.ProjectFile == owner.File && cached.Defines.SequenceEqual(defines);
                if (reusable && forceHash && cached!.Hash.Length > 0)
                {
                    await using var stream = File.OpenRead(stamp.Path);
                    reusable = Convert.ToHexString(await SHA256.HashDataAsync(stream, token)) == cached.Hash;
                }
                if (reusable) { files.Add(cached!); reused++; }
                else { files.Add(new(stamp.Path, "", stamp.Hash, owner.File is null ? "Source" : Path.GetFileNameWithoutExtension(owner.File), owner.File, defines, [], [])); invalidated = true; }
            }
            diagnostics.AddRange(files.SelectMany(file => file.Diagnostics));
            var states = projects.Select(project => new ProjectState(Path.GetFileNameWithoutExtension(project.File), project.File, files.Count(file => file.ProjectFile == project.File), null)).ToList();
            if (files.Any(file => file.ProjectFile is null)) states.Add(new("Source", null, files.Count(file => file.ProjectFile is null), null));
            bool unchanged = manifest is not null && SolutionInputTracker.SameContents(stamps, manifest.Files) && !invalidated;
            var state = unchanged ? manifest!.State : new SolutionState(diagnostics.Count == 0 ? "Ready" : "Partial", path, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, states, diagnostics);
            var indexed = files.ToArray();
            if (disk is not null)
            {
                if (!unchanged) await disk.SaveItemAsync(state.SnapshotId!, "source-files", indexed, token);
                if (!unchanged || !stamps.SequenceEqual(manifest!.Files)) await disk.SaveManifestAsync(new(state, [], stamps), token);
            }
            _state = state; _diskIndex = disk; _sourceFiles = indexed; _stamps = stamps;
            _indexedSymbols = indexed.SelectMany(file => file.Symbols).ToArray(); _roots.Clear(); _parsedDirty = false;
            _inputMonitor?.Dispose(); _inputMonitor = nextMonitor; _inputMonitor?.CompleteScan(observed); published = true;
            log?.Invoke($"Source catalog ready: {files.Count} files, parsed 0, reused {reused}; declarations are parsed on demand. No compiler or MSBuild.");
            return state;
        }
        finally { if (!published) nextMonitor?.Dispose(); }
    }
    public async Task EnsureFreshAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            if (_state.SolutionPath is not { } path) return;
            if (_inputMonitor is not null && !_inputMonitor.NeedsScan) return;
            long version = _inputMonitor?.Version ?? 0;
            var stamps = await SolutionInputTracker.CaptureAsync(_state, [], _stamps, token, hashContent: false);
            if (!SolutionInputTracker.SameContents(stamps, _stamps) || _inputMonitor?.HasChanges == true)
            {
                await LoadIndexAsync(path, _inputMonitor?.HasChanges == true, token);
                return; // The new monitor has its own version counter; never complete an old generation.
            }
            _inputMonitor?.CompleteScan(version);
        }
        finally { _gate.Release(); }
    }
    private void RequireIndex()
    { if (_state.SolutionPath is null) throw new InvalidOperationException("Load a source folder or solution first."); }
    public async Task<Page<SymbolInfo>> FindSymbolsAsync(string query, int offset, int limit, CancellationToken token)
    {
        RequireText(query, nameof(query)); ValidatePage(offset, limit); await _gate.WaitAsync(token);
        try
        {
            RequireIndex();
            var parts = query.Split('(')[0].TrimEnd('.').Split('.');
            var hints = parts.TakeLast(2).Select(value => value.Split('<')[0]).Where(value => value.Length >= 3).ToArray();
            var preferred = parts.Length > 1 ? _sourceFiles.Where(file => hints.Any(hint => Path.GetFileName(file.File).Split('.')[0].Equals(hint, StringComparison.OrdinalIgnoreCase))).ToArray() : [];
            var leaf = parts.Last();
            async Task ParseCandidates(IEnumerable<SourceFile> files)
            {
                foreach (var file in files)
                {
                    token.ThrowIfCancellationRequested();
                    if (file.Hash.Length == 0 && !(await File.ReadAllTextAsync(file.File, token)).Contains(leaf, StringComparison.OrdinalIgnoreCase)) continue;
                    await EnsureParsedAsync(file.File, token);
                }
            }
            await ParseCandidates(preferred);
            bool prioritized = preferred.Length > 0 && _indexedSymbols.Any(symbol => preferred.Any(file => file.File == symbol.File) && Matches(symbol, query));
            if (!prioritized) await ParseCandidates(_sourceFiles);
            var paths = prioritized ? preferred.Select(file => file.File).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
            var matches = _indexedSymbols.Where(symbol => (paths is null || paths.Contains(symbol.File!)) && Matches(symbol, query))
                .OrderByDescending(symbol => symbol.Name.Equals(query, StringComparison.OrdinalIgnoreCase) || symbol.FullName == query).ThenBy(symbol => symbol.FullName).ThenBy(symbol => symbol.File).ToArray();
            await PersistParsedAsync(token);
            var result = Paginate(matches, offset, limit);
            return prioritized ? result with { Warnings = result.Warnings.Concat(["Filename-prioritized declaration candidates; other files may contain additional matches. Use scoped text/reference search when exhaustive coverage is required."]).ToArray() } : result;
        }
        finally { _gate.Release(); }
    }
    private static bool Matches(SymbolInfo symbol, string query) => symbol.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || symbol.FullName.Contains(query, StringComparison.OrdinalIgnoreCase);
    private SymbolInfo Resolve(string query)
    {
        var exact = _indexedSymbols.FirstOrDefault(s => s.Id == query);
        if (exact is not null) return exact;
        var matches = _indexedSymbols.Where(s => s.FullName == query || s.FullName.Split('(')[0] == query).Take(2).ToArray();
        return matches.Length switch { 1 => matches[0], 0 => throw new ArgumentException("Declaration not found; use find_symbol first."),
            _ => throw new ArgumentException("Ambiguous declaration; pass the exact source ID returned by find_symbol.") };
    }
    public async Task<SymbolOverview> OverviewAsync(string symbolOrFile, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            SymbolInfo[] items;
            if (Path.IsPathFullyQualified(symbolOrFile) && File.Exists(symbolOrFile))
            {
                var file = ResolveSolutionPath(GetSolutionRoot(), symbolOrFile);
                if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Choose a C# source file.");
                if (!_sourceFiles.Any(source => source.File.Equals(file, StringComparison.OrdinalIgnoreCase)))
                    await LoadIndexAsync(_state.SolutionPath ?? ValidateSolutionPath(_options.SolutionPath), false, token);
                if (!_sourceFiles.Any(source => source.File.Equals(file, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("File is outside the source catalog.");
                items = (await EnsureParsedAsync(file, token)).Symbols;
                await PersistParsedAsync(token);
            }
            else { RequireIndex(); var owner = Resolve(symbolOrFile); items = _indexedSymbols.Where(s => s.Id == owner.Id || s.ContainingType == owner.FullName).ToArray(); }
            return new(items.Where(s => s.SymbolKind is "Class" or "Struct" or "Interface" or "Enum" or "Record").ToArray(),
                items.Where(s => s.SymbolKind == "Field").ToArray(), items.Where(s => s.SymbolKind == "Property").ToArray(),
                items.Where(s => s.SymbolKind == "Method").ToArray(), items.Where(s => s.SymbolKind == "Constructor").ToArray(),
                items.Where(s => s.SymbolKind == "Event").ToArray(), _state.SnapshotId, Warnings());
        }
        finally { _gate.Release(); }
    }
    public async Task<Page<ReferenceInfo>> ReferencesAsync(string symbol, int offset, int limit, CancellationToken token)
    {
        ValidatePage(offset, limit); await _gate.WaitAsync(token);
        try
        {
            var declaration = Resolve(symbol); var references = new List<ReferenceInfo>();
            foreach (var file in _sourceFiles)
            {
                if (!(await File.ReadAllTextAsync(file.File, token)).Contains(declaration.Name, StringComparison.Ordinal)) continue;
                var root = await RootAsync(file, token);
                foreach (var name in root.DescendantNodes().OfType<SimpleNameSyntax>().Where(name => name.Identifier.ValueText == declaration.Name))
                {
                    var span = name.GetLocation().GetLineSpan();
                    references.Add(new(Containing(file, name), file.Project, file.File, span.StartLinePosition.Line + 1,
                        span.StartLinePosition.Character + 1, "NameReferenceCandidate"));
                }
            }
            await PersistParsedAsync(token);
            return Paginate(references.ToArray(), offset, limit);
        }
        finally { _gate.Release(); }
    }
    public async Task<Page<ProcedureUsage>> ProcedureUsagesAsync(string procedure, int offset, int limit, CancellationToken token)
    {
        RequireText(procedure, nameof(procedure)); ValidatePage(offset, limit); await _gate.WaitAsync(token);
        try
        {
            RequireIndex(); var usages = new List<ProcedureUsage>();
            var requested = procedure.Replace("[", "").Replace("]", "");
            foreach (var file in _sourceFiles)
            {
                if (!(await File.ReadAllTextAsync(file.File, token)).Contains(requested.Split('.').Last(), StringComparison.OrdinalIgnoreCase)) continue;
                var root = await RootAsync(file, token);
                foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>().Where(node => node.IsKind(SyntaxKind.StringLiteralExpression)))
                {
                    var value = literal.Token.ValueText.Replace("[", "").Replace("]", "");
                    if (!value.Equals(requested, StringComparison.OrdinalIgnoreCase) &&
                        (requested.Contains('.') || !value.Split('.').Last().Equals(requested, StringComparison.OrdinalIgnoreCase))) continue;
                    usages.Add(new(literal.Token.ValueText, file.File, Line(literal), Containing(file, literal),
                        "ProcedureNameCandidate", false, "String literal in source; execution is not inferred."));
                }
            }
            await PersistParsedAsync(token);
            return Paginate(usages.ToArray(), offset, limit);
        }
        finally { _gate.Release(); }
    }
    private async Task<SourceFile> EnsureParsedAsync(string path, CancellationToken token, bool updateSymbols = true, int knownIndex = -1)
    {
        int index = knownIndex >= 0 ? knownIndex : Array.FindIndex(_sourceFiles, file => file.File.Equals(path, StringComparison.OrdinalIgnoreCase));
        var file = _sourceFiles[index];
        if (file.Hash.Length > 0) return file;
        var bytes = await File.ReadAllBytesAsync(path, token);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true);
        var root = CSharpSyntaxTree.ParseText(await reader.ReadToEndAsync(token), new CSharpParseOptions(preprocessorSymbols: file.Defines), path, cancellationToken: token).GetRoot(token);
        var diagnostics = root.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Take(5).Select(d => new AnalysisDiagnostic("SyntaxError", d.ToString(), file.Project)).ToArray();
        var parsed = file with { Hash = hash, Symbols = DescribeDeclarations(root, file.Project, hash).ToArray(), Diagnostics = diagnostics };
        SymbolDocumentIndexed?.Invoke(); token.ThrowIfCancellationRequested();
        _sourceFiles[index] = parsed;
        _parsedDirty = true;
        if (updateSymbols) _indexedSymbols = _sourceFiles.SelectMany(source => source.Symbols).ToArray();
        if (_roots.Count >= 24) _roots.Clear(); _roots[path] = root;
        return parsed;
    }
    private async Task PersistParsedAsync(CancellationToken token)
    {
        if (!_parsedDirty) return;
        var diagnostics = _sourceFiles.SelectMany(file => file.Diagnostics).ToArray();
        _state = _state with { Status = diagnostics.Length == 0 ? "Ready" : "Partial", Diagnostics = diagnostics };
        if (_diskIndex is not null)
        {
            await _diskIndex.SaveItemAsync(_state.SnapshotId!, "source-files", _sourceFiles, token);
            await _diskIndex.SaveManifestAsync(new(_state, [], _stamps), token);
        }
        _parsedDirty = false;
    }
    private async Task<SyntaxNode> RootAsync(SourceFile file, CancellationToken token)
    {
        file = await EnsureParsedAsync(file.File, token);
        if (_roots.TryGetValue(file.File, out var root)) return root;
        var bytes = await File.ReadAllBytesAsync(file.File, token);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != file.Hash) throw new ArgumentException("Source changed after indexing; refresh and retry with current source IDs.");
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true);
        root = CSharpSyntaxTree.ParseText(await reader.ReadToEndAsync(token), new CSharpParseOptions(preprocessorSymbols: file.Defines), file.File, cancellationToken: token).GetRoot(token);
        if (_roots.Count >= 24) _roots.Clear(); return _roots[file.File] = root;
    }
    private SymbolInfo? Containing(SourceFile file, SyntaxNode node)
    {
        file = _sourceFiles.First(current => current.File == file.File);
        var member = node.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().FirstOrDefault(item =>
            item is MethodDeclarationSyntax or ConstructorDeclarationSyntax or PropertyDeclarationSyntax);
        return member is null ? null : file.Symbols.FirstOrDefault(s => s.Line == Line(member));
    }
    private static IEnumerable<SymbolInfo> DescribeDeclarations(SyntaxNode root, string project, string hash)
    {
        foreach (var node in root.DescendantNodes().Where(node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax or
            MethodDeclarationSyntax or ConstructorDeclarationSyntax or PropertyDeclarationSyntax or EventDeclarationSyntax ||
            node is VariableDeclaratorSyntax { Parent.Parent: FieldDeclarationSyntax or EventFieldDeclarationSyntax }))
        {
            var type = node.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
            var containing = type is null ? null : TypeName(type);
            var (name, kind, parameters) = node switch
            {
                BaseTypeDeclarationSyntax t => (t.Identifier.ValueText, t.Kind().ToString().Replace("Declaration", ""), ""),
                DelegateDeclarationSyntax d => (d.Identifier.ValueText, "Delegate", ""),
                MethodDeclarationSyntax m => (m.Identifier.ValueText, "Method", "(" + string.Join(", ", m.ParameterList.Parameters.Select(p => p.Type?.ToString() ?? "?")) + ")"),
                ConstructorDeclarationSyntax c => (c.Identifier.ValueText, "Constructor", "(" + string.Join(", ", c.ParameterList.Parameters.Select(p => p.Type?.ToString() ?? "?")) + ")"),
                PropertyDeclarationSyntax p => (p.Identifier.ValueText, "Property", ""),
                EventDeclarationSyntax e => (e.Identifier.ValueText, "Event", ""),
                VariableDeclaratorSyntax v => (v.Identifier.ValueText, v.Parent?.Parent is EventFieldDeclarationSyntax ? "Event" : "Field", ""),
                _ => ("", "", "")
            };
            var fullName = node is BaseTypeDeclarationSyntax own ? TypeName(own) : Qualify(containing ?? NamespaceOf(node), name) + parameters;
            var span = node.GetLocation().GetLineSpan();
            var id = SourceId(span.Path, hash, node.SpanStart);
            var signature = node switch { MethodDeclarationSyntax m => m.ReturnType + " " + fullName,
                _ => fullName };
            yield return new(id, name, kind, NamespaceOf(node), fullName, signature, project, span.Path,
                span.StartLinePosition.Line + 1, containing, type?.BaseList?.ToString().Contains("Form") == true);
        }
    }
    private static string TypeName(BaseTypeDeclarationSyntax type) => Qualify(NamespaceOf(type),
        string.Join(".", type.Ancestors().OfType<BaseTypeDeclarationSyntax>().Reverse().Select(t => t.Identifier.ValueText).Append(type.Identifier.ValueText)));
    private static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
    private static string SourceId(string path, string hash, int position) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"source-1|{path}|{hash}|{position}")));
    private static bool IsInside(string path, string folder) => path.StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string[] ReadDefines(string path, List<AnalysisDiagnostic> diagnostics)
    {
        try { return XDocument.Load(path).Descendants().Where(node => node.Name.LocalName == "DefineConstants")
            .SelectMany(node => node.Value.Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries))
            .Where(value => Regex.IsMatch(value, @"^[A-Za-z_][A-Za-z0-9_]*$" )).Distinct().ToArray(); }
        catch (Exception error) when (error is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        { diagnostics.Add(new("Warning", "Project metadata unreadable; source search remains available.", Path.GetFileNameWithoutExtension(path))); return []; }
    }
    private static IEnumerable<string> ReadSolutionProjectPaths(string path)
    {
        var root = Directory.Exists(path) ? path : Path.GetDirectoryName(path)!;
        if (Directory.Exists(path)) return EnumerateSearchFiles(root, ".csproj");
        return Regex.Matches(File.ReadAllText(path), "\"([^\"]+\\.csproj)\"", RegexOptions.IgnoreCase)
            .Select(match => Path.GetFullPath(Path.Combine(root, match.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar))))
            .Where(project => IsInside(project, root));
    }
    private static string ValidateSolutionPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || (!Directory.Exists(path) &&
            (!File.Exists(path) || !path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))))
            throw new ArgumentException("Provide an existing absolute source folder or .sln path.");
        return Path.GetFullPath(path);
    }
    private IReadOnlyList<string> Warnings() => new[] { CandidateWarning }.Concat(_state.Diagnostics.Take(10).Select(d => d.Message)).Distinct().ToArray();
    private Page<T> Paginate<T>(IReadOnlyList<T> items, int offset, int limit) =>
        new(items.Skip(offset).Take(limit).ToArray(), items.Count, offset, limit, (long)offset + limit < items.Count, _state.SnapshotId, Warnings());
    private static void RequireText(string value, string name)
    { if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name} is required."); }
    private static void ValidatePage(int offset, int limit)
    { if (offset < 0 || limit is < 1 or > 1000) throw new ArgumentException("offset >= 0 and limit between 1 and 1000 required."); }
    public void Dispose() { _inputMonitor?.Dispose(); _gate.Dispose(); }
}
