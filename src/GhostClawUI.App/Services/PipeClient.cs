using System;
using System.IO.Pipes;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GhostClawUI.Shared;

namespace GhostClawUI.App.Services;

internal sealed class PipeClient
{
    public async Task<T?> RequestAsync<T>(string command, object? payload = null, CancellationToken cancellationToken = default)
    {
        var payloadNode = payload is null ? null : JsonSerializer.SerializeToNode(payload, PipeJson.Options);
        string? token = null;
        try
        {
            var tokenPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GhostClawUI", "ipc_token.txt");
            if (File.Exists(tokenPath))
            {
                token = File.ReadAllText(tokenPath);
            }
        }
        catch { }

        var requestEnvelope = new PipeEnvelope(
            "request",
            command,
            Guid.NewGuid().ToString("N"),
            payloadNode,
            null,
            DateTimeOffset.UtcNow,
            token);

        var requestJson = JsonSerializer.Serialize(requestEnvelope, PipeJson.Options);

        using var pipe = new NamedPipeClientStream(
            ".",
            GhostClawConstants.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync(3000, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException("Failed to connect to GhostClaw service within the timeout.");
        }

        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 65536, leaveOpen: true);
        var writer = new StreamWriter(pipe, new UTF8Encoding(false), 65536, leaveOpen: true) { AutoFlush = true };

        await using (writer.ConfigureAwait(false))
        {
            await writer.WriteLineAsync(requestJson).ConfigureAwait(false);
            var responseJson = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            
            if (responseJson is null)
            {
                throw new InvalidOperationException("Service disconnected unexpectedly.");
            }

            var responseEnvelope = JsonSerializer.Deserialize<PipeEnvelope>(responseJson, PipeJson.Options);
            if (responseEnvelope is null)
            {
                throw new InvalidOperationException("Invalid response from service.");
            }

            if (responseEnvelope.Type == "error")
            {
                throw new InvalidOperationException(responseEnvelope.Error ?? "GhostClaw service request failed.");
            }

            return responseEnvelope.Payload is null ? default : responseEnvelope.Payload.Deserialize<T>(PipeJson.Options);
        }
    }
}
