using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using GhostClawUI.Shared;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GhostClawUI.Service.Ipc;

internal sealed class PipeHostedService : BackgroundService
{
    private readonly CommandRouter _router;
    private readonly ILogger<PipeHostedService> _logger;

    public PipeHostedService(CommandRouter router, ILogger<PipeHostedService> logger)
    {
        _router = router;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
#pragma warning disable CA2000 // Handled in HandleClientAsync background thread
            var pipe = NamedPipeServerStreamAcl.Create(
                GhostClawConstants.PipeName,
                PipeDirection.InOut,
#pragma warning restore CA2000
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                65536,
                65536,
                CreatePipeSecurity());

            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                _ = Task.Run(() => HandleClientAsync(pipe, stoppingToken), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                pipe.Dispose();
                break;
            }
            catch (Exception ex)
            {
                pipe.Dispose();
                _logger.LogError(ex, "Named pipe accept failed");
            }
        }
    }

    private static PipeSecurity CreatePipeSecurity()
    {
        var security = new PipeSecurity();
        
        var currentUser = WindowsIdentity.GetCurrent().User;
        if (currentUser != null)
        {
            security.AddAccessRule(new PipeAccessRule(currentUser, PipeAccessRights.FullControl, AccessControlType.Allow));
        }

        var allApplicationPackages = new SecurityIdentifier("S-1-15-2-1");
        var allRestrictedApplicationPackages = new SecurityIdentifier("S-1-15-2-2");

        security.AddAccessRule(new PipeAccessRule(allApplicationPackages, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(allRestrictedApplicationPackages, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe.ConfigureAwait(false))
        using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 65536, leaveOpen: true))
        {
            var writer = new StreamWriter(pipe, new UTF8Encoding(false), 65536, leaveOpen: true) { AutoFlush = true };
            await using (writer.ConfigureAwait(false))
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return;
            }

            PipeEnvelope? request = null;
            try
            {
                request = JsonSerializer.Deserialize<PipeEnvelope>(line, PipeJson.Options);
                if (request is null || request.Type != "request")
                {
                    return;
                }

                var response = await _router.HandleAsync(request, cancellationToken).ConfigureAwait(false);
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, PipeJson.Options)).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pipe request failed");
                try
                {
                    if (request is not null && pipe.IsConnected)
                    {
                        await writer.WriteLineAsync(JsonSerializer.Serialize(PipeEnvelope.ErrorResponse(request, ex.Message), PipeJson.Options)).ConfigureAwait(false);
                        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception writeEx)
                {
                    _logger.LogError(writeEx, "Failed to send error response.");
                }
            }
        }
        }
    }
}
