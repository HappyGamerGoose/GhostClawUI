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

    private async Task<string?> DownloadWhatsAppMediaAsync(string mediaId, string fileNameHint, WhatsAppSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://graph.facebook.com/v17.0/{mediaId}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.AccessToken);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("url", out var urlEl)) return null;
            
            var downloadUrl = urlEl.GetString();
            if (string.IsNullOrEmpty(downloadUrl)) return null;

            using var dlReq = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
            dlReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.AccessToken);
            using var dlRes = await _httpClient.SendAsync(dlReq, cancellationToken).ConfigureAwait(false);
            if (!dlRes.IsSuccessStatusCode) return null;

            var tempDir = Path.Combine(Path.GetTempPath(), "GhostClawWhatsAppAttachments");
            Directory.CreateDirectory(tempDir);
            var localPath = Path.Combine(tempDir, fileNameHint);

            using var fileStream = File.Create(localPath);
            using var downloadStream = await dlRes.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await downloadStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
            return localPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download WhatsApp media {MediaId}", mediaId);
            return null;
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

        var attachments = new List<ChatAttachment>();
        
        // Parse incoming media
        var mediaTypes = new[] { "image", "audio", "video", "document" };
        foreach (var mType in mediaTypes)
        {
            if (msgEl.TryGetProperty(mType, out var mediaEl) && mediaEl.ValueKind == JsonValueKind.Object)
            {
                if (mediaEl.TryGetProperty("id", out var idEl))
                {
                    var mediaId = idEl.GetString();
                    var mimeType = mediaEl.TryGetProperty("mime_type", out var mimeEl) ? mimeEl.GetString() : "application/octet-stream";
                    var ext = mType switch { "image" => ".jpg", "audio" => ".ogg", "video" => ".mp4", "document" => ".bin", _ => ".bin" };
                    if (mediaEl.TryGetProperty("filename", out var fnEl)) ext = fnEl.GetString() ?? ext;

                    var fileName = $"wa_{mType}_{Guid.NewGuid().ToString("N")[..8]}{ext}";
                    
                    if (!string.IsNullOrEmpty(mediaId))
                    {
                        var localPath = await DownloadWhatsAppMediaAsync(mediaId, fileName, settings, cancellationToken).ConfigureAwait(false);
                        if (localPath != null)
                        {
                            var size = new FileInfo(localPath).Length;
                            string? textPreview = null;
                            if (mType == "document" || mimeType?.Contains("text") == true)
                            {
                                textPreview = await FileTextExtractor.ReadTextPreviewAsync(localPath, size, 100000).ConfigureAwait(false);
                            }
                            attachments.Add(new ChatAttachment(
                                Name: fileName,
                                Path: localPath,
                                ContentType: mimeType ?? "application/octet-stream",
                                SizeBytes: size,
                                TextPreview: textPreview,
                                DataUri: null
                            ));
                        }
                    }
                }
            }
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
            if (attachments.Count > 0)
            {
                text = $"Analyze the attached file{(attachments.Count == 1 ? "" : "s")}.";
            }
            else
            {
                return;
            }
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

        // Send Thinking message
        var thinkingMsgId = await SendWhatsAppMessageAsync(senderPhoneNumber, "Thinking...", settings, cancellationToken).ConfigureAwait(false);

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
                Attachments: attachments.Count > 0 ? attachments : null,
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

                    // Send generated attachments
                    var replyAttachments = ReadAttachments(result.AssistantMessage.Metadata);
                    foreach (var att in replyAttachments)
                    {
                        if (!string.IsNullOrWhiteSpace(att.Path) && File.Exists(att.Path))
                        {
                            await SendWhatsAppMessageAsync(senderPhoneNumber, $"Sending generated file: {att.Name}", settings, cancellationToken).ConfigureAwait(false);
                            await UploadAndSendWhatsAppMediaAsync(senderPhoneNumber, att.Path, att.Name, settings, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
            }
        }
    }

    private static IReadOnlyList<ChatAttachment> ReadAttachments(JsonNode? metadata)
    {
        try
        {
            return metadata?["attachments"]?.Deserialize<IReadOnlyList<ChatAttachment>>(PipeJson.Options) ?? Array.Empty<ChatAttachment>();
        }
        catch
        {
            return Array.Empty<ChatAttachment>();
        }
    }

    private async Task<string?> SendWhatsAppMessageAsync(string to, string text, WhatsAppSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            var formattedText = SocialMessageFormatter.ToWhatsAppMarkdown(text);
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
                    ["body"] = formattedText
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
            else
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array && msgs.GetArrayLength() > 0)
                {
                    return msgs[0].GetProperty("id").GetString();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send WhatsApp message to {To}", to);
        }
        return null;
    }

    private async Task UploadAndSendWhatsAppMediaAsync(string to, string filePath, string fileName, WhatsAppSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            // Upload
            var uploadUrl = $"https://graph.facebook.com/v17.0/{settings.PhoneNumberId}/media";
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent("whatsapp"), "messaging_product");
            var fileBytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
            using var fileContent = new ByteArrayContent(fileBytes);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            form.Add(fileContent, "file", fileName);

            using var uploadReq = new HttpRequestMessage(HttpMethod.Post, uploadUrl) { Content = form };
            uploadReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.AccessToken);
            using var uploadRes = await _httpClient.SendAsync(uploadReq, cancellationToken).ConfigureAwait(false);
            if (!uploadRes.IsSuccessStatusCode)
            {
                 var err = await uploadRes.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                 _logger.LogWarning("WhatsApp media upload failed: {Err}", err);
                 return;
            }
            var json = await uploadRes.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("id", out var idEl)) return;
            var mediaId = idEl.GetString();

            // Send
            var sendUrl = $"https://graph.facebook.com/v17.0/{settings.PhoneNumberId}/messages";
            var payload = new JsonObject
            {
                ["messaging_product"] = "whatsapp",
                ["recipient_type"] = "individual",
                ["to"] = to,
                ["type"] = "document",
                ["document"] = new JsonObject
                {
                    ["id"] = mediaId,
                    ["filename"] = fileName
                }
            };

            using var sendReq = new HttpRequestMessage(HttpMethod.Post, sendUrl);
            sendReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.AccessToken);
            sendReq.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            await _httpClient.SendAsync(sendReq, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send WhatsApp document to {To}", to);
        }
    }
}
