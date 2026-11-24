using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SourceTrail.Core.Models;
using SourceTrail.Mcp.Tools;
using SourceTrail.Roslyn.Workspace;
using SourceTrail.SqlServer.Metadata;
using Xunit;
namespace SourceTrail.Tests;

// 3.4.0: ripgrep line results for search_text, SQL callers/references, derived types and memberQuery.
public sealed class V340Tests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "v340-" + Guid.NewGuid().ToString("N"));
    private string Code => Path.Combine(root, "code");
    private string Scripts => Path.Combine(root, "sql");
    public V340Tests() { Directory.CreateDirectory(Code); Directory.CreateDirectory(Scripts); }
    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }
    private string WriteCode(string name, string text, Encoding? encoding = null)
    {
        var file = Path.Combine(Code, name.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text, encoding ?? new UTF8Encoding(false));
        return file;
    }
    private void WriteSql(string name, string sql) => File.WriteAllText(Path.Combine(Scripts, name), sql);
    private DatabaseAnalyzer Database() => new(new DatabaseOptions
        { Mode = "SqlFiles", SqlFolder = Scripts, CacheDirectory = Path.Combine(root, "cache"), WatchFiles = false });

    // ---- 1. search_text takes lines from ripgrep ----

    [RipgrepFact]
    public async Task RipgrepLinesMatchManagedLinesColumnsAndPreviewsAndHandOffFilesItCannotDecode()
    {
        var executable = RipgrepTextSearch.ResolveExecutable(null)!;
        const string query = "저장 usp_Save";
        var line = "\t  가나다 😀 " + query + " " + new string('x', 300) + " " + query;
        WriteCode("plain.cs", "first\r\n" + line + "\r\nlast " + query);
        WriteCode("bom.cs", line + "\r\n" + query, new UTF8Encoding(true));
        WriteCode("utf16.cs", "a\r\n" + line, Encoding.Unicode);
        WriteCode("utf16be-lone-cr.cs", "a\r" + line, Encoding.BigEndianUnicode);
        WriteCode("utf32.cs", line, new UTF32Encoding(true, true));
        WriteCode("utf32le.cs", line, new UTF32Encoding(false, true));
        WriteCode("lone-cr.cs", "one\rtwo\r" + query + "\rfour");
        WriteCode("invalid.cs", "x\n");
        File.WriteAllBytes(Path.Combine(Code, "invalid.cs"), Encoding.UTF8.GetBytes("\u0001 ").Concat(new byte[] { 0xC0, 0xFF, 0x0A }).Concat(Encoding.UTF8.GetBytes("ok " + query)).ToArray());
        WriteCode("sub/settings.config", "<add value=\"" + query + "\" />");
        var messages = new List<string>();
        using var native = new RoslynAnalyzer(new AnalysisOptions { SolutionPath = Code, RipgrepPath = executable }, messages.Add);
        using var managed = new RoslynAnalyzer(new AnalysisOptions { SolutionPath = Code, UseRipgrep = false });
        var expected = await managed.SearchTextAsync(query, 0, 100, default);
        var actual = await native.SearchTextAsync(query, 0, 100, default);
        Assert.True(expected.Total >= 10);
        Assert.Equal(expected.Items, actual.Items);
        Assert.Equal(expected.Total, actual.Total);
        Assert.Contains(messages, message => message.StartsWith("Text search engine: ripgrep lines"));
        Assert.DoesNotContain(messages, message => message.StartsWith("Text search engine: ripgrep candidates"));
        var bom = Assert.Single(actual.Items, item => item.File.EndsWith("bom.cs") && item.Line == 1);
        Assert.Equal(line.IndexOf(query, StringComparison.Ordinal) + 1, bom.Column); // BOM is not counted
        Assert.Equal(240, actual.Items.First(item => item.File.EndsWith("plain.cs")).Preview.Length);
        Assert.Equal(3, Assert.Single(actual.Items, item => Path.GetFileName(item.File) == "lone-cr.cs").Line); // lone CR is a line break for the managed reader
        // Lines come straight from rg; files rg cannot decode as UTF-8 text are the only ones handed to the managed reader.
        var lines = await RipgrepTextSearch.FindLinesAsync([Code], null, query, executable, null, default);
        Assert.NotNull(lines);
        Assert.Equal(["lone-cr.cs", "utf16be-lone-cr.cs", "utf32.cs", "utf32le.cs"], lines!.Managed.Select(file => Path.GetFileName(file)!).Order().ToArray());
        Assert.DoesNotContain(lines.Managed, file => file.EndsWith("plain.cs") || file.EndsWith("invalid.cs") || file.EndsWith("settings.config"));
        Assert.Contains(lines.Hits.Keys, file => file.EndsWith("plain.cs"));
        Assert.Contains(lines.Hits.Keys, file => file.EndsWith("invalid.cs"));
        Assert.Contains(lines.Hits.Keys, file => file.EndsWith("utf16.cs")); // decoded by ripgrep itself
        Assert.Contains(lines.Hits.Keys, file => file.EndsWith("bom.cs"));
        Assert.Contains(lines.Hits.Keys, file => file.EndsWith("settings.config"));
    }

    [RipgrepFact]
    public async Task CaseFoldingOnlyMatchesAndUnsupportedQueriesKeepTheManagedRules()
    {
        var executable = RipgrepTextSearch.ResolveExecutable(null)!;
        WriteCode("fold.cs", "ſ\nK\nı\nİ\nÉ\nσ\nς\nplain s\nplain k\nplain i\n");
        using var managed = new RoslynAnalyzer(new AnalysisOptions { SolutionPath = Code, UseRipgrep = false });
        var messages = new List<string>();
        using var native = new RoslynAnalyzer(new AnalysisOptions { SolutionPath = Code, RipgrepPath = executable }, messages.Add);
        foreach (var query in new[] { "s", "k", "i", "é", "σ", "ς", "İ", "ı" })
        {
            var expected = await managed.SearchTextAsync(query, 0, 100, default);
            var actual = await native.SearchTextAsync(query, 0, 100, default);
            Assert.Equal(expected.Items, actual.Items);
        }
        // Cased non-ASCII text is not delegated to ripgrep's Unicode folding; file candidates are still used.
        Assert.Contains(messages, message => message.StartsWith("Text search engine: ripgrep candidates"));
        Assert.False(RipgrepTextSearch.SupportsLineResults("é"));
        Assert.False(RipgrepTextSearch.SupportsLineResults("a\nb"));
        Assert.False(RipgrepTextSearch.SupportsLineResults("�"));
        Assert.True(RipgrepTextSearch.SupportsLineResults("접수 저장 usp_X"));
    }

    [Fact]
    public async Task UnavailableRipgrepFallsBackToTheManagedLineSearch()
    {
        WriteCode("one.cs", "needle\r\nneedle");
        var executable = Path.Combine(root, "invalid.exe");
        File.WriteAllText(executable, "not executable");
        var messages = new List<string>();
        using var code = new RoslynAnalyzer(new AnalysisOptions { SolutionPath = Code, RipgrepPath = executable }, messages.Add);
        var result = await code.SearchTextAsync("needle", 0, 10, default);
        Assert.Equal([1, 2], result.Items.Select(item => item.Line));
        Assert.Contains(messages, message => message.StartsWith("Text search engine: managed"));
        Assert.Null(await RipgrepTextSearch.FindLinesAsync([Code], null, "needle", executable, null, default));
    }

    // ---- 2 and 3. SQL references and callers ----

    private void WriteOrderSql()
    {
        // The Orders table itself has no script. Only procedures and a view reference it.
        WriteSql("ins.sql", "CREATE PROCEDURE dbo.usp_insert_Order @Id int AS BEGIN INSERT INTO dbo.Orders(Id) VALUES(@Id); EXEC dbo.usp_insert_OrderStatus @Id, 1; END");
        WriteSql("upd.sql", "CREATE PROCEDURE dbo.usp_update_Order @Id int AS BEGIN UPDATE [dbo].[Orders] SET Id = @Id WHERE Id = @Id; EXEC [dbo].[usp_insert_OrderStatus] @Id, 2; DELETE FROM Orders WHERE Id = 0; END");
        WriteSql("read.sql", "CREATE VIEW dbo.uv_Orders AS SELECT o.Id FROM dbo.Orders AS o JOIN dbo.OrderLines AS l ON l.OrderId = o.Id;");
        WriteSql("merge.sql", "CREATE PROCEDURE dbo.usp_merge_Order AS MERGE dbo.Orders AS t USING dbo.OrderStaging AS s ON t.Id = s.Id WHEN MATCHED THEN UPDATE SET Id = s.Id;");
        WriteSql("status.sql", "CREATE PROCEDURE dbo.usp_insert_OrderStatus @Id int, @Status int AS INSERT INTO dbo.OrderStatus(Id, Status) VALUES(@Id, @Status);");
        WriteSql("other.sql", "CREATE PROCEDURE archive.usp_other AS SELECT 1 FROM archive.Orders;");
    }

    [Fact]
    public async Task SqlReferencesListWritersAndReadersWithoutATargetDefinition()
    {
        WriteOrderSql();
        using var db = Database();
        var result = await db.FindReferencesAsync("Orders", 0, 100, default);
        var byOperation = result.Items.GroupBy(item => item.Operation).ToDictionary(group => group.Key, group => group.Select(item => item.Name).Order().ToArray());
        Assert.Equal(["usp_insert_Order"], byOperation["INSERT"]);
        Assert.Equal(["usp_update_Order"], byOperation["UPDATE"]);
        Assert.Equal(["usp_update_Order"], byOperation["DELETE"]);
        Assert.Equal(["usp_merge_Order"], byOperation["MERGE"]);
        Assert.Equal(["usp_other", "uv_Orders"], byOperation["SELECT"]); // schema omitted: every schema matches
        Assert.Equal(new[] { "INSERT", "UPDATE", "DELETE", "MERGE", "SELECT" }, result.OperationCounts.Keys);
        Assert.Equal(result.Items.Count, result.Total);
        Assert.All(result.Items, item => { Assert.True(File.Exists(item.File)); Assert.True(item.Line >= 1); });
        Assert.Equal("INSERT", result.Items[0].Operation); // writers come first
        Assert.Equal(1, Assert.Single(result.Items, item => item.Operation == "INSERT").Line);
        // Brackets and an explicit schema are honoured; another schema is excluded.
        var qualified = await db.FindReferencesAsync("[dbo].[Orders]", 0, 100, default);
        Assert.DoesNotContain(qualified.Items, item => item.Name == "usp_other");
        Assert.Equal(result.Total - 1, qualified.Total);
        var archive = await db.FindReferencesAsync("archive.Orders", 0, 100, default);
        // An unqualified reference may resolve to any schema, so it stays a candidate for archive.Orders.
        Assert.Equal(["usp_other"], archive.Items.Where(item => item.Operation == "SELECT").Select(item => item.Name));
        Assert.DoesNotContain(archive.Items, item => item.Name == "usp_insert_Order");
        // Case-insensitive; paging is applied after sorting and total stays exact.
        var page = await db.FindReferencesAsync("ORDERS", 1, 2, default);
        Assert.Equal(result.Total, page.Total); Assert.True(page.Truncated);
        Assert.Equal(result.Items.Skip(1).Take(2), page.Items);
        Assert.Empty((await db.FindReferencesAsync("NoSuchObject", 0, 10, default)).Items);
        await Assert.ThrowsAsync<ArgumentException>(() => db.FindReferencesAsync(" ", 0, 10, default));
        await Assert.ThrowsAsync<ArgumentException>(() => db.FindReferencesAsync("a.b.c.d", 0, 10, default));
    }

    [Fact]
    public async Task SqlReferencesFindProcedureExecCallersAndTheToolWrapsThem()
    {
        WriteOrderSql();
        using var db = Database();
        var tool = new DatabaseTools(db, NullLogger<DatabaseTools>.Instance, new SourceTrail.Mcp.ServerOptions());
        var result = await tool.FindSqlReferences("dbo.usp_insert_OrderStatus", default);
        Assert.Equal(["usp_insert_Order", "usp_update_Order"], result.Items.Select(item => item.Name));
        Assert.All(result.Items, item => { Assert.Equal("EXEC", item.Operation); Assert.Equal("Procedure", item.Kind); Assert.Equal("dbo", item.Schema); });
        Assert.Equal(2, result.OperationCounts["EXEC"]);
        var json = JsonSerializer.SerializeToElement(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("EXEC", json.GetProperty("items")[0].GetProperty("operation").GetString());
        Assert.Equal(2, json.GetProperty("operationCounts").GetProperty("EXEC").GetInt32());
        Assert.Equal("Ready", json.GetProperty("indexStatus").GetString());
    }

    [Fact]
    public async Task SqlReferencesWarnWhenTheIndexIsPartial()
    {
        WriteSql("ok.sql", "CREATE PROCEDURE dbo.A AS SELECT 1 FROM dbo.T;");
        WriteSql("bad.sql", "CREATE PROCEDURE !!!");
        using var db = Database();
        var result = await db.FindReferencesAsync("T", 0, 10, default);
        Assert.Single(result.Items);
        Assert.Equal("Partial", result.IndexStatus);
        Assert.Contains(result.Warnings, warning => warning.Contains("Partial"));
    }

    [Fact]
    public async Task ProcedureUsageAddsSqlCallersAndKeepsCSharpCandidates()
    {
        WriteOrderSql();
        WriteCode("Svc.cs", "namespace S; public class Svc { public void Go() { Run(\"dbo.usp_insert_OrderStatus\"); } void Run(string s) { } }");
        using var db = Database();
        await db.SelectAsync("SqlFiles", Scripts, default);
        using var code = new RoslynAnalyzer(new AnalysisOptions { SolutionPath = Code });
        await code.LoadAsync(Code, default);
        var tool = new AnalysisTools(code, NullLogger<AnalysisTools>.Instance, new AnalysisOptions { SolutionPath = Code },
            new SemaphoreSlim(1, 1), new SourceTrail.Mcp.ServerOptions(), db);
        var page = await tool.FindProcedureUsage("usp_insert_OrderStatus", default);
        Assert.Single(page.Items);
        Assert.Equal("ProcedureNameCandidate", page.Items[0].RelationType);
        Assert.Equal(["usp_insert_Order", "usp_update_Order"], page.SqlCallers.Select(site => site.Name));
        Assert.Equal(2, page.SqlCallerTotal);
        Assert.DoesNotContain(page.Warnings, warning => warning.StartsWith("SQL callers unavailable"));
        // The C# fields keep their names; sqlCallers is the only addition.
        var json = JsonSerializer.SerializeToElement(page, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        foreach (var name in new[] { "items", "total", "offset", "limit", "truncated", "snapshotId", "warnings", "sqlCallers" })
            Assert.True(json.TryGetProperty(name, out _), name);
    }

    [Fact]
    public async Task ProcedureUsageOnlyWarnsWhenTheSqlIndexIsUnavailable()
    {
        WriteCode("Svc.cs", "namespace S; public class Svc { public void Go() { Run(\"dbo.usp_X\"); } void Run(string s) { } }");
        using var code = new RoslynAnalyzer(new AnalysisOptions { SolutionPath = Code });
        await code.LoadAsync(Code, default);
        using var missing = new DatabaseAnalyzer(new DatabaseOptions { Mode = "SqlFiles", SqlFolder = "", CacheDirectory = Path.Combine(root, "cache2"), WatchFiles = false });
        foreach (var database in new DatabaseAnalyzer?[] { missing, null })
        {
            var tool = new AnalysisTools(code, NullLogger<AnalysisTools>.Instance, new AnalysisOptions { SolutionPath = Code },
                new SemaphoreSlim(1, 1), new SourceTrail.Mcp.ServerOptions(), database);
            var page = await tool.FindProcedureUsage("usp_X", default);
            Assert.Single(page.Items);
            Assert.Empty(page.SqlCallers);
            Assert.Contains(page.Warnings, warning => warning.StartsWith("SQL callers unavailable"));
        }
    }

    // ---- 4 and 5. derivedTypes and memberQuery ----

    [Fact]
    public async Task OverviewListsDerivedTypesFromSourceBaseLists()
    {
        WriteCode("Core.cs", "namespace M; public class ResultModelCore { public int Count; public void Save() { } public void Load() { } public int SaveCount { get; set; } }");
        WriteCode("Model.cs", "namespace Checkup.Model;\n\npublic class ResultModel : M.ResultModelCore, System.IDisposable { public void Dispose() { } }");
        WriteCode("Generic.cs", "namespace M; public interface IRepo<T> { } public class Repo : IRepo<int> { }\npublic class Unrelated { ResultModelCore field; }");
        WriteCode("Child.cs", "namespace M; public class Child : ResultModel_Other { } public struct S : IRepo<ResultModelCore> { } public class ResultModelCoreLike : ResultModelCore { }");
        using var code = new RoslynAnalyzer(new AnalysisOptions { SolutionPath = Code });
        await code.LoadAsync(Code, default);
        var type = Assert.Single((await code.FindSymbolsAsync("ResultModelCore", 0, 20, default)).Items.Where(item => item.SymbolKind == "Class" && item.Name == "ResultModelCore").ToArray());
        var overview = await code.OverviewAsync(type.Id, default);
        Assert.NotNull(overview.DerivedTypes);
        Assert.Equal(["Checkup.Model.ResultModel", "M.ResultModelCoreLike"], overview.DerivedTypes!.Select(item => item.Name));
        var model = overview.DerivedTypes![0];
        Assert.Equal(Path.Combine(Code, "Model.cs"), model.File);
        Assert.Equal(3, model.Line);
        Assert.Equal(64, model.Id.Length);
        Assert.Contains(overview.Warnings, warning => warning.Contains("DerivedTypes"));
        // The derived type's own ID is a valid overview input and its baseTypes still show the parents.
        var derived = await code.OverviewAsync(model.Id, default);
        Assert.Contains("Checkup.Model.ResultModel : ResultModelCore, IDisposable", derived.BaseTypes!);
        Assert.Null(derived.DerivedTypes);
        // Types nobody extends carry no derivedTypes field.
        var json = JsonSerializer.SerializeToElement(derived.ToBrief(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.False(json.TryGetProperty("derivedTypes", out _));
        Assert.True(JsonSerializer.SerializeToElement(overview.ToBrief(), new JsonSerializerOptions(JsonSerializerDefaults.Web)).TryGetProperty("derivedTypes", out _));
        // A file that declares one type gets the same list.
        Assert.Equal(overview.DerivedTypes!.Select(item => item.Id), (await code.OverviewAsync(Path.Combine(Code, "Core.cs"), default)).DerivedTypes!.Select(item => item.Id));
    }

    [Fact]
    public async Task OverviewMemberQueryFiltersMembersByNameAndDefaultsToAll()
    {
        WriteCode("Core.cs", "namespace M; public class Core { public int saveCount; public int Other; public void Save() { } public void Load() { } public int SaveValue { get; set; } public Core(int save) { } public event System.EventHandler Saved; }");
        using var code = new RoslynAnalyzer(new AnalysisOptions { SolutionPath = Code });
        await code.LoadAsync(Code, default);
        var all = await code.OverviewAsync(Path.Combine(Code, "Core.cs"), default);
        Assert.Equal(all.Methods.Count + all.Fields.Count + all.Properties.Count + all.Constructors.Count + all.Events.Count,
            (await code.OverviewAsync(Path.Combine(Code, "Core.cs"), default, "  ")).Methods.Count +
            (await code.OverviewAsync(Path.Combine(Code, "Core.cs"), default, "")).Fields.Count + all.Properties.Count + all.Constructors.Count + all.Events.Count);
        var filtered = await code.OverviewAsync(Path.Combine(Code, "Core.cs"), default, "SAVE");
        Assert.Equal(["Save"], filtered.Methods.Select(item => item.Name));
        Assert.Equal(["saveCount"], filtered.Fields.Select(item => item.Name));
        Assert.Equal(["SaveValue"], filtered.Properties.Select(item => item.Name));
        Assert.Equal(["Saved"], filtered.Events.Select(item => item.Name));
        Assert.Single(filtered.Types); // types are not filtered
        Assert.Empty((await code.OverviewAsync(Path.Combine(Code, "Core.cs"), default, "zzz")).Methods);
        Assert.Equal(2, all.Methods.Count);
    }
}
