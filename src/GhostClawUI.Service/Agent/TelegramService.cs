using System;
using System.Collections.Generic;
using System.Linq;
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

internal sealed class TelegramService : BackgroundService
{
    private readonly EncryptedStore _store;
    private readonly CommandRouter _router;
    private readonly HttpClient _httpClient;
    private readonly ILogger<TelegramService> _logger;

    public TelegramService(
        EncryptedStore store,
        CommandRouter router,
        HttpClient httpClient,
        ILogger<TelegramService> logger)
    {
        _store = store;
        _router = router;
        _httpClient = httpClient;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Telegram Bot background listener starting...");
        long lastUpdateId = 0;
        bool commandsRegistered = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var settings = _store.GetTelegramSettings();
                if (settings == null || !settings.IsEnabled || string.IsNullOrWhiteSpace(settings.BotToken))
                {
                    commandsRegistered = false;
                    await Task.Delay(5000, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (!commandsRegistered)
                {
                    await RegisterBotCommandsAsync(settings.BotToken, stoppingToken).ConfigureAwait(false);
                    commandsRegistered = true;
                }

                // Poll updates
                var url = $"https://api.telegram.org/bot{settings.BotToken}/getUpdates?offset={lastUpdateId + 1}&timeout=8";
                using var response = await _httpClient.GetAsync(url, stoppingToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    await Task.Delay(5000, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var content = await response.Content.ReadAsStringAsync(stoppingToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(content);
                if (doc.RootElement.TryGetProperty("ok", out var okEl) && okEl.GetBoolean())
                {
                    var resultEl = doc.RootElement.GetProperty("result");
                    foreach (var update in resultEl.EnumerateArray())
                    {
                        lastUpdateId = update.GetProperty("update_id").GetInt64();

                        if (update.TryGetProperty("message", out var msgEl))
                        {
                            await HandleTelegramMessageAsync(msgEl, settings, stoppingToken).ConfigureAwait(false);
                        }
                        else if (update.TryGetProperty("edited_message", out var editedMsgEl))
                        {
                            await HandleTelegramMessageAsync(editedMsgEl, settings, stoppingToken).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Telegram bot polling loop");
                await Task.Delay(10000, stoppingToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("Telegram Bot background listener stopped.");
    }

    private async Task<string?> DownloadTelegramFileAsync(string fileId, string botToken, string fileNameHint, CancellationToken cancellationToken)
    {
        try
        {
            var getFileUrl = $"https://api.telegram.org/bot{botToken}/getFile?file_id={fileId}";
            using var getFileResponse = await _httpClient.GetAsync(getFileUrl, cancellationToken).ConfigureAwait(false);
            if (!getFileResponse.IsSuccessStatusCode)
            {
                return null;
            }

            var responseContent = await getFileResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(responseContent);
            if (doc.RootElement.TryGetProperty("ok", out var okEl) && okEl.GetBoolean())
            {
                var filePath = doc.RootElement.GetProperty("result").GetProperty("file_path").GetString();
                if (string.IsNullOrEmpty(filePath)) return null;

                var downloadUrl = $"https://api.telegram.org/file/bot{botToken}/{filePath}";
                var tempDir = Path.Combine(Path.GetTempPath(), "GhostClawTelegramAttachments");
                Directory.CreateDirectory(tempDir);
                var localPath = Path.Combine(tempDir, fileNameHint);

                using var fileStream = File.Create(localPath);
                using var downloadStream = await _httpClient.GetStreamAsync(downloadUrl, cancellationToken).ConfigureAwait(false);
                await downloadStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
                return localPath;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download Telegram file {FileId}", fileId);
        }
        return null;
    }

    private async Task SendTelegramDocumentAsync(string chatId, string filePath, string fileName, string token, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://api.telegram.org/bot{token}/sendDocument";
            using var form = new MultipartFormDataContent();
            using var chatIdContent = new StringContent(chatId);
            form.Add(chatIdContent, "chat_id");

            var fileBytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
            using var fileContent = new ByteArrayContent(fileBytes);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            form.Add(fileContent, "document", fileName);

            using var response = await _httpClient.PostAsync(url, form, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogWarning("Telegram sendDocument failed: {StatusCode}, Body: {Body}", response.StatusCode, body);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Telegram document {FileName} to ChatId {ChatId}", fileName, chatId);
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

    private async Task HandleTelegramMessageAsync(JsonElement msgEl, TelegramSettings settings, CancellationToken cancellationToken)
    {
        string? chatId = null;
        try
        {
            if (!msgEl.TryGetProperty("chat", out var chatEl)) return;
            chatId = chatEl.GetProperty("id").GetInt64().ToString();

            // Security Check: Unauthorized chats are ignored
            if (!string.IsNullOrWhiteSpace(settings.ChatId))
            {
                var allowedChats = settings.ChatId.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
                if (allowedChats.Count > 0 && !allowedChats.Contains(chatId))
                {
                    _logger.LogWarning("Telegram message from unauthorized chat ID: {ChatId}", chatId);
                    return;
                }
            }

            // Extract text/caption
            string? text = null;
            if (msgEl.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
            {
                text = textEl.GetString();
            }
            else if (msgEl.TryGetProperty("caption", out var captionEl) && captionEl.ValueKind == JsonValueKind.String)
            {
                text = captionEl.GetString();
            }

            // Detect and download attachments
            var attachments = new List<ChatAttachment>();

            // Document
            if (msgEl.TryGetProperty("document", out var docEl) && docEl.ValueKind == JsonValueKind.Object)
            {
                var fileId = docEl.GetProperty("file_id").GetString();
                var fileName = docEl.TryGetProperty("file_name", out var fnEl) ? fnEl.GetString() : $"telegram_doc_{Guid.NewGuid().ToString("N")[..8]}";
                var mimeType = docEl.TryGetProperty("mime_type", out var mimeEl) ? mimeEl.GetString() : "application/octet-stream";
                var size = docEl.TryGetProperty("file_size", out var sizeEl) ? sizeEl.GetInt64() : 0;

                if (size > 20 * 1024 * 1024)
                {
                    await SendTelegramMessageAsync(chatId, $"⚠️ Attachment {fileName} ignored: exceeds 20MB Telegram bot limit.", settings.BotToken, cancellationToken).ConfigureAwait(false);
                }
                else if (!string.IsNullOrEmpty(fileId))
                {
                    var localPath = await DownloadTelegramFileAsync(fileId, settings.BotToken, fileName ?? "file", cancellationToken).ConfigureAwait(false);
                    if (localPath != null)
                    {
                        var textPreview = await FileTextExtractor.ReadTextPreviewAsync(localPath, size, 100000).ConfigureAwait(false);
                        attachments.Add(new ChatAttachment(
                            Name: fileName ?? "file",
                            Path: localPath,
                            ContentType: mimeType ?? "application/octet-stream",
                            SizeBytes: size,
                            TextPreview: textPreview,
                            DataUri: null
                        ));
                    }
                }
            }

            // Photo (multiple sizes, pick largest)
            if (msgEl.TryGetProperty("photo", out var photoEl) && photoEl.ValueKind == JsonValueKind.Array && photoEl.GetArrayLength() > 0)
            {
                var photoArray = photoEl.EnumerateArray().ToList();
                var largestPhoto = photoArray.Last();
                var fileId = largestPhoto.GetProperty("file_id").GetString();
                var size = largestPhoto.TryGetProperty("file_size", out var sizeEl) ? sizeEl.GetInt64() : 0;

                if (size > 20 * 1024 * 1024)
                {
                    await SendTelegramMessageAsync(chatId, $"⚠️ Photo ignored: exceeds 20MB Telegram bot limit.", settings.BotToken, cancellationToken).ConfigureAwait(false);
                }
                else if (!string.IsNullOrEmpty(fileId))
                {
                    var fileName = $"telegram_image_{Guid.NewGuid().ToString("N")[..8]}.jpg";
                    var localPath = await DownloadTelegramFileAsync(fileId, settings.BotToken, fileName, cancellationToken).ConfigureAwait(false);
                    if (localPath != null)
                    {
                        attachments.Add(new ChatAttachment(
                            Name: fileName,
                            Path: localPath,
                            ContentType: "image/jpeg",
                            SizeBytes: size,
                            TextPreview: null,
                            DataUri: null
                        ));
                    }
                }
            }

            // Voice/Audio
            if (msgEl.TryGetProperty("voice", out var voiceEl) && voiceEl.ValueKind == JsonValueKind.Object)
            {
                var fileId = voiceEl.GetProperty("file_id").GetString();
                var mimeType = voiceEl.TryGetProperty("mime_type", out var mimeEl) ? mimeEl.GetString() : "audio/ogg";
                var size = voiceEl.TryGetProperty("file_size", out var sizeEl) ? sizeEl.GetInt64() : 0;

                if (size > 20 * 1024 * 1024)
                {
                    await SendTelegramMessageAsync(chatId, $"⚠️ Voice note ignored: exceeds 20MB Telegram bot limit.", settings.BotToken, cancellationToken).ConfigureAwait(false);
                }
                else if (!string.IsNullOrEmpty(fileId))
                {
                    var localPath = await DownloadTelegramFileAsync(fileId, settings.BotToken, $"telegram_voice_{Guid.NewGuid().ToString("N")[..8]}.ogg", cancellationToken).ConfigureAwait(false);
                    if (localPath != null) attachments.Add(new ChatAttachment("Voice Note.ogg", localPath, mimeType ?? "audio/ogg", size, null, null));
                }
            }
            else if (msgEl.TryGetProperty("audio", out var audioEl) && audioEl.ValueKind == JsonValueKind.Object)
            {
                var fileId = audioEl.GetProperty("file_id").GetString();
                var fileName = audioEl.TryGetProperty("file_name", out var fnEl) ? fnEl.GetString() : $"telegram_audio_{Guid.NewGuid().ToString("N")[..8]}.mp3";
                var mimeType = audioEl.TryGetProperty("mime_type", out var mimeEl) ? mimeEl.GetString() : "audio/mpeg";
                var size = audioEl.TryGetProperty("file_size", out var sizeEl) ? sizeEl.GetInt64() : 0;

                if (size > 20 * 1024 * 1024)
                {
                    await SendTelegramMessageAsync(chatId, $"⚠️ Audio file ignored: exceeds 20MB Telegram bot limit.", settings.BotToken, cancellationToken).ConfigureAwait(false);
                }
                else if (!string.IsNullOrEmpty(fileId))
                {
                    var localPath = await DownloadTelegramFileAsync(fileId, settings.BotToken, fileName ?? "audio.mp3", cancellationToken).ConfigureAwait(false);
                    if (localPath != null) attachments.Add(new ChatAttachment(fileName ?? "Audio File", localPath, mimeType ?? "audio/mpeg", size, null, null));
                }
            }

            // Video
            if (msgEl.TryGetProperty("video", out var videoEl) && videoEl.ValueKind == JsonValueKind.Object)
            {
                var fileId = videoEl.GetProperty("file_id").GetString();
                var fileName = videoEl.TryGetProperty("file_name", out var fnEl) ? fnEl.GetString() : $"telegram_video_{Guid.NewGuid().ToString("N")[..8]}.mp4";
                var mimeType = videoEl.TryGetProperty("mime_type", out var mimeEl) ? mimeEl.GetString() : "video/mp4";
                var size = videoEl.TryGetProperty("file_size", out var sizeEl) ? sizeEl.GetInt64() : 0;

                if (size > 20 * 1024 * 1024)
                {
                    await SendTelegramMessageAsync(chatId, $"⚠️ Video ignored: exceeds 20MB Telegram bot limit.", settings.BotToken, cancellationToken).ConfigureAwait(false);
                }
                else if (!string.IsNullOrEmpty(fileId))
                {
                    var localPath = await DownloadTelegramFileAsync(fileId, settings.BotToken, fileName ?? "video.mp4", cancellationToken).ConfigureAwait(false);
                    if (localPath != null) attachments.Add(new ChatAttachment(fileName ?? "Video File", localPath, mimeType ?? "video/mp4", size, null, null));
                }
            }

            // Handle commands
            if (text != null)
            {
                var trimmed = text.Trim();
                if (trimmed.Equals("/start", StringComparison.OrdinalIgnoreCase))
                {
                    await SendTelegramMessageAsync(chatId, "Hello! I am GhostClaw. Send me any instruction, task, or file upload (PDFs, images, audio, video, documents), and I'll process it via active agents with tool access.", settings.BotToken, cancellationToken).ConfigureAwait(false);
                    return;
                }
                if (trimmed.Equals("/clear", StringComparison.OrdinalIgnoreCase))
                {
                    var cid = "telegram_" + chatId;
                    _store.ClearContext(cid);
                    await SendTelegramMessageAsync(chatId, "Context cleared. Let's start fresh!", settings.BotToken, cancellationToken).ConfigureAwait(false);
                    return;
                }
            }

            // If we have attachments but no text, assign a default description
            if (string.IsNullOrWhiteSpace(text))
            {
                if (attachments.Count > 0)
                {
                    text = $"Analyze the attached file{(attachments.Count == 1 ? "" : "s")}.";
                }
                else
                {
                    return; // Ignore empty text/no attachments
                }
            }

            // Find active provider and model
            var appSettings = _store.GetSettings();
            var providers = _store.ListProviders().Where(p => p.IsEnabled).ToList();
            if (providers.Count == 0)
            {
                await SendTelegramMessageAsync(chatId, "Error: No active LLM providers configured in GhostClaw.", settings.BotToken, cancellationToken).ConfigureAwait(false);
                return;
            }

            var provider = providers.FirstOrDefault(p => p.Id == appSettings.DefaultProviderId) ?? providers.First();
            var model = (provider.Id == appSettings.DefaultProviderId && !string.IsNullOrEmpty(appSettings.DefaultModelId) && provider.Models.Contains(appSettings.DefaultModelId))
                ? appSettings.DefaultModelId
                : (provider.DefaultModel ?? provider.Models.FirstOrDefault());
            if (string.IsNullOrWhiteSpace(model))
            {
                await SendTelegramMessageAsync(chatId, "Error: No active model configured on provider.", settings.BotToken, cancellationToken).ConfigureAwait(false);
                return;
            }

            // Send "Thinking..." notification and chat action
            _ = SendChatActionAsync(chatId, "typing", settings.BotToken, cancellationToken);
            var thinkingMsgId = await SendTelegramMessageAsync(chatId, "Thinking...", settings.BotToken, cancellationToken).ConfigureAwait(false);

            var conversationId = "telegram_" + chatId;
            _store.GetOrCreateConversation(conversationId);

            Action<string, AgentTraceCard> traceHandler = (cid, trace) =>
            {
                if (cid == conversationId && thinkingMsgId.HasValue)
                {
                    var detail = trace.Detail ?? "";
                    if (detail.Length > 250) detail = detail.Substring(0, 250) + "...";
                    _ = EditTelegramMessageAsync(chatId, thinkingMsgId.Value, $"GhostClaw Agent 🧠\n\n📌 {trace.Title}\n{detail}", settings.BotToken, CancellationToken.None);
                }
            };

            PipeEnvelope? responseEnvelope = null;
            _router.OnAgentTraceEmitted += traceHandler;
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
                    AgentMode: true // Use Agent/Autonomous mode by default
                );

                var requestEnvelope = PipeEnvelope.Request("chat.send", chatRequest);
                responseEnvelope = await _router.HandleAsync(requestEnvelope, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _router.OnAgentTraceEmitted -= traceHandler;
            }

            if (responseEnvelope == null)
            {
                await SendTelegramMessageAsync(chatId, "Error: Request failed or returned null response.", settings.BotToken, cancellationToken).ConfigureAwait(false);
            }
            else if (responseEnvelope.Type == "error")
            {
                await SendTelegramMessageAsync(chatId, $"Error: {responseEnvelope.Error}", settings.BotToken, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var result = responseEnvelope.ReadPayload<ChatSendResult>();
                if (result != null)
                {
                    if (result.Error != null)
                    {
                        await SendTelegramMessageAsync(chatId, $"Error: {result.Error}", settings.BotToken, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        var replyText = result.AssistantMessage.Content;
                        await SendTelegramMessageAsync(chatId, replyText, settings.BotToken, cancellationToken).ConfigureAwait(false);

                        // Upload/send any generated files back to Telegram
                        var replyAttachments = ReadAttachments(result.AssistantMessage.Metadata);
                        foreach (var att in replyAttachments)
                        {
                            if (!string.IsNullOrWhiteSpace(att.Path) && File.Exists(att.Path))
                            {
                                _ = SendChatActionAsync(chatId, "upload_document", settings.BotToken, cancellationToken);
                                await SendTelegramMessageAsync(chatId, $"Sending generated file: {att.Name}", settings.BotToken, cancellationToken).ConfigureAwait(false);
                                await SendTelegramDocumentAsync(chatId, att.Path, att.Name, settings.BotToken, cancellationToken).ConfigureAwait(false);
                            }
                        }
                    }
                }
                else
                {
                    await SendTelegramMessageAsync(chatId, "Received an empty response from the agent.", settings.BotToken, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling Telegram message");
            if (chatId != null)
            {
                try
                {
                    await SendTelegramMessageAsync(chatId, $"Exception: {ex.Message}", settings.BotToken, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception sendEx) { _logger.LogWarning(sendEx, "Failed to send error back to telegram."); }
            }
        }
    }

    private async Task<long?> SendTelegramMessageAsync(string chatId, string text, string token, CancellationToken cancellationToken)
    {
        long? lastMessageId = null;
        try
        {
            var url = $"https://api.telegram.org/bot{token}/sendMessage";
            int chunkSize = 4000;

            var htmlText = ConvertMarkdownToHtml(text);
            var chunks = ChunkHtmlSafely(htmlText, chunkSize);

            foreach (var chunk in chunks)
            {
                var payload = new JsonObject
                {
                    ["chat_id"] = chatId,
                    ["text"] = chunk,
                    ["parse_mode"] = "HTML"
                };

                using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    // Retry without Markdown in case of unclosed syntax errors
                    payload.Remove("parse_mode");
                    using var retryContent = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
                    response = await _httpClient.PostAsync(url, retryContent, cancellationToken).ConfigureAwait(false);
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Telegram sendMessage returned non-success status code: {StatusCode}", response.StatusCode);
                }
                else
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("result", out var resultEl) && resultEl.TryGetProperty("message_id", out var msgIdEl))
                    {
                        lastMessageId = msgIdEl.GetInt64();
                    }
                }

                response.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Telegram message to ChatId {ChatId}", chatId);
        }
        return lastMessageId;
    }

    private static List<string> ChunkHtmlSafely(string html, int maxLength)
    {
        var chunks = new List<string>();
        int i = 0;
        while (i < html.Length)
        {
            if (html.Length - i <= maxLength)
            {
                chunks.Add(html.Substring(i));
                break;
            }

            int splitIndex = i + maxLength;
            // Seek backward for a safe newline
            int safeSplit = html.LastIndexOf('\n', splitIndex, maxLength);
            if (safeSplit > i + (maxLength / 2)) // Found a newline in the reasonable past half
            {
                splitIndex = safeSplit;
            }

            chunks.Add(html.Substring(i, splitIndex - i));
            i = splitIndex;
        }
        return chunks;
    }

    private async Task RegisterBotCommandsAsync(string token, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://api.telegram.org/bot{token}/setMyCommands";
            var payload = new JsonObject
            {
                ["commands"] = new JsonArray
                {
                    new JsonObject { ["command"] = "start", ["description"] = "Start the bot" },
                    new JsonObject { ["command"] = "clear", ["description"] = "Clear conversational context" }
                }
            };
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            await _httpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register Telegram bot commands");
        }
    }
    private async Task EditTelegramMessageAsync(string chatId, long messageId, string text, string token, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://api.telegram.org/bot{token}/editMessageText";
            var payload = new JsonObject
            {
                ["chat_id"] = chatId,
                ["message_id"] = messageId,
                ["text"] = text
            };

            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to edit Telegram message to ChatId {ChatId}", chatId);
        }
    }

    private async Task SendChatActionAsync(string chatId, string action, string token, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://api.telegram.org/bot{token}/sendChatAction";
            var payload = new JsonObject
            {
                ["chat_id"] = chatId,
                ["action"] = action
            };
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            await _httpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send chat action {Action} to ChatId {ChatId}", action, chatId);
        }
    }

    private static string ConvertMarkdownToHtml(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return "";

        var html = markdown
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");

        // Convert bold
        html = System.Text.RegularExpressions.Regex.Replace(html, @"\*\*(.+?)\*\*", "<b>$1</b>");
        // Convert italic
        html = System.Text.RegularExpressions.Regex.Replace(html, @"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)", "<i>$1</i>");
        // Convert inline code
        html = System.Text.RegularExpressions.Regex.Replace(html, @"`([^`]+)`", "<code>$1</code>");
        // Convert code blocks
        html = System.Text.RegularExpressions.Regex.Replace(html, @"```\w*\n([\s\S]*?)```", "<pre><code>$1</code></pre>");

        return html;
    }
}
