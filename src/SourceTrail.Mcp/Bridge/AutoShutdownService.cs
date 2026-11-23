using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SourceTrail.Mcp.Bridge;

/// <summary>Stops an on-demand HTTP server once no bridge has been connected for the configured grace period.</summary>
public sealed class AutoShutdownService(BridgeRegistry bridges, IHostApplicationLifetime lifetime, ServerOptions server,
    ILogger<AutoShutdownService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var grace = TimeSpan.FromSeconds(server.ShutdownAfterNoBridgesSeconds);
        var timeout = TimeSpan.FromSeconds(server.BridgeTimeoutSeconds);
        var interval = TimeSpan.FromSeconds(Math.Clamp(server.ShutdownAfterNoBridgesSeconds / 4, 1, 30));
        var clock = new ShutdownClock(grace, DateTimeOffset.UtcNow);
        logger.LogInformation("Auto-shutdown on: exits after {Grace}s with no connected bridge.", server.ShutdownAfterNoBridgesSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
            if (!clock.Observe(bridges.ActiveCount(timeout), DateTimeOffset.UtcNow)) continue;
            logger.LogInformation("No connected bridge for {Grace}s; stopping SourceTrail MCP.", server.ShutdownAfterNoBridgesSeconds);
            lifetime.StopApplication();
            return;
        }
    }
}
