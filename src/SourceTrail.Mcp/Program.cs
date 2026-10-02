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
        var builder = CreateBuilder(args);
        var analysisOptions = RegisterServices(builder);

        using var host = builder.Build();
        var startupLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SourceTrail");
        startupLogger.LogInformation("SourceTrail MCP starting.");
        await LoadConfiguredSolutionAsync(host, analysisOptions, startupLogger);
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
    private static AnalysisOptions RegisterServices(HostApplicationBuilder builder)
    {
        var analysisOptions = builder.Configuration.GetSection("Analysis").Get<AnalysisOptions>() ?? new();
        var databaseOptions = builder.Configuration.GetSection("Database").Get<DatabaseOptions>() ?? new();
        builder.Services.AddSingleton(analysisOptions);
        builder.Services.AddSingleton(databaseOptions);
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
        builder.Services.AddSingleton<FlowAnalyzer>();
        builder.Services.AddSingleton(new SemaphoreSlim(1, 1));
        builder.Services.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly();

        return analysisOptions;
    }

    private static async Task LoadConfiguredSolutionAsync(IHost host, AnalysisOptions analysisOptions, ILogger startupLogger)
    {
        // 시작 경로가 없으면 load_solution 호출을 기다린다. 시작 로딩 실패도 도구로 재시도할 수 있다.
        if (!string.IsNullOrWhiteSpace(analysisOptions.SolutionPath))
        {
            try { await host.Services.GetRequiredService<ICodeAnalyzer>().LoadAsync(analysisOptions.SolutionPath, CancellationToken.None); }
            catch (Exception error)
            {
                startupLogger.LogWarning("Configured solution load failed ({ErrorType}); use load_solution to retry.", error.GetType().Name);
            }
        }
    }

}
