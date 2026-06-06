using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using GhostClawUI.Service.Storage;
using GhostClawUI.Service.Ipc;
using GhostClawUI.Shared;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GhostClawUI.Service.Agent;

internal sealed class WhatsAppService : BackgroundService
{
    private readonly EncryptedStore _store;
    private readonly CommandRouter _router;
    private readonly HttpClient _httpClient;
    private readonly ILogger<WhatsAppService> _logger;

    public WhatsAppService(
        EncryptedStore store,
        CommandRouter router,
        HttpClient httpClient,
        ILogger<WhatsAppService> logger)
    {
        _store = store;
        _router = router;
        _httpClient = httpClient;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("WhatsApp Bot Webhook listener starting...");

        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = _store.GetWhatsAppSettings();
            if (settings == null || !settings.IsEnabled || string.IsNullOrWhiteSpace(settings.WebhookPort))
            {
                await Task.Delay(5000, stoppingToken).ConfigureAwait(false);
                continue;
            }

            if (!int.TryParse(settings.WebhookPort, out int port))
            {
                port = 5000;
            }

            using var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{port}/webhook/whatsapp/");
            listener.Prefixes.Add($"http://127.0.0.1:{port}/webhook/whatsapp/");

            try
            {
                listener.Start();
                _logger.LogInformation("WhatsApp Webhook listener bound to port {Port}", port);

                while (!stoppingToken.IsCancellationRequested)
                {
                    var ctxSettings = _store.GetWhatsAppSettings();
                    if (ctxSettings == null || !ctxSettings.IsEnabled || ctxSettings.WebhookPort != settings.WebhookPort)
                    {
                        break; // Restart listener if port or enabled status changes
                    }

                    var contextTask = listener.GetContextAsync();
                    var timeoutTask = Task.Delay(5000, stoppingToken);
                    var completedTask = await Task.WhenAny(contextTask, timeoutTask).ConfigureAwait(false);

                    if (completedTask == timeoutTask)
                    {
                        continue;
                    }

                    var context = await contextTask.ConfigureAwait(false);
                    _ = HandleWebhookRequestAsync(context, ctxSettings, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in WhatsApp webhook listener");
                await Task.Delay(10000, stoppingToken).ConfigureAwait(false);
            }
            finally
            {
                if (listener.IsListening)
                {
                    listener.Stop();
                }
            }
        }

        _logger.LogInformation("WhatsApp Webhook listener stopped.");
    }

    private async Task HandleWebhookRequestAsync(HttpListenerContext context, WhatsAppSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            if (context.Request.HttpMethod == "GET")
            {
                var query = context.Request.QueryString;
                var mode = query["hub.mode"];
                var token = query["hub.verify_token"];
                var challenge = query["hub.challenge"];

                if (mode == "subscribe" && token == settings.VerifyToken)
                {
                    context.Response.StatusCode = 200;
                    var buffer = Encoding.UTF8.GetBytes(challenge ?? "");
                    await context.Response.OutputStream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    context.Response.StatusCode = 403;
                }
            }
            else if (context.Request.HttpMethod == "POST")
            {
                using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                var body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

                context.Response.StatusCode = 200;
                var okBuffer = Encoding.UTF8.GetBytes("OK");
                await context.Response.OutputStream.WriteAsync(okBuffer, cancellationToken).ConfigureAwait(false);

                // Process message asynchronously
                _ = ProcessIncomingMessageAsync(body, settings, cancellationToken);
            }
            else
            {
                context.Response.StatusCode = 405;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle WhatsApp webhook request");
            context.Response.StatusCode = 500;
        }
        finally
        {
            context.Response.Close();
        }
    }

    private async Task ProcessIncomingMessageAsync(string jsonPayload, WhatsAppSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonPayload);
            if (!doc.RootElement.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array) return;

            foreach (var entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array) continue;

                foreach (var change in changes.EnumerateArray())
                {
                    if (!change.TryGetProperty("value", out var valueObj)) continue;
                    if (!valueObj.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array) continue;

                    foreach (var msg in messages.EnumerateArray())
                    {
                        await HandleWhatsAppMessageAsync(msg, settings, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing WhatsApp message payload");
        }
    }

    private async Task HandleWhatsAppMessageAsync(JsonElement msgEl, WhatsAppSettings settings, CancellationToken cancellationToken)
    {
        if (!msgEl.TryGetProperty("from", out var fromEl)) return;
        var senderPhoneNumber = fromEl.GetString()!;
        var conversationId = "whatsapp_" + senderPhoneNumber;

        string? text = null;
        if (msgEl.TryGetProperty("text", out var textEl) && textEl.TryGetProperty("body", out var bodyEl))
        {
            text = bodyEl.GetString();
        }

        var trimmed = text?.Trim() ?? "";
        if (trimmed.Equals("/clear", StringComparison.OrdinalIgnoreCase))
        {
            _store.ClearContext(conversationId);
            await SendWhatsAppMessageAsync(senderPhoneNumber, "Context cleared. Let's start fresh!", settings, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var appSettings = _store.GetSettings();
        var providers = _store.ListProviders().Where(p => p.IsEnabled).ToList();
        if (providers.Count == 0)
        {
            await SendWhatsAppMessageAsync(senderPhoneNumber, "Error: No active LLM providers configured in GhostClaw.", settings, cancellationToken).ConfigureAwait(false);
            return;
        }

        var provider = providers.FirstOrDefault(p => p.Id == appSettings.DefaultProviderId) ?? providers.First();
        var model = (provider.Id == appSettings.DefaultProviderId && !string.IsNullOrEmpty(appSettings.DefaultModelId) && provider.Models.Contains(appSettings.DefaultModelId))
            ? appSettings.DefaultModelId
            : (provider.DefaultModel ?? provider.Models.FirstOrDefault());

        if (string.IsNullOrWhiteSpace(model))
        {
            await SendWhatsAppMessageAsync(senderPhoneNumber, "Error: No active model configured on provider.", settings, cancellationToken).ConfigureAwait(false);
            return;
        }

        _store.GetOrCreateConversation(conversationId);

        PipeEnvelope? responseEnvelope = null;
        try
        {
            var chatRequest = new ChatSendRequest(
                ConversationId: conversationId,
                ProviderId: provider.Id,
                Model: model,
                Content: text,
                WhisperMode: false,
                Verbosity: "Normal",
                Attachments: null,
                AgentMode: true
            );

            var requestEnvelope = PipeEnvelope.Request("chat.send", chatRequest);
            responseEnvelope = await _router.HandleAsync(requestEnvelope, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to route WhatsApp message");
            await SendWhatsAppMessageAsync(senderPhoneNumber, $"Error: {ex.Message}", settings, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (responseEnvelope == null)
        {
            await SendWhatsAppMessageAsync(senderPhoneNumber, "Error: Request failed or returned null response.", settings, cancellationToken).ConfigureAwait(false);
        }
        else if (responseEnvelope.Type == "error")
        {
            await SendWhatsAppMessageAsync(senderPhoneNumber, $"Error: {responseEnvelope.Error}", settings, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var result = responseEnvelope.ReadPayload<ChatSendResult>();
            if (result != null)
            {
                if (result.Error != null)
                {
                    await SendWhatsAppMessageAsync(senderPhoneNumber, $"Error: {result.Error}", settings, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var replyText = result.AssistantMessage.Content;
                    await SendWhatsAppMessageAsync(senderPhoneNumber, replyText, settings, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task SendWhatsAppMessageAsync(string to, string text, WhatsAppSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://graph.facebook.com/v17.0/{settings.PhoneNumberId}/messages";
            var payload = new JsonObject
            {
                ["messaging_product"] = "whatsapp",
                ["recipient_type"] = "individual",
                ["to"] = to,
                ["type"] = "text",
                ["text"] = new JsonObject
                {
                    ["preview_url"] = false,
                    ["body"] = text
                }
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.AccessToken);
            request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogWarning("WhatsApp sendMessage failed: {StatusCode}, Body: {Body}", response.StatusCode, body);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send WhatsApp message to {To}", to);
        }
    }
}
