using ModelContextProtocol;
using SourceTrail.Mcp;
using SourceTrail.Mcp.Tools;
using Xunit;

namespace SourceTrail.Tests;

public sealed class ServerOptionsTests
{
    [Fact]
    public void DefaultsToSharedLoopbackHttpServer()
    {
        var options = new ServerOptions();
        options.Validate();
        Assert.True(options.UsesHttp);
        Assert.Equal("http://127.0.0.1:3920", options.Url);
        Assert.Equal("/mcp", options.Path);
        Assert.False(options.TargetChangesAllowed);
    }

    [Theory]
    [InlineData("http://localhost:4000")]
    [InlineData("http://[::1]:4000")]
    [InlineData("http://127.0.0.1:4000")]
    public void AcceptsLoopbackUrls(string url) => new ServerOptions { Url = url }.Validate();

    [Theory]
    [InlineData("http://0.0.0.0:3920")]
    [InlineData("http://192.168.0.10:3920")]
    [InlineData("http://example.com:3920")]
    [InlineData("https://127.0.0.1:3920")]
    [InlineData("not a url")]
    public void RejectsNonLoopbackOrInvalidUrls(string url) =>
        Assert.Throws<ArgumentException>(() => new ServerOptions { Url = url }.Validate());

    [Fact]
    public void RejectsUnknownTransportAndRelativePath()
    {
        Assert.Throws<ArgumentException>(() => new ServerOptions { Transport = "Pipe" }.Validate());
        Assert.Throws<ArgumentException>(() => new ServerOptions { Path = "mcp" }.Validate());
    }

    [Fact]
    public void StdioSkipsUrlValidationAndAllowsTargetChangesByDefault()
    {
        var options = new ServerOptions { Transport = "stdio", Url = "http://0.0.0.0:1" };
        options.Validate();
        Assert.False(options.UsesHttp);
        Assert.True(options.TargetChangesAllowed);
    }

    [Fact]
    public void ExplicitSettingOverridesTargetChangeDefault()
    {
        Assert.True(new ServerOptions { AllowTargetChanges = true }.TargetChangesAllowed);
        Assert.False(new ServerOptions { Transport = "Stdio", AllowTargetChanges = false }.TargetChangesAllowed);
    }

    [Fact]
    public void ShortSwitchesMapToTransportSetting()
    {
        var mapped = ServerOptions.NormalizeArguments(["--stdio", "--Analysis:MaxResults=5", "--http"]);
        Assert.Equal(["--Server:Transport=Stdio", "--Analysis:MaxResults=5", "--Server:Transport=Http"], mapped);
    }

    [Fact]
    public void GuardBlocksTargetChangesOnSharedServerWithGuidance()
    {
        var error = Assert.Throws<McpException>(() =>
            TargetChangeGuard.EnsureAllowed(new ServerOptions(), "load_solution", "Analysis:SolutionPath"));
        Assert.Contains("Analysis:SolutionPath", error.Message);
        TargetChangeGuard.EnsureAllowed(new ServerOptions { Transport = "Stdio" }, "load_solution", "Analysis:SolutionPath");
    }
}
