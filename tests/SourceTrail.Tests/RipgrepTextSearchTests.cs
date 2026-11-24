using System.Text;
using SourceTrail.Core.Models;
using SourceTrail.Roslyn.Workspace;
using Xunit;

namespace SourceTrail.Tests;

public sealed class RipgrepFactAttribute : FactAttribute
{
    public RipgrepFactAttribute(bool requireLinks = false)
    {
        if (RipgrepTextSearch.ResolveExecutable(null) is null)
        {
            Skip = "Native integration requires rg/rg.exe on PATH.";
            return;
        }
        if (!requireLinks) return;
        var root = Path.Combine(Path.GetTempPath(), "sourcetrail-link-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var target = Path.Combine(root, "target"); File.WriteAllText(target, "probe");
            File.CreateSymbolicLink(Path.Combine(root, "file-link"), target);
            var folder = Path.Combine(root, "folder"); Directory.CreateDirectory(folder);
            Directory.CreateSymbolicLink(Path.Combine(root, "folder-link"), folder);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Skip = "The current filesystem does not allow symbolic links.";
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}

public sealed class RipgrepTextSearchTests
{
    [Fact]
    public void ExecutableResolutionUsesThePlatformExecutableAndRespectsExplicitPaths()
    {
        using var fixture = new SearchFixture();
        var native = fixture.Write(OperatingSystem.IsWindows() ? "rg.exe" : "rg", "placeholder");
        if (OperatingSystem.IsWindows()) fixture.Write("rg", "not a Windows executable");
        Assert.Equal(native, RipgrepTextSearch.ResolveExecutable(null, fixture.Root));
        Assert.Null(RipgrepTextSearch.ResolveExecutable(Path.Combine(fixture.Root, "missing.exe"), fixture.Root));
    }

    [RipgrepFact]
    public async Task NativeSearchPreservesDecodingScopeColumnsPreviewsAndPaging()
    {
        var executable = RequireRipgrep();
        using var fixture = new SearchFixture();
        const string query = "접수 저장.[x]*$";
        var line = "  😀 " + query + " " + new string('x', 260) + " " + query;
        fixture.Write("one.cs", line + "\r\n" + query);
        fixture.Write(".hidden.cs", line);
        fixture.Write(".gitignore", "*.cs");
        fixture.Write("nested/Form.Designer.CS", line);
        fixture.Write("utf8-bom.cs", line, new UTF8Encoding(true));
        fixture.Write("utf16-le.cs", line, Encoding.Unicode);
        fixture.Write("utf16-be.cs", line, Encoding.BigEndianUnicode);
        fixture.Write("utf32-le.cs", line, new UTF32Encoding(false, true));
        fixture.Write("utf32-be.cs", line, new UTF32Encoding(true, true));
        fixture.Write("settings.config", line);
        fixture.Write("query.sql", line);
        fixture.Write("ignored.txt", line);
        foreach (var excluded in new[] { "bin", "OBJ", ".git", ".svn", ".vs", "node_modules" })
            fixture.Write(excluded + "/excluded.cs", line);
        var messages = new List<string>();
        using var managed = fixture.Analyzer(false);
        using var native = fixture.Analyzer(true, executable, messages.Add);
        foreach (var extension in new string?[] { null, ".cs", ".Designer.cs" })
        {
            var expected = await managed.SearchTextAsync(query, 0, 100, default, extension: extension);
            var actual = await native.SearchTextAsync(query, 0, 100, default, extension: extension);
            Assert.NotEmpty(expected.Items);
            Assert.Equal(expected.Total, actual.Total);
            Assert.Equal(expected.Items, actual.Items);
            Assert.DoesNotContain(actual.Items, match => match.File.EndsWith("excluded.cs"));
        }
        var all = await managed.SearchTextAsync(query, 0, 100, default, extension: ".cs");
        var page = await native.SearchTextAsync(query, 1, 2, default, extension: ".cs");
        Assert.Equal(all.Total, page.Total);
        Assert.True(page.Truncated);
        Assert.Equal(all.Items.Skip(1).Take(2), page.Items);
        var empty = await native.SearchTextAsync("absent-value-917032", 0, 5, default);
        Assert.Empty(empty.Items); Assert.Equal(0, empty.Total);
        Assert.Contains(messages, message => message.StartsWith("Text search engine: ripgrep lines"));
        Assert.DoesNotContain(messages, message => message.Contains(query));
    }

    [RipgrepFact]
    public async Task NativeSearchPreservesAdditionalRootsAndScopeRestrictions()
    {
        var executable = RequireRipgrep();
        using var fixture = new SearchFixture();
        using var extra = new SearchFixture();
        fixture.Write("main.cs", "needle");
        fixture.Write("bin/explicit.cs", "needle");
        extra.Write("extra.cs", "needle");
        var options = new AnalysisOptions { SolutionPath = fixture.Root, RipgrepPath = executable,
            AdditionalSearchRoots = [extra.Root, fixture.Root] };
        using var native = new RoslynAnalyzer(options);
        using var managed = new RoslynAnalyzer(new AnalysisOptions { SolutionPath = fixture.Root, UseRipgrep = false,
            AdditionalSearchRoots = options.AdditionalSearchRoots });
        var expected = await managed.SearchTextAsync("needle", 0, 100, default);
        var actual = await native.SearchTextAsync("needle", 0, 100, default);
        Assert.Equal(2, actual.Total); Assert.Equal(expected.Items, actual.Items);
        await Assert.ThrowsAsync<ArgumentException>(() => native.SearchTextAsync("needle", 0, 100, default, "bin"));
        await Assert.ThrowsAsync<ArgumentException>(() => managed.SearchTextAsync("needle", 0, 100, default, "bin"));
    }

    [RipgrepFact(requireLinks: true)]
    public async Task NativeSearchIncludesFileLinksWithoutFollowingDirectoryLinks()
    {
        var executable = RequireRipgrep();
        using var fixture = new SearchFixture();
        using var target = new SearchFixture();
        var source = target.Write("target.cs", "needle");
        File.CreateSymbolicLink(Path.Combine(fixture.Root, "linked.cs"), source);
        Directory.CreateSymbolicLink(Path.Combine(fixture.Root, "linked-directory"), target.Root);
        using var managed = fixture.Analyzer(false);
        using var native = fixture.Analyzer(true, executable);
        var expected = await managed.SearchTextAsync("needle", 0, 100, default);
        var actual = await native.SearchTextAsync("needle", 0, 100, default);
        Assert.Single(actual.Items);
        Assert.EndsWith("linked.cs", actual.Items[0].File);
        Assert.Equal(expected.Items, actual.Items);
    }

    [RipgrepFact]
    public async Task NativeCandidatesPreserveOrdinalCaseRulesAndLiteralArguments()
    {
        var executable = RequireRipgrep();
        using var fixture = new SearchFixture();
        fixture.Write("case.cs", "k K K\ns S ſ\ni I ı İ\né É\nΣ σ ς\n𐐀 𐐨\n--flag \"quoted\" $value C:\\x\\y [a] a|b\ninvalid: \uFFFD");
        using var managed = fixture.Analyzer(false);
        using var native = fixture.Analyzer(true, executable);
        foreach (var query in new[] { "k", "s", "i", "ı", "İ", "é", "σ", "𐐀", "--flag", "\"quoted\"", "$value",
            @"C:\x\y", "[a]", "a|b", "\uFFFD", "i\nI" })
        {
            var expected = await managed.SearchTextAsync(query, 0, 100, default);
            var actual = await native.SearchTextAsync(query, 0, 100, default);
            Assert.Equal(expected.Items, actual.Items);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrUnlaunchableRipgrepFallsBackWithoutDroppingMatches(bool createInvalidExecutable)
    {
        using var fixture = new SearchFixture();
        fixture.Write("one.cs", "needle\nneedle");
        var executable = Path.Combine(fixture.Root, "invalid.exe");
        if (createInvalidExecutable) File.WriteAllText(executable, "not executable");
        var messages = new List<string>();
        using var code = fixture.Analyzer(true, executable, messages.Add);
        var actual = await code.SearchTextAsync("needle", 1, 1, default);
        Assert.Equal(2, actual.Total); Assert.Single(actual.Items); Assert.Equal(2, actual.Items[0].Line);
        Assert.Contains(messages, message => message.StartsWith("Text search engine: managed"));
    }

    [Fact]
    public async Task CancellationDoesNotLeaveTheSearchGateHeld()
    {
        using var fixture = new SearchFixture();
        fixture.Write("one.cs", "needle");
        using var code = fixture.Analyzer(false);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => code.SearchTextAsync("needle", 0, 10, cancellation.Token));
        Assert.Single((await code.SearchTextAsync("needle", 0, 10, default)).Items);
    }

    private static string RequireRipgrep() => RipgrepTextSearch.ResolveExecutable(null)
        ?? throw new InvalidOperationException("rg disappeared after native test discovery.");

    private sealed class SearchFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "sourcetrail-rg-" + Guid.NewGuid().ToString("N"));
        public SearchFixture() => Directory.CreateDirectory(Root);
        public string Write(string name, string text, Encoding? encoding = null)
        {
            var file = Path.Combine(Root, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text, encoding ?? new UTF8Encoding(false));
            return file;
        }
        public RoslynAnalyzer Analyzer(bool useRipgrep, string? executable = null, Action<string>? log = null) =>
            new(new AnalysisOptions { SolutionPath = Root, UseRipgrep = useRipgrep, RipgrepPath = executable ?? "" }, log);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
