using SourceTrail.Core.Models;
using SourceTrail.SqlServer.Metadata;
using Xunit;
namespace SourceTrail.Tests;

// 3.4.1: UPDATE/DELETE 대상이 별칭이면 같은 문장 FROM의 실제 테이블을 쓰기로 분류한다.
public sealed class SqlAliasTargetTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "alias-" + Guid.NewGuid().ToString("N"));
    public SqlAliasTargetTests() => Directory.CreateDirectory(Path.Combine(root, "sql"));
    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    private async Task<Dictionary<string, string[]>> Operations(string body, string table)
    {
        File.WriteAllText(Path.Combine(root, "sql", "p.sql"), "CREATE PROCEDURE dbo.usp_test AS\r\nBEGIN\r\n" + body + "\r\nEND");
        using var db = new DatabaseAnalyzer(new DatabaseOptions
            { Mode = "SqlFiles", SqlFolder = Path.Combine(root, "sql"), CacheDirectory = Path.Combine(root, "cache"), WatchFiles = false });
        var result = await db.FindReferencesAsync(table, 0, 100, default);
        return result.Items.GroupBy(i => i.Operation).ToDictionary(g => g.Key, g => g.Select(i => i.Line.ToString()).ToArray());
    }

    [Fact]
    public async Task AliasUpdateTargetsTheRealTableAndDropsTheSameStatementSelect()
    {
        var ops = await Operations("UPDATE a SET a.X = 1\r\nFROM dbo.Orders a\r\nJOIN dbo.Lines l ON l.Id = a.Id;", "Orders");
        Assert.Equal(["3"], ops["UPDATE"]);
        Assert.False(ops.ContainsKey("SELECT"));
        Assert.Equal(["5"], (await Operations("UPDATE a SET a.X = 1\r\nFROM dbo.Orders a\r\nJOIN dbo.Lines l ON l.Id = a.Id;", "Lines"))["SELECT"]);
    }

    [Fact]
    public async Task AliasDeleteWithAndWithoutAsTargetsTheRealTable()
    {
        var ops = await Operations("DELETE a FROM dbo.Orders a JOIN dbo.Lines l ON l.Id = a.Id;\r\nDELETE c FROM dbo.Lines AS c WHERE c.Id = 1;", "Orders");
        Assert.Equal(["3"], ops["DELETE"]);
        Assert.False(ops.ContainsKey("SELECT"));
        var lines = await Operations("DELETE a FROM dbo.Orders a JOIN dbo.Lines l ON l.Id = a.Id;\r\nDELETE c FROM dbo.Lines AS c WHERE c.Id = 1;", "Lines");
        Assert.Equal(["4"], lines["DELETE"]);
        Assert.Equal(["3"], lines["SELECT"]);
        var asUpdate = await Operations("UPDATE C SET X = 1 FROM dbo.Orders AS C;", "Orders");
        Assert.Equal(["3"], asUpdate["UPDATE"]);
        Assert.False(asUpdate.ContainsKey("SELECT"));
    }

    [Fact]
    public async Task OtherAliasOfTheSameTableStaysAReadAndUnqualifiedNameResolvesTheSchema()
    {
        var ops = await Operations("UPDATE a SET a.X = h.X\r\nFROM dbo.Orders a\r\nJOIN dbo.Orders h ON h.Id = a.Id;", "Orders");
        Assert.Equal(["3"], ops["UPDATE"]);
        Assert.Equal(["5"], ops["SELECT"]); // 이력용 JOIN은 읽기
        var plain = await Operations("UPDATE Orders SET X = 1 FROM dbo.Orders JOIN dbo.Lines l ON l.Id = Orders.Id;", "dbo.Orders");
        Assert.Equal(["3"], plain["UPDATE"]);
        Assert.False(plain.ContainsKey("SELECT"));
    }

    [Fact]
    public async Task TableVariableAliasIsNotReportedAsATable()
    {
        const string body = "DECLARE @t TABLE(Id int);\r\nUPDATE v SET v.Id = 1 FROM @t v JOIN dbo.Orders o ON o.Id = v.Id;";
        var ops = await Operations(body, "Orders");
        Assert.Equal(["4"], ops["SELECT"]);
        Assert.False(ops.ContainsKey("UPDATE"));
        Assert.Empty(await Operations(body, "v"));
    }

    [Fact]
    public async Task CteAndTempTableAliasKeepTheirOwnNamesNotARealTable()
    {
        const string cte = "WITH c AS (SELECT Id FROM dbo.Orders)\r\nUPDATE x SET Id = 1 FROM c x;";
        var ops = await Operations(cte, "Orders");
        Assert.Equal(["3"], ops["SELECT"]); // CTE 본문은 읽기, 쓰기로 올리지 않는다.
        Assert.False(ops.ContainsKey("UPDATE"));
        Assert.Equal(["4"], (await Operations(cte, "c"))["UPDATE"]);
        var temp = await Operations("UPDATE t SET Id = 1 FROM #tmp t JOIN dbo.Orders o ON o.Id = t.Id;", "Orders");
        Assert.Equal(["3"], temp["SELECT"]);
        Assert.False(temp.ContainsKey("UPDATE"));
    }

    [Fact]
    public async Task UnresolvableAliasTargetIsWarnedAndNotAttributedToATable()
    {
        File.WriteAllText(Path.Combine(root, "sql", "p.sql"),
            "CREATE PROCEDURE dbo.usp_test AS\r\nBEGIN\r\nUPDATE d SET d.Id = 1 FROM (SELECT Id FROM dbo.Orders) d;\r\nEND");
        using var db = new DatabaseAnalyzer(new DatabaseOptions
            { Mode = "SqlFiles", SqlFolder = Path.Combine(root, "sql"), CacheDirectory = Path.Combine(root, "cache"), WatchFiles = false });
        var found = (await db.FindAsync("usp_test", 0, 10, false, default)).Items.Single();
        Assert.Contains(found.Warnings, w => w.Contains("Write target alias 'd'"));
        var orders = await db.FindReferencesAsync("Orders", 0, 10, default);
        Assert.Equal("SELECT", Assert.Single(orders.Items).Operation);
        Assert.Empty((await db.FindReferencesAsync("d", 0, 10, default)).Items);
    }
}
