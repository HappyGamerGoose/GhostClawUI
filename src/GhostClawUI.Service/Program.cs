using GhostClawUI.Service.Agent;
using GhostClawUI.Service.Infrastructure;
using GhostClawUI.Service.Ipc;
using GhostClawUI.Service.Providers;
using GhostClawUI.Service.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options => options.ServiceName = GhostClawUI.Shared.GhostClawConstants.ServiceName)
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
        });
    })
    .ConfigureServices(services =>
    {
        services.AddSingleton<AppPaths>();
        services.AddSingleton<EncryptedStore>();
        services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromMinutes(15) });
        services.AddSingleton<ProviderGateway>();
        services.AddSingleton<McpCatalog>();
        services.AddSingleton<McpToolRunner>();
        services.AddSingleton<GhostClawAgentRunner>();
        services.AddSingleton<GhostClawSupervisor>();
        services.AddSingleton<CommandRouter>();
        services.AddHostedService<GhostClawHostedService>();
        services.AddHostedService<PipeHostedService>();
        services.AddHostedService<TelegramService>();
        services.AddHostedService<BackgroundWorkerService>();
    });

await builder.Build().RunAsync().ConfigureAwait(false);
