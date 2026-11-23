using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SourceTrail.Core.Models;

namespace SourceTrail.Roslyn.Workspace;

public sealed partial class RoslynAnalyzer
{
    private static readonly Regex UiWords = new(@"[가-힣]+|[A-Z]?[a-z]+|[A-Z]+(?=[A-Z][a-z]|\b)|[0-9]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> UiQueryFillers = new(StringComparer.OrdinalIgnoreCase)
    {
        "화면", "화면에서", "버튼", "이벤트", "동작", "찾아줘", "확인해줘", "알려줘"
    };
    private static readonly string[][] UiActionAliases =
    [
        ["저장", "save"], ["수정", "edit", "modify", "update"], ["등록", "register", "add", "insert"],
        ["조회", "검색", "search", "find", "lookup", "query"], ["삭제", "delete", "remove"], ["출력", "print"]
    ];
    private UiIndex? _uiIndex;

    private sealed record UiControl(string Name, string Event, string Handler,
        UiActionEvidence Binding, UiActionEvidence? HandlerSite, UiActionEvidence? Caption,
        UiActionEvidence[] Calls);
    private sealed record UiScreen(string Type, string QualifiedType, UiActionEvidence? Title, UiControl[] Controls);
    private sealed record UiMenu(string TargetType, string OwnerNamespace, UiActionEvidence Caption,
        UiActionEvidence Creation);
    private sealed record UiIndex(string Fingerprint, UiScreen[] Screens, UiMenu[] Menus);
    private sealed record UiInput(string Designer, string? Source, string? Resources);
    private sealed record UiProperty(string? Type, string Control, string Property, UiActionEvidence Value);

    public async Task<UiActionSearchResult> FindUiActionsAsync(string query, int limit, CancellationToken cancellationToken)
    {
        RequireText(query, nameof(query));
        if (query.Length > 200) throw new ArgumentException("Use a task phrase of at most 200 characters.", nameof(query));
        if (limit is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(limit), "Use a limit between 1 and 20.");
        var terms = QueryWords(query).Where(word => !UiQueryFillers.Contains(word))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (terms.Length == 0) throw new ArgumentException("Provide a screen or action keyword.", nameof(query));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await EnsureUiIndexAsync(cancellationToken);
            var matchedTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var candidates = RankUiActions(_uiIndex!, terms, matchedTerms).ToArray();
            var unmatched = candidates.Length == 0 ? [] : terms.Where(term => !matchedTerms.Contains(term)).ToArray();
            return new("StaticCandidates", state, _uiIndex!.Screens.Length, candidates.Length,
                candidates.Length > limit, candidates.Take(limit).ToArray())
                { UnmatchedTerms = unmatched.Length == 0 ? null : unmatched };
        }
        finally { _gate.Release(); }
    }

    private async Task<string> EnsureUiIndexAsync(CancellationToken token)
    {
        var solution = ValidateSolutionPath(_state.SolutionPath ?? _options.SolutionPath);
        var inputs = CollectUiInputs(solution, GetSolutionRoot());
        var fingerprint = UiFingerprint(solution, inputs);
        if (_uiIndex?.Fingerprint == fingerprint) return "Memory";
        var disk = string.IsNullOrWhiteSpace(_options.CodeCacheDirectory) ? null
            : new DiskCodeIndex(_options.CodeCacheDirectory, solution, _options, log, requireCacheWrites);
        _uiIndex = disk is null ? null : await disk.ReadItemAsync<UiIndex>(fingerprint, "ui-actions", token);
        if (_uiIndex is not null) return "Disk";
        var built = await BuildUiIndexAsync(fingerprint, inputs, token);
        if (UiFingerprint(solution, inputs) != fingerprint)
            throw new IOException("A UI source changed during indexing. Retry the query.");
        if (disk is not null) await disk.SaveItemAsync(fingerprint, "ui-actions", built, token);
        _uiIndex = built;
        return "Built";
    }

    private static UiInput[] CollectUiInputs(string solution, string root)
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in ReadSolutionProjectPaths(solution))
        {
            if (!project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || !File.Exists(project)) continue;
            var relative = Path.GetRelativePath(root, project);
            if (Path.IsPathRooted(relative) || relative == ".." ||
                relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            try { folders.Add(Path.GetDirectoryName(ResolveSolutionPath(root, project))!); }
            catch (ArgumentException) { } // A project behind a reparse point is outside the searchable scope.
        }
        if (folders.Count == 0) folders.Add(root);
        var roots = folders.Where(folder => !folders.Any(other => !folder.Equals(other, StringComparison.OrdinalIgnoreCase) &&
            folder.StartsWith(Path.TrimEndingDirectorySeparator(other) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)));
        return roots.SelectMany(folder => EnumerateSearchFiles(folder, ".cs"))
            .Where(file => file.EndsWith(DesignerSuffix, StringComparison.OrdinalIgnoreCase) && IsRegularFile(file))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).Select(file =>
            {
                var stem = file[..^DesignerSuffix.Length];
                var source = stem + ".cs";
                var resources = stem + ".resx";
                return new UiInput(file, IsRegularFile(source) ? source : null, IsRegularFile(resources) ? resources : null);
            }).ToArray();
    }

    private static bool IsRegularFile(string path) =>
        File.Exists(path) && !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    private static string UiFingerprint(string solution, IEnumerable<UiInput> inputs)
    {
        var stamps = new StringBuilder(solution).Append('\n');
        foreach (var path in inputs.SelectMany(input => new[] { input.Designer, input.Source, input.Resources })
            .Prepend(File.Exists(solution) ? solution : null).OfType<string>())
        {
            var file = new FileInfo(path);
            stamps.Append(path).Append('\0').Append(file.Length).Append('\0')
                .Append(file.LastWriteTimeUtc.Ticks).Append('\0').Append(file.CreationTimeUtc.Ticks).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stamps.ToString())));
    }

    private static async Task<UiIndex> BuildUiIndexAsync(string fingerprint, IEnumerable<UiInput> inputs,
        CancellationToken token)
    {
        var screens = new List<UiScreen>();
        var menus = new List<UiMenu>();
        foreach (var input in inputs)
        {
            token.ThrowIfCancellationRequested();
            var designer = await ParseSourceAsync(input.Designer, token);
            var bindings = ReadBindings(designer, null).ToArray();
            if (bindings.Length == 0) continue;
            var source = input.Source is null ? null : await ParseSourceAsync(input.Source, token);
            var methods = designer.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Concat(source?.DescendantNodes().OfType<MethodDeclarationSyntax>() ?? [])
                .ToLookup(method => (Owner(method), method.Identifier.ValueText));
            var properties = DesignerProperties(designer).ToLookup(property => (property.Type, property.Control));
            var resources = input.Resources is not null &&
                designer.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(call =>
                    InvocationName(call) == "ApplyResources") ? ResourceProperties(input.Resources) : [];

            foreach (var group in bindings.Where(binding => binding.Type is not null).GroupBy(binding => binding.Type!))
            {
                var type = designer.DescendantNodes().OfType<TypeDeclarationSyntax>()
                    .First(declaration => declaration.Identifier.ValueText == group.Key);
                var ownerNamespace = NamespaceOf(type);
                var title = Caption(properties[(group.Key, "$this")], resources, "$this");
                var controls = new List<UiControl>();
                foreach (var binding in group)
                {
                    var handlers = methods[(group.Key, binding.Handler)].Take(2).ToArray();
                    var handler = handlers.Length == 1 ? handlers[0] : null;
                    var caption = Caption(properties[(group.Key, binding.Control)], resources, binding.Control);
                    var calls = handler is null ? [] : handler.DescendantNodes().OfType<InvocationExpressionSyntax>()
                        .Select(call => (Name: InvocationName(call), Call: call))
                        .Where(item => item.Name is not null)
                        .DistinctBy(item => item.Name)
                        .Select(item => Evidence("HandlerCall", item.Name!, item.Call.GetLocation())).ToArray();
                    controls.Add(new(binding.Control, binding.Event, binding.Handler,
                        new("EventBinding", binding.Control + "." + binding.Event + " → " + binding.Handler,
                            input.Designer, binding.Line),
                        handler is null ? null : Evidence("Handler", binding.Handler, handler.Identifier.GetLocation()),
                        caption, calls));
                    if (handler is not null && caption is not null)
                        AddMenuCandidates(menus, caption, handler, ownerNamespace);
                }
                screens.Add(new(group.Key, Qualify(ownerNamespace, group.Key), title, controls.ToArray()));
            }

            foreach (var creation in source?.DescendantNodes().OfType<ObjectCreationExpressionSyntax>() ?? [])
            {
                if (creation.ArgumentList?.Arguments.FirstOrDefault()?.Expression is not { } callback ||
                    SimpleName(callback) is not { } name || creation.Initializer is null) continue;
                var caption = creation.Initializer.Expressions.OfType<AssignmentExpressionSyntax>()
                    .FirstOrDefault(assignment => SimpleName(assignment.Left) is "Caption" or "Text" &&
                        assignment.Right is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression));
                if (caption?.Right is not LiteralExpressionSyntax label) continue;
                var handlers = methods[(Owner(creation), name)].Take(2).ToArray();
                if (handlers.Length == 1)
                    AddMenuCandidates(menus, Evidence("MenuCaption", label.Token.ValueText, label.GetLocation()),
                        handlers[0], NamespaceOf(creation));
            }
        }
        return new(fingerprint, screens.ToArray(), menus.ToArray());
    }

    private static IEnumerable<UiProperty> DesignerProperties(SyntaxNode designer)
    {
        foreach (var assignment in designer.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (assignment.FirstAncestorOrSelf<MethodDeclarationSyntax>()?.Identifier.ValueText != "InitializeComponent" ||
                assignment.Right is not LiteralExpressionSyntax literal || !literal.IsKind(SyntaxKind.StringLiteralExpression))
                continue;
            var property = SimpleName(assignment.Left);
            if (property is not ("Text" or "Caption" or "Title") || string.IsNullOrWhiteSpace(literal.Token.ValueText))
                continue;
            string? control = assignment.Left is MemberAccessExpressionSyntax member &&
                member.Expression is not ThisExpressionSyntax ? SimpleName(member.Expression) : "$this";
            if (control is not null)
                yield return new(Owner(assignment), control, property,
                    Evidence(control == "$this" ? "FormTitle" : "ControlCaption", literal.Token.ValueText, literal.GetLocation()));
        }
    }

    private static Dictionary<string, UiActionEvidence> ResourceProperties(string path)
    {
        var values = new Dictionary<string, UiActionEvidence>(StringComparer.Ordinal);
        try
        {
            using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "data" ||
                    reader.GetAttribute("name") is not { } name ||
                    !new[] { ".Text", ".Caption", ".Title" }.Any(suffix => name.EndsWith(suffix, StringComparison.Ordinal)))
                    continue;
                int line = ((IXmlLineInfo)reader).LineNumber;
                using var data = reader.ReadSubtree();
                if (!data.ReadToFollowing("value")) continue;
                var text = data.ReadElementContentAsString();
                if (!string.IsNullOrWhiteSpace(text))
                    values[name] = new(name.StartsWith("$this.", StringComparison.Ordinal) ? "FormTitle" : "ControlCaption", text, path, line);
            }
        }
        catch (XmlException) { } // A malformed resource does not invalidate Designer evidence.
        return values;
    }

    private static UiActionEvidence? Caption(IEnumerable<UiProperty> properties,
        Dictionary<string, UiActionEvidence> resources, string control)
    {
        var entries = properties.ToArray();
        foreach (var name in new[] { "Caption", "Text", "Title" })
        {
            if (entries.FirstOrDefault(entry => entry.Property == name) is { } entry) return entry.Value;
            if (resources.TryGetValue(control + "." + name, out var localized)) return localized;
        }
        return null;
    }

    private static void AddMenuCandidates(List<UiMenu> menus, UiActionEvidence caption,
        MethodDeclarationSyntax handler, string ownerNamespace)
    {
        foreach (var creation in handler.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            menus.Add(new(creation.Type.ToString().Replace("global::", "", StringComparison.Ordinal), ownerNamespace,
                caption with { Kind = "MenuCaption" }, Evidence("ScreenCreation", creation.Type.ToString(), creation.GetLocation())));
    }

    private static IEnumerable<UiActionCandidate> RankUiActions(UiIndex index, string[] terms, HashSet<string> matchedTerms)
    {
        var byQualified = index.Screens.ToLookup(screen => screen.QualifiedType, StringComparer.Ordinal);
        var bySimple = index.Screens.ToLookup(screen => screen.Type, StringComparer.Ordinal);
        var menus = index.Menus.Select(menu =>
        {
            var exact = byQualified[menu.TargetType].Concat(byQualified[Qualify(menu.OwnerNamespace, menu.TargetType)])
                .Distinct().Take(2).ToArray();
            var simple = bySimple[menu.TargetType.Split('.').Last()].Take(2).ToArray();
            return (Menu: menu, Screen: exact.Length == 1 ? exact[0] : exact.Length == 0 && simple.Length == 1 ? simple[0] : null);
        }).Where(item => item.Screen is not null).ToLookup(item => item.Screen!.QualifiedType, item => item.Menu);

        var screens = index.Screens.Select(screen =>
        {
            var screenMenus = menus[screen.QualifiedType].ToArray();
            var screenTerms = terms.Select(term =>
            {
                var menu = screenMenus.FirstOrDefault(item => Matches(item.Caption.Text, term, aliases: false));
                var partialMenu = menu is null ? screenMenus.FirstOrDefault(item => ContainsKoreanTerm(item.Caption.Text, term)) : null;
                int score = menu is not null ? 20 : partialMenu is not null ? 14
                    : Matches(screen.Title?.Text, term, aliases: false) ? 11
                    : ContainsKoreanTerm(screen.Title?.Text, term) ? 8
                    : Matches(screen.Type, term, aliases: false) ? 7 : 0;
                return (Term: term, Menu: menu ?? partialMenu, Score: score, Partial: score is 14 or 8);
            }).ToArray();
            return (Screen: screen, Terms: screenTerms);
        }).ToArray();
        // A word that names many screens ("검사" in a checkup system) says little about which screen is meant.
        // With three or more words, candidates can match different word subsets, so scale a common word's
        // screen score down by how many screens it matches; a matched word keeps at least 1 point.
        // One- and two-word queries keep their scores: every candidate there matches the same words.
        var screenFrequency = terms.ToDictionary(term => term,
            term => screens.Count(item => item.Terms.Any(match => match.Term == term && match.Score > 0)), StringComparer.OrdinalIgnoreCase);
        const int CommonScreenWord = 12;
        // Exact screen-word evidence always ranks above in-compound (partial) evidence, so the partial
        // match added for "결과입력" never pushes a whole-word "접수" screen down.
        var ranked = new List<(bool ExactScreen, int Score, UiActionCandidate Candidate)>();
        // In a Korean noun phrase the last noun is the head ("검사 결과" is a kind of 결과). With three or more
        // words, a screen word written before the head counts half, so "결과입력" outranks "검사옵션".
        var nouns = terms.Where(term => !UiActionAliases.Any(group => group.Contains(term, StringComparer.OrdinalIgnoreCase))).ToArray();
        var head = nouns.Length > 1 ? nouns[^1] : null;
        foreach (var (screen, unscaled) in screens)
        {
            int Scale(string term, int score)
            {
                double rarity = screenFrequency[term] <= CommonScreenWord ? 1.0 : (double)CommonScreenWord / screenFrequency[term];
                double role = head is null || term == head || !nouns.Contains(term) ? 1.0 : 0.5;
                return Math.Max(1, (int)Math.Round(score * rarity * role));
            }
            var screenTerms = unscaled.Select(item => terms.Length < 3 || item.Score == 0 ? item
                : item with { Score = Scale(item.Term, item.Score) }).ToArray();
            if (terms.Length > 1 && screenTerms.All(term => term.Score == 0)) continue;
            foreach (var control in screen.Controls)
            {
                int score = 0, unmatched = 0;
                bool screenMatched = false, actionMatched = false, exactScreen = false;
                UiMenu? matchedMenu = null;
                UiActionEvidence? matchedCall = null;
                var matchedHere = new List<string>();
                foreach (var (term, menu, screenScore, partial) in screenTerms)
                {
                    var call = control.Calls.FirstOrDefault(item => Matches(item.Text, term, aliases: true));
                    int actionScore = Matches(control.Caption?.Text, term, aliases: false) ? 15
                        : Matches(control.Name, term, aliases: true) ? 12
                        : ContainsKoreanTerm(control.Caption?.Text, term) ? 10
                        : Matches(control.Handler, term, aliases: true) ? 9 : call is not null ? 6 : 0;
                    // A task phrase can carry words that no screen uses (e.g. "검사" in "검사 결과 저장").
                    // Rank on the words that matched instead of discarding the candidate.
                    // The requested action ("저장", "save", ...) must be the control's action, not just a word nearby.
                    if (actionScore == 0 && UiActionAliases.Any(group => group.Contains(term, StringComparer.OrdinalIgnoreCase)))
                    { score = 0; break; }
                    if (screenScore == 0 && actionScore == 0) { unmatched++; continue; }
                    matchedHere.Add(term);
                    screenMatched |= screenScore > 0;
                    exactScreen |= screenScore > 0 && !partial;
                    actionMatched |= actionScore > 0;
                    matchedMenu ??= menu;
                    matchedCall ??= call;
                    score += Math.Max(screenScore, actionScore);
                }
                if (score == 0 || terms.Length > 1 && !(screenMatched && actionMatched)) continue;
                score -= 9 * unmatched;
                matchedTerms.UnionWith(matchedHere);
                if (control.Event is "Click" or "ItemClick" or "ButtonClick") score += 2;
                var operationWords = Words(control.Name).Concat(Words(control.Handler))
                    .Where(word => UiActionAliases.Any(group => group.Contains(word))).Distinct().ToArray();
                // A screen can have many "edit" controls. A save-named control that calls save code
                // provides stronger evidence for the screen's edit/save action.
                if (terms.Contains("수정") && Matches(control.Caption?.Text, "수정", aliases: false) &&
                    operationWords.Contains("save") &&
                    control.Calls.FirstOrDefault(call => Matches(call.Text, "저장", aliases: true)) is { } saveCall)
                {
                    score += 5;
                    matchedCall = saveCall;
                }
                matchedCall ??= control.Calls.FirstOrDefault(call => operationWords.Any(word =>
                    Matches(call.Text, word, aliases: true)));
                var evidence = new[] { matchedMenu?.Caption, matchedMenu?.Creation, screen.Title, control.Caption,
                    control.Binding, control.HandlerSite, matchedCall }.OfType<UiActionEvidence>().ToArray();
                ranked.Add((exactScreen, score, new(screen.QualifiedType, screen.Title?.Text, control.Name, control.Caption?.Text,
                    control.Event, control.Handler, evidence)));
            }
        }
        return ranked.OrderByDescending(item => item.ExactScreen).ThenByDescending(item => item.Score)
            .ThenBy(item => item.Candidate.Screen, StringComparer.Ordinal)
            .ThenBy(item => item.Candidate.Control, StringComparer.Ordinal).Select(item => item.Candidate);
    }

    private static bool Matches(string? text, string term, bool aliases)
    {
        if (text is null) return false;
        var words = Words(text).ToArray();
        if (words.Contains(term, StringComparer.OrdinalIgnoreCase)) return true;
        return aliases && UiActionAliases.Any(group => group.Contains(term, StringComparer.OrdinalIgnoreCase) &&
            group.Intersect(words, StringComparer.OrdinalIgnoreCase).Any());
    }

    // Korean captions are often written as one compound ("결과입력", "검사결과저장"). Accept a Korean
    // query word of two or more syllables inside such a compound, ranked below a whole-word match.
    private static bool ContainsKoreanTerm(string? text, string term) =>
        text is not null && term.Length >= 2 && term.All(character => character is >= '가' and <= '힣') &&
        Words(text).Any(word => word.Length > term.Length && word.Contains(term, StringComparison.Ordinal));

    private static IEnumerable<string> Words(string text) =>
        UiWords.Matches(text.Normalize(NormalizationForm.FormKC)).Select(match => match.Value.ToLowerInvariant());

    // Normalize only query-side Korean screen+action compounds, never identifiers in the index.
    private static IEnumerable<string> QueryWords(string query)
    {
        var actions = UiActionAliases.SelectMany(group => group)
            .Where(word => word.All(character => character is >= '가' and <= '힣'))
            .OrderByDescending(word => word.Length).ToArray();
        foreach (var word in Words(query))
        {
            var suffix = word.All(character => character is >= '가' and <= '힣')
                ? actions.FirstOrDefault(action => word.Length >= action.Length + 2 &&
                    word.EndsWith(action, StringComparison.Ordinal)) : null;
            if (suffix is null) yield return word;
            else
            {
                yield return word[..^suffix.Length];
                yield return suffix;
            }
        }
    }
    private static string? Owner(SyntaxNode node) => node.FirstAncestorOrSelf<TypeDeclarationSyntax>()?.Identifier.ValueText;
    private static string NamespaceOf(SyntaxNode node) => string.Join(".", node.Ancestors()
        .OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(parent => parent.Name.ToString()));
    private static string Qualify(string prefix, string name) => prefix.Length == 0 ? name : prefix + "." + name;
    private static string? InvocationName(InvocationExpressionSyntax call) => SimpleName(call.Expression);
    private static string? SimpleName(ExpressionSyntax expression) => expression switch
    {
        SimpleNameSyntax name => name.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        _ => null,
    };

    private static UiActionEvidence Evidence(string kind, string text, Location location)
    {
        var span = location.GetLineSpan();
        return new(kind, text, span.Path, span.StartLinePosition.Line + 1);
    }
}
