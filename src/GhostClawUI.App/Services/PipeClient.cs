using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using GhostClawUI.Service.Infrastructure;
using GhostClawUI.Service.Storage;
using GhostClawUI.Service.Providers;
using GhostClawUI.Service.Agent;
using GhostClawUI.Service.Ipc;
using GhostClawUI.Shared;

namespace GhostClawUI.App.Services;

internal sealed class PipeClient
{
    private static readonly ILoggerFactory _loggerFactory;
    private static readonly ILogger<PipeClient> _logger;
    private static CommandRouter? _router;
    private static readonly Task _initTask;

    static PipeClient()
    {
        _loggerFactory = LoggerFactory.Create(builder => 
        {
            builder.AddDebug();
        });
        _logger = _loggerFactory.CreateLogger<PipeClient>();

        _initTask = Task.Run(() =>
        {
            var paths = new AppPaths();
            var store = new EncryptedStore(paths);
            var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            var gateway = new ProviderGateway(httpClient);
            var mcpCatalog = new McpCatalog(store, httpClient, paths);
            var mcpToolRunner = new McpToolRunner(paths);
            
            var agentRunnerLogger = _loggerFactory.CreateLogger<GhostClawAgentRunner>();
            var supervisorLogger = _loggerFactory.CreateLogger<GhostClawSupervisor>();
            
            var agentRunner = new GhostClawAgentRunner(paths, mcpCatalog, agentRunnerLogger);
            var supervisor = new GhostClawSupervisor(paths, mcpCatalog, supervisorLogger);
            
            try
            {
                supervisor.ProvisionRuntime();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to provision runtime");
            }

            _router = new CommandRouter(
                store,
                gateway,
                mcpCatalog,
                mcpToolRunner,
                agentRunner,
                supervisor,
                paths);

            try
            {
                var telegramService = new TelegramService(
                    store,
                    _router,
                    httpClient,
                    _loggerFactory.CreateLogger<TelegramService>());
                _ = telegramService.StartAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start Telegram service");
            }
        });
    }

    public async Task<T?> RequestAsync<T>(string command, object? payload = null, CancellationToken cancellationToken = default)
    {
        await _initTask;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var payloadNode = payload is null ? null : JsonSerializer.SerializeToNode(payload, PipeJson.Options);
        var payloadSize = payloadNode?.ToJsonString(PipeJson.Options).Length ?? 0;
        
        _logger.LogInformation("[IPC] RequestAsync started for command: {Command}. Payload length: {PayloadSize}", command, payloadSize);

        var requestEnvelope = new PipeEnvelope(
            "request",
            command,
            Guid.NewGuid().ToString("N"),
            payloadNode,
            null,
            DateTimeOffset.UtcNow);

        var responseEnvelope = await _router!.HandleAsync(requestEnvelope, cancellationToken);
        sw.Stop();

        var responseSize = responseEnvelope.Payload?.ToJsonString(PipeJson.Options).Length ?? 0;
        _logger.LogInformation("[IPC] RequestAsync completed for command: {Command} in {Elapsed}ms. Response length: {ResponseSize}", command, sw.ElapsedMilliseconds, responseSize);

        if (responseEnvelope.Type == "error")
        {
            throw new InvalidOperationException(responseEnvelope.Error ?? "GhostClaw service request failed.");
        }

        return responseEnvelope.Payload is null ? default : responseEnvelope.Payload.Deserialize<T>(PipeJson.Options);
    }
}
