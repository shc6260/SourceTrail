using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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

        args = ServerOptions.NormalizeArguments(args);
        ServerOptions server;
        try { server = ReadServerOptions(args); }
        catch (ArgumentException error)
        {
            Console.Error.WriteLine($"SourceTrail MCP configuration error: {error.Message}");
            Environment.ExitCode = 2;
            return;
        }
        if (server.UsesBridge)
            await RunBridgeAsync(server);
        else if (server.UsesHttp)
            await RunHttpAsync(args, server);
        else
            await RunStdioAsync(args, server);
    }

    // 클라이언트가 stdio로 실행하는 가벼운 중계기. 공유 HTTP 서버가 없으면 켜고 요청을 전달한다.
    private static async Task RunBridgeAsync(ServerOptions server)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        var bridge = new Bridge.McpBridge(server, typeof(Program).Assembly.Location, Console.Error);
        Environment.ExitCode = await bridge.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), cancellation.Token);
    }

    // 기본: 한 번 띄운 상주 프로세스를 여러 MCP 클라이언트가 HTTP로 함께 사용한다.
    private static async Task RunHttpAsync(string[] args, ServerOptions server)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        ConfigureSources(builder.Configuration, args);
        ConfigureLogging(builder.Logging);
        RegisterServices(builder.Services, builder.Configuration, server);
        builder.Services.AddMcpServer().WithHttpTransport().WithToolsFromAssembly();
        builder.Services.AddSingleton(new Bridge.BridgeRegistry());
        if (server.AutoShutdown) builder.Services.AddHostedService<Bridge.AutoShutdownService>();
        builder.WebHost.UseUrls(server.Url);

        await using var app = builder.Build();
        app.MapMcp(server.Path);
        // 브리지가 연결 중임을 알리는 신호. 자동 종료 서버는 신호가 끊긴 뒤 유예 시간이 지나면 종료한다.
        app.MapPost("/bridge/heartbeat", (Bridge.BridgeBeat beat, Bridge.BridgeRegistry bridges) => { bridges.Touch(beat.Id); return Results.Ok(); });
        app.MapPost("/bridge/leave", (Bridge.BridgeBeat beat, Bridge.BridgeRegistry bridges) => { bridges.Leave(beat.Id); return Results.Ok(); });
        var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SourceTrail");
        using var measurements = AnalysisTelemetry.Listen(message => startupLogger.LogInformation("{Measurement}", message));
        startupLogger.LogInformation("SourceTrail MCP starting (HTTP) at {Url}{Path}. Target changes allowed: {Allowed}. Auto-shutdown: {AutoShutdown}.",
            server.Url.TrimEnd('/'), server.Path, server.TargetChangesAllowed, server.AutoShutdown);
        await app.RunAsync();
    }

    // 선택: 클라이언트가 직접 실행하는 1:1 stdio 프로세스(--stdio).
    private static async Task RunStdioAsync(string[] args, ServerOptions server)
    {
        var builder = CreateBuilder(args);
        RegisterServices(builder.Services, builder.Configuration, server);
        builder.Services.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly();

        using var host = builder.Build();
        var startupLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SourceTrail");
        using var measurements = AnalysisTelemetry.Listen(message => startupLogger.LogInformation("{Measurement}", message));
        startupLogger.LogInformation("SourceTrail MCP starting (stdio).");
        await host.RunAsync();
    }

    private static ServerOptions ReadServerOptions(string[] args)
    {
        var configuration = new ConfigurationBuilder();
        ConfigureSources(configuration, args);
        var server = configuration.Build().GetSection("Server").Get<ServerOptions>() ?? new();
        server.Validate();
        return server;
    }

    // 실행한 작업 폴더와 무관하게 서버 DLL 옆의 설정을 읽고 로그는 stderr로 보낸다.
    private static HostApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        ConfigureSources(builder.Configuration, args);
        ConfigureLogging(builder.Logging);
        return builder;
    }

    // Resolve configuration next to the executable, independent of the MCP client's working directory.
    private static void ConfigureSources(IConfigurationBuilder configuration, string[] args)
    {
        configuration.Sources.Clear();
        configuration
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .AddCommandLine(args);
    }

    private static void ConfigureLogging(ILoggingBuilder logging)
    {
        logging.ClearProviders();
        logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
    }

    // 분석기는 Singleton으로 등록해 도구 호출 간 작업 공간과 색인을 재사용한다.
    // HTTP 모드에서는 연결된 모든 클라이언트가 같은 인스턴스와 실행 게이트를 공유한다.
    private static void RegisterServices(IServiceCollection services, IConfiguration configuration, ServerOptions server)
    {
        var analysisOptions = configuration.GetSection("Analysis").Get<AnalysisOptions>() ?? new();
        var databaseOptions = configuration.GetSection("Database").Get<DatabaseOptions>() ?? new();
        var indexOptions = configuration.GetSection("Indexing").Get<IndexPreparationOptions>() ?? new();
        IndexPreparationService.ApplyActive(analysisOptions, databaseOptions, indexOptions);
        services.AddSingleton(server);
        services.AddSingleton(analysisOptions);
        services.AddSingleton(databaseOptions);
        services.AddSingleton(indexOptions);
        services.AddSingleton<IndexPreparationService>();
        services.AddSingleton<ICodeAnalyzer>(provider =>
        {
            var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Roslyn");
            return new RoslynAnalyzer(analysisOptions, message => logger.LogInformation("{Diagnostic}", message));
        });
        services.AddSingleton<DatabaseAnalyzer>(provider =>
        {
            var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("SqlServer");
            return new DatabaseAnalyzer(databaseOptions, message => logger.LogWarning("{Diagnostic}", message));
        });
        services.AddSingleton<IProcedureAnalyzer>(provider => provider.GetRequiredService<DatabaseAnalyzer>());
        services.AddSingleton(new SemaphoreSlim(1, 1));
    }
}
