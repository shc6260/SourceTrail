using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using SourceTrail.Core.Contracts;
using SourceTrail.Core.Flow;
using SourceTrail.Core.Models;
using SourceTrail.Roslyn.Workspace;
using SourceTrail.SqlServer.Metadata;

namespace SourceTrail.Mcp;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        // Resolve configuration next to the executable, independent of the MCP client's working directory.
        builder.Configuration.Sources.Clear();
        builder.Configuration
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .AddCommandLine(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

        var analysisOptions = builder.Configuration.GetSection("Analysis").Get<AnalysisOptions>() ?? new();
        var databaseOptions = builder.Configuration.GetSection("Database").Get<DatabaseOptions>() ?? new();
        builder.Services.AddSingleton(analysisOptions);
        builder.Services.AddSingleton(databaseOptions);
        builder.Services.AddSingleton<ICodeAnalyzer>(services =>
        {
            var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Roslyn");
            return new RoslynAnalyzer(analysisOptions, message => logger.LogInformation("{Diagnostic}", message));
        });
        builder.Services.AddSingleton<IProcedureAnalyzer>(services =>
        {
            var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("SqlServer");
            return new SqlProcedureAnalyzer(databaseOptions, message => logger.LogWarning("{Diagnostic}", message));
        });
        builder.Services.AddSingleton<FlowAnalyzer>();
        builder.Services.AddSingleton(new SemaphoreSlim(1, 1));
        builder.Services.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly();

        using var host = builder.Build();
        var startupLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SourceTrail");
        startupLogger.LogInformation("SourceTrail MCP starting.");
        // Loading is explicit by default. An optional startup path uses the same validation and diagnostics.
        if (!string.IsNullOrWhiteSpace(analysisOptions.SolutionPath))
        {
            try { await host.Services.GetRequiredService<ICodeAnalyzer>().LoadAsync(analysisOptions.SolutionPath, CancellationToken.None); }
            catch (Exception error)
            {
                startupLogger.LogWarning("Configured solution load failed ({ErrorType}); use load_solution to retry.", error.GetType().Name);
            }
        }
        await host.RunAsync();
    }
}
