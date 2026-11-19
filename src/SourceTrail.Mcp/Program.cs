using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using SourceTrail.Core.Contracts;
using SourceTrail.Core;
using SourceTrail.Core.Models;
using SourceTrail.Roslyn.Workspace;
using SourceTrail.SqlServer.Metadata;
using SourceTrail.Mcp.Indexing;

namespace SourceTrail.Mcp;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        // Codex reads MCP stderr as UTF-8, including localized file diagnostics.
        Console.OutputEncoding = Encoding.UTF8;
        if (args.FirstOrDefault() == "prepare-index")
        {
            var configuration = CreateBuilder([]).Configuration;
            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
            Environment.ExitCode = await IndexPreparationCommand.RunAsync(args[1..],
                configuration.GetSection("Analysis").Get<AnalysisOptions>() ?? new(),
                configuration.GetSection("Database").Get<DatabaseOptions>() ?? new(),
                configuration.GetSection("Indexing").Get<IndexPreparationOptions>() ?? new(),
                Console.Out, Console.Error, cancellation.Token);
            return;
        }
        var builder = CreateBuilder(args);
        RegisterServices(builder);

        using var host = builder.Build();
        var startupLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SourceTrail");
        using var measurements = AnalysisTelemetry.Listen(message => startupLogger.LogInformation("{Measurement}", message));
        startupLogger.LogInformation("SourceTrail MCP starting.");
        await host.RunAsync();
    }

    // 실행한 작업 폴더와 무관하게 서버 DLL 옆의 설정을 읽고 로그는 stderr로 보낸다.
    private static HostApplicationBuilder CreateBuilder(string[] args)
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

        return builder;
    }

    // 분석기는 Singleton으로 등록해 도구 호출 간 작업 공간과 색인을 재사용한다.
    private static void RegisterServices(HostApplicationBuilder builder)
    {
        var analysisOptions = builder.Configuration.GetSection("Analysis").Get<AnalysisOptions>() ?? new();
        var databaseOptions = builder.Configuration.GetSection("Database").Get<DatabaseOptions>() ?? new();
        var indexOptions = builder.Configuration.GetSection("Indexing").Get<IndexPreparationOptions>() ?? new();
        IndexPreparationService.ApplyActive(analysisOptions, databaseOptions, indexOptions);
        builder.Services.AddSingleton(analysisOptions);
        builder.Services.AddSingleton(databaseOptions);
        builder.Services.AddSingleton(indexOptions);
        builder.Services.AddSingleton<IndexPreparationService>();
        builder.Services.AddSingleton<ICodeAnalyzer>(services =>
        {
            var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Roslyn");
            return new RoslynAnalyzer(analysisOptions, message => logger.LogInformation("{Diagnostic}", message));
        });
        builder.Services.AddSingleton<DatabaseAnalyzer>(services =>
        {
            var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("SqlServer");
            return new DatabaseAnalyzer(databaseOptions, message => logger.LogWarning("{Diagnostic}", message));
        });
        builder.Services.AddSingleton<IProcedureAnalyzer>(services => services.GetRequiredService<DatabaseAnalyzer>());
        builder.Services.AddSingleton(new SemaphoreSlim(1, 1));
        builder.Services.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly();

    }

}
