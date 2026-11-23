using System.Text.Json.Nodes;
using SourceTrail.Mcp;
using SourceTrail.Mcp.Bridge;
using Xunit;

namespace SourceTrail.Tests;

public sealed class BridgeTests
{
    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void RegistryDropsBridgesWithoutRecentHeartbeat()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-11-23T00:00:00Z"));
        var registry = new BridgeRegistry(clock);
        registry.Touch("a"); registry.Touch("b");
        Assert.Equal(2, registry.ActiveCount(TimeSpan.FromSeconds(90)));
        clock.Now += TimeSpan.FromSeconds(60);
        registry.Touch("b");
        clock.Now += TimeSpan.FromSeconds(40);
        Assert.Equal(1, registry.ActiveCount(TimeSpan.FromSeconds(90)));
        registry.Leave("b");
        Assert.Equal(0, registry.ActiveCount(TimeSpan.FromSeconds(90)));
        registry.Touch(" ");
        Assert.Equal(0, registry.ActiveCount(TimeSpan.FromSeconds(90)));
    }

    [Fact]
    public void ShutdownWaitsForGraceAfterLastBridgeAndResetsOnReconnect()
    {
        var start = DateTimeOffset.Parse("2026-11-23T00:00:00Z");
        var clock = new ShutdownClock(TimeSpan.FromMinutes(10), start);
        Assert.False(clock.Observe(0, start.AddMinutes(9)));
        Assert.False(clock.Observe(1, start.AddMinutes(9.5)));
        Assert.False(clock.Observe(0, start.AddMinutes(12)));
        Assert.False(clock.Observe(0, start.AddMinutes(21)));
        Assert.True(clock.Observe(0, start.AddMinutes(22)));
    }

    [Fact]
    public void ServerWithNoBridgeEverConnectedStopsAfterGraceFromStart()
    {
        var start = DateTimeOffset.Parse("2026-11-23T00:00:00Z");
        var clock = new ShutdownClock(TimeSpan.FromSeconds(30), start);
        Assert.False(clock.Observe(0, start.AddSeconds(29)));
        Assert.True(clock.Observe(0, start.AddSeconds(30)));
    }

    [Fact]
    public void ExtractsJsonRpcMessagesFromSseAndJsonBodies()
    {
        var sse = "event: message\r\ndata: {\"id\":1,\"result\":{}}\r\n\r\nevent: message\ndata: {\"id\":2,\n\n";
        Assert.Equal(["{\"id\":1,\"result\":{}}", "{\"id\":2,"], McpBridge.Messages("text/event-stream", sse));
        Assert.Equal(["{\"id\":3}"], McpBridge.Messages("application/json", " {\"id\":3}\n"));
        Assert.Empty(McpBridge.Messages("text/event-stream", ""));
        Assert.Equal(["a\nb"], McpBridge.Messages("text/event-stream", "data: a\ndata: b\n\n"));
    }

    [Fact]
    public void ErrorKeepsRequestIdAndJsonRpcShape()
    {
        var error = JsonNode.Parse(McpBridge.Error(JsonValue.Create(7)!, "down"))!;
        Assert.Equal("2.0", error["jsonrpc"]!.GetValue<string>());
        Assert.Equal(7, error["id"]!.GetValue<int>());
        Assert.Equal(-32000, error["error"]!["code"]!.GetValue<int>());
        Assert.Equal("down", error["error"]!["message"]!.GetValue<string>());
        var named = JsonNode.Parse(McpBridge.Error(JsonValue.Create("req-1")!, "x"))!;
        Assert.Equal("req-1", named["id"]!.GetValue<string>());
    }

    [Fact]
    public void LaunchTaskIsManagedAndRunsTheLauncherOnDemand()
    {
        var launcher = @"D:\Tools\R&D <x>\SourceTrail.Launcher.exe";
        var xml = LaunchTask.Build(launcher, @"PC\user");
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-16\"?>", xml);
        Assert.True(LaunchTask.IsManaged(xml));
        Assert.Equal(launcher, LaunchTask.CommandOf(xml));
        Assert.Contains("<Arguments>--http --Server:AutoShutdown=true</Arguments>", xml);
        Assert.Contains(@"<WorkingDirectory>D:\Tools\R&amp;D &lt;x&gt;</WorkingDirectory>", xml);
        Assert.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", xml);
        Assert.DoesNotContain("Triggers", xml);
    }

    [Fact]
    public void HandRegisteredLaunchTaskIsNotManaged()
    {
        var manual = """
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Actions Context="Author"><Exec><Command>"C:\Program Files\dotnet\dotnet.exe"</Command></Exec></Actions>
            </Task>
            """;
        Assert.False(LaunchTask.IsManaged(manual));
        Assert.Equal(@"C:\Program Files\dotnet\dotnet.exe", LaunchTask.CommandOf(manual));
        Assert.Null(LaunchTask.CommandOf("not xml"));
    }

    [Fact]
    public void BridgeSwitchAndValidation()
    {
        Assert.Equal(["--Server:Transport=Bridge"], ServerOptions.NormalizeArguments(["--bridge"]));
        var bridge = new ServerOptions { Transport = "bridge" };
        bridge.Validate();
        Assert.True(bridge.UsesBridge);
        Assert.False(bridge.UsesHttp);
        Assert.Equal("http://127.0.0.1:3920/mcp", bridge.McpUri.ToString());
        Assert.Equal("http://127.0.0.1:3920/bridge/heartbeat", new Uri(bridge.BaseUri, "bridge/heartbeat").ToString());
        Assert.Throws<ArgumentException>(() => new ServerOptions { Transport = "Bridge", Url = "http://10.0.0.1:3920" }.Validate());
        Assert.Throws<ArgumentException>(() => new ServerOptions { HeartbeatSeconds = 90, BridgeTimeoutSeconds = 90 }.Validate());
        Assert.Throws<ArgumentException>(() => new ServerOptions { ShutdownAfterNoBridgesSeconds = 0 }.Validate());
        Assert.False(new ServerOptions().AutoShutdown);
    }
}
