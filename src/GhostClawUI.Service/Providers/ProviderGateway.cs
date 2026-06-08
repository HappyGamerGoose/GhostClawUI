using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GhostClawUI.Shared;

namespace GhostClawUI.Service.Providers;

internal sealed class ProviderGateway
{
    private readonly HttpClient _httpClient;
    private sealed record RequestVariant(string Name, string Endpoint, JsonObject Payload, bool IsResponses);

    public ProviderGateway(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<ProviderValidationResult> ValidateAsync(ProviderValidationRequest request, CancellationToken cancellationToken)
    {
        var manual = CleanModels(request.ManualModels);
        if (!Uri.TryCreate(request.BaseUrl.TrimEnd('/') + "/models", UriKind.Absolute, out var modelsUri))
        {
            return manual.Count > 0
                ? new ProviderValidationResult(true, manual, "Model endpoint was invalid, using manual models.", true)
                : new ProviderValidationResult(false, Array.Empty<string>(), "Base URL is not a valid absolute URL.", false);
        }

        try
        {
            var isAnthropic = IsAnthropicProvider(request.BaseUrl, request.Name, "");
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, modelsUri);
            ApplyAuth(httpRequest, request.ApiKey, isAnthropic);
            using var response = await _httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return manual.Count > 0
                    ? new ProviderValidationResult(true, manual, $"Model endpoint returned {(int)response.StatusCode}; using manual models.", true)
                    : new ProviderValidationResult(false, Array.Empty<string>(), HumanizeProviderError(response.StatusCode, body, "models"), false);
            }

            var models = ExtractModels(body);
            if (models.Count > 0)
            {
                return new ProviderValidationResult(true, models, $"Fetched {models.Count} models.", false);
            }

            return manual.Count > 0
                ? new ProviderValidationResult(true, manual, "Model endpoint did not include model IDs; using manual models.", true)
                : new ProviderValidationResult(false, Array.Empty<string>(), "Model endpoint responded, but no model IDs were found.", false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return manual.Count > 0
                ? new ProviderValidationResult(true, manual, $"Could not reach /models ({ex.Message}); using manual models.", true)
                : new ProviderValidationResult(false, Array.Empty<string>(), $"Could not reach /models: {ex.Message}", false);
        }
    }

    public async Task<(string? Content, string? Error)> SendChatAsync(
        ProviderProfile provider,
        string apiKey,
        string model,
        string userMessage,
        IReadOnlyList<ChatMessage> history,
        IReadOnlyList<MemoryFact> facts,
        IReadOnlyList<ChatAttachment> attachments,
        string verbosity,
        CancellationToken cancellationToken)
    {
        var requests = BuildRequestVariants(provider, model, userMessage, history, facts, attachments, verbosity);
        string? lastError = null;

        foreach (var variant in requests)
        {
            var delay = TimeSpan.FromMilliseconds(600);
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    var isAnthropic = IsAnthropicProvider(provider.BaseUrl, provider.Name, provider.Id);
                    using var request = new HttpRequestMessage(HttpMethod.Post, variant.Endpoint);
                    ApplyAuth(request, apiKey, isAnthropic);
                    request.Content = new StringContent(variant.Payload.ToJsonString(PipeJson.Options), Encoding.UTF8, "application/json");
                    using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                    var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        lastError = HumanizeProviderError(response.StatusCode, body, variant.Name);
                        if ((int)response.StatusCode >= 500 && attempt < 3)
                        {
                            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                            delay += delay;
                            continue;
                        }

                        if (ShouldTryNextPayload(response.StatusCode, body, variant.Name))
                        {
                            break;
                        }

                        return (null, lastError);
                    }

                    return (variant.IsResponses ? ExtractResponsesContent(body) : ExtractAssistantContent(body), null);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }

                    lastError = ex is TaskCanceledException
                        ? $"The model request timed out while using the {variant.Name} payload. Test this model in Providers, or choose a faster chat-capable model."
                        : $"Provider connection failed while using {variant.Name} payload: {ex.Message}";
                    if (attempt < 3)
                    {
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                        delay += delay;
                        continue;
                    }
                }
            }
        }

        return (null, lastError ?? "Provider call failed after retries.");
    }

    public async Task<CommandResult> TestModelAsync(ProviderModelTestRequest request, CancellationToken cancellationToken)
    {
        var provider = new ProviderProfile(
            "test",
            request.Name,
            request.BaseUrl,
            new[] { request.Model },
            request.Model,
            true,
            DateTimeOffset.UtcNow);
        var (content, error) = await SendChatAsync(
            provider,
            request.ApiKey ?? string.Empty,
            request.Model,
            "Reply with exactly: OK",
            Array.Empty<ChatMessage>(),
            Array.Empty<MemoryFact>(),
            Array.Empty<ChatAttachment>(),
            "Minimal",
            cancellationToken).ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(error)
            ? new CommandResult(true, string.IsNullOrWhiteSpace(content) ? "Model responded." : $"Model responded: {TrimBody(content)}")
            : new CommandResult(false, error);
    }

    private static string BuildSystemPrompt(IReadOnlyList<MemoryFact> facts, string verbosity)
    {
        var builder = new StringBuilder();
        var localNow = DateTimeOffset.Now;
        var timeZone = TimeZoneInfo.Local;
        builder.AppendLine("You are GhostClaw, a Windows-native autonomous agent. Be direct, useful, and tool-aware.");
        builder.AppendLine("GREETING RULE: If the user just says hello, keep your greeting simple (e.g., 'I'm GhostClaw. How can I help?'). If the user asks about your capabilities, explain what you can do clearly. Never repeat the same greeting multiple times in a row, and do not include your local time, OS version, or model ID unless explicitly asked.");
        builder.AppendLine("Format replies in clean Markdown. Use fenced code blocks for code and $...$ or $$...$$ for math. Keep image/file references explicit and readable.");
        builder.AppendLine("Never expose raw tool-call JSON, XML tags, function-call arguments, MCP request payloads, or assistant-to-tool frames to the user. If tool activity matters, describe the outcome in natural language.");
        builder.AppendLine("For current or recent information, use available search/browser tools when the agent runtime exposes them. If no search tool is available, say what needs to be connected instead of guessing.");

        // System-level file generation instructions to guarantee actual creation and avoid path/attachment hallucinations
        builder.AppendLine("=== CRITICAL FILE CREATION RULE ===");
        builder.AppendLine("If the user asks you to create, design, or attach a file (such as a PowerPoint presentation, Excel spreadsheet, Word document, PDF, zip, image, text, or CSV), you MUST generate it by writing the complete, self-contained Python script to create that file, and wrap it inside a ```python ... ``` code block. NEVER say you have created, attached, or saved a file unless your exact response contains the ```python ... ``` block that actually generates it!");
        builder.AppendLine("You MUST write and output the Python code block. Example of the exact output format you must follow:");
        builder.AppendLine("```python");
        builder.AppendLine("from pptx import Presentation");
        builder.AppendLine("prs = Presentation()");
        builder.AppendLine("# code to create slides...");
        builder.AppendLine("prs.save('Cleanliness.pptx')");
        builder.AppendLine("```");
        builder.AppendLine("The host platform automatically executes your code block in the background, attaches the created file to your chat bubble, and presents a native download link card to the user. Do not make excuses; write the complete python code block to construct the requested file.");
        builder.AppendLine("CRITICAL PYTHON CODE QUALITY INSTRUCTIONS:");
        builder.AppendLine("1. Never include un-commented decorative headers, text lines, or sections (such as '── Colour palette ──' or box drawings) inside python blocks. Every line in a ```python block must be valid, executable Python syntax. Comment out decorative dividers using '#'.");
        builder.AppendLine("2. For ReportLab PDF generation: ALWAYS import standard alignment constants with underscores (e.g. TA_CENTER, TA_JUSTIFY, TA_LEFT, TA_RIGHT) from 'reportlab.lib.enums'. NEVER write TACENTER, TAJUSTIFY, TALEFT, or TARIGHT without underscores.");
        builder.AppendLine("3. For python-pptx presentations: NEVER use non-existent methods like '.fit_text()' or '.autofit()' on text frames or shapes. Use standard font sizing and word wrap features.");
        builder.AppendLine("===================================");

        builder.AppendLine("=== DEEP INSPECTION & BRAINSTORMING ===");
        builder.AppendLine("When faced with a complex task or error, you MUST wrap your thought process in a <think>...</think> block. Inside this block, perform Deep Inspection (break down the problem, examine the root cause of errors, analyze data context) and Brainstorming (list out at least 2-3 distinct approaches, weigh their pros and cons, and choose the most robust one) before you output your final answer or tool payload.");
        builder.AppendLine("This guarantees high-quality, agentic reasoning.");
        builder.AppendLine("=======================================");

        builder.AppendLine($"User-selected verbosity: {verbosity}.");
        if (facts.Count > 0)
        {
            builder.AppendLine("Relevant persistent memory:");
            foreach (var fact in facts)
            {
                builder.AppendLine($"- {fact.Summary}: {fact.Content}");
            }
        }

        return builder.ToString();
    }

    private static void ApplyAuth(HttpRequestMessage request, string? apiKey, bool isAnthropic)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            if (isAnthropic)
            {
                request.Headers.Add("x-api-key", apiKey);
                request.Headers.Add("anthropic-version", "2023-06-01");
            }
            else
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }
        }
    }

    private static string BuildChatEndpoint(string baseUrl, bool isAnthropic)
    {
        var normalized = baseUrl.Trim().TrimEnd('/');
        if (normalized.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) || normalized.EndsWith("/v1/messages", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        if (normalized.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^"/models".Length].TrimEnd('/');
        }

        if (normalized.Equals("https://logfare.ai", StringComparison.OrdinalIgnoreCase))
        {
            normalized += "/v1";
        }

        return isAnthropic ? normalized + "/v1/messages" : normalized + "/chat/completions";
    }

    private static string BuildResponsesEndpoint(string baseUrl)
    {
        var normalized = baseUrl.Trim().TrimEnd('/');
        if (normalized.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^"/chat/completions".Length].TrimEnd('/');
        }

        if (normalized.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^"/models".Length].TrimEnd('/');
        }

        if (normalized.Equals("https://logfare.ai", StringComparison.OrdinalIgnoreCase))
        {
            normalized += "/v1";
        }

        return normalized + "/responses";
    }

    private static bool IsGoogleProvider(string baseUrl, string name, string id)
    {
        var url = baseUrl ?? "";
        var nm = name ?? "";
        var identifier = id ?? "";
        return url.Contains("googleapis.com", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("google", StringComparison.OrdinalIgnoreCase) ||
               nm.Contains("google", StringComparison.OrdinalIgnoreCase) ||
               nm.Contains("gemini", StringComparison.OrdinalIgnoreCase) ||
               identifier.Contains("google", StringComparison.OrdinalIgnoreCase) ||
               identifier.Contains("gemini", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAnthropicProvider(string baseUrl, string name, string id)
    {
        var url = baseUrl ?? "";
        var nm = name ?? "";
        var identifier = id ?? "";
        return url.Contains("api.anthropic.com", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("anthropic", StringComparison.OrdinalIgnoreCase) ||
               nm.Contains("anthropic", StringComparison.OrdinalIgnoreCase) ||
               nm.Contains("claude", StringComparison.OrdinalIgnoreCase) ||
               identifier.Contains("anthropic", StringComparison.OrdinalIgnoreCase) ||
               identifier.Contains("claude", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<RequestVariant> BuildRequestVariants(
        ProviderProfile provider,
        string model,
        string userMessage,
        IReadOnlyList<ChatMessage> history,
        IReadOnlyList<MemoryFact> facts,
        IReadOnlyList<ChatAttachment> attachments,
        string verbosity)
    {
        var isAnthropic = IsAnthropicProvider(provider.BaseUrl, provider.Name, provider.Id);
        var chatEndpoint = BuildChatEndpoint(provider.BaseUrl, isAnthropic);
        var variants = new List<RequestVariant>();

        if (isAnthropic)
        {
            var systemPrompt = BuildSystemPrompt(facts, verbosity);
            variants.Add(new("anthropic-standard", chatEndpoint, new JsonObject
            {
                ["model"] = model,
                ["max_tokens"] = 4096,
                ["system"] = systemPrompt,
                ["messages"] = BuildMessages(userMessage, history, facts, attachments, verbosity, includeSystem: false, includeImages: true, isAnthropic: true)
            }, false));
            variants.Add(new("anthropic-no-history", chatEndpoint, new JsonObject
            {
                ["model"] = model,
                ["max_tokens"] = 4096,
                ["system"] = systemPrompt,
                ["messages"] = BuildMessages(userMessage, Array.Empty<ChatMessage>(), facts, attachments, verbosity, includeSystem: false, includeImages: true, isAnthropic: true)
            }, false));
        }
        else
        {
            variants.Add(new("chat-standard", chatEndpoint, new JsonObject
            {
                ["model"] = model,
                ["stream"] = false,
                ["messages"] = BuildMessages(userMessage, history, facts, attachments, verbosity, includeSystem: true, includeImages: true, isAnthropic: false)
            }, false));
            variants.Add(new("chat-minimal", chatEndpoint, new JsonObject
            {
                ["model"] = model,
                ["messages"] = BuildMessages(userMessage, history, facts, attachments, verbosity, includeSystem: true, includeImages: true, isAnthropic: false)
            }, false));
            variants.Add(new("chat-max-completion-tokens", chatEndpoint, new JsonObject
            {
                ["model"] = model,
                ["max_completion_tokens"] = 4096,
                ["messages"] = BuildMessages(userMessage, history, facts, attachments, verbosity, includeSystem: true, includeImages: true, isAnthropic: false)
            }, false));
            variants.Add(new("chat-max-tokens", chatEndpoint, new JsonObject
            {
                ["model"] = model,
                ["max_tokens"] = 4096,
                ["messages"] = BuildMessages(userMessage, history, facts, attachments, verbosity, includeSystem: true, includeImages: true, isAnthropic: false)
            }, false));
            variants.Add(new("chat-no-history", chatEndpoint, new JsonObject
            {
                ["model"] = model,
                ["max_tokens"] = 4096,
                ["messages"] = BuildMessages(userMessage, Array.Empty<ChatMessage>(), facts, attachments, verbosity, includeSystem: true, includeImages: true, isAnthropic: false)
            }, false));
        }

        if (IsGoogleProvider(provider.BaseUrl, provider.Name, provider.Id))
        {
            var responsesEndpoint = BuildResponsesEndpoint(provider.BaseUrl);
            variants.Add(new("responses", responsesEndpoint, new JsonObject
            {
                ["model"] = model,
                ["instructions"] = BuildSystemPrompt(facts, verbosity),
                ["input"] = BuildResponsesInput(userMessage, history, facts, verbosity)
            }, true));
        }

        return variants;
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

    private static string BuildPromptWithAttachments(string content, IReadOnlyList<ChatAttachment> attachments)
    {
        if (attachments.Count == 0)
        {
            return content;
        }

        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(content))
        {
            builder.AppendLine(content.Trim());
            builder.AppendLine();
        }
        else
        {
            builder.AppendLine("Please review the attached file(s).");
            builder.AppendLine();
        }

        builder.AppendLine("Attached files:");
        builder.AppendLine("Acknowledge every attached file by filename and use the provided extracted text content to answer the user's request.");
        foreach (var attachment in attachments)
        {
            builder.AppendLine($"- {attachment.Name} ({attachment.ContentType}, {attachment.SizeBytes} bytes)");
            if (!string.IsNullOrWhiteSpace(attachment.TextPreview))
            {
                builder.AppendLine("  Extracted text content:");
                builder.AppendLine("  ```");
                foreach (var line in attachment.TextPreview.ReplaceLineEndings("\n").Split('\n'))
                {
                    builder.AppendLine("  " + line);
                }

                builder.AppendLine("  ```");
            }
            else if (attachment.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                builder.AppendLine(!string.IsNullOrWhiteSpace(attachment.DataUri) || (!string.IsNullOrWhiteSpace(attachment.Path) && System.IO.File.Exists(attachment.Path))
                    ? "  Image bytes are attached to the provider request when the selected model supports vision."
                    : "  Image file received, but image bytes were too large or unavailable for this request.");
            }
            else
            {
                builder.AppendLine("  File received; no text could be extracted for prompt context.");
            }
        }

        return builder.ToString();
    }

    private static JsonArray BuildMessages(
        string userMessage,
        IReadOnlyList<ChatMessage> history,
        IReadOnlyList<MemoryFact> facts,
        IReadOnlyList<ChatAttachment> attachments,
        string verbosity,
        bool includeSystem,
        bool includeImages,
        bool isAnthropic)
    {
        var messages = new JsonArray();
        if (includeSystem)
        {
            messages.Add(new JsonObject
            {
                ["role"] = "system",
                ["content"] = BuildSystemPrompt(facts, verbosity)
            });
        }

        foreach (var item in history.Where(message => message.Kind == "message" && !string.IsNullOrWhiteSpace(message.Content)).TakeLast(16))
        {
            var isAssistant = item.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase);
            var role = isAssistant ? "assistant" : "user";

            if (isAssistant)
            {
                messages.Add(new JsonObject
                {
                    ["role"] = role,
                    ["content"] = item.Content
                });
            }
            else
            {
                var attachmentsForMessage = ReadAttachments(item.Metadata);
                var textWithAttachments = attachmentsForMessage.Count > 0
                    ? BuildPromptWithAttachments(item.Content, attachmentsForMessage)
                    : item.Content;

                messages.Add(new JsonObject
                {
                    ["role"] = role,
                    ["content"] = BuildUserContent(textWithAttachments, attachmentsForMessage, includeImages, isAnthropic)
                });
            }
        }

        var finalUserMessageText = includeSystem ? userMessage : BuildPortableUserMessage(userMessage, facts, verbosity);
        var finalUserTextWithAttachments = finalUserMessageText;

        messages.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = BuildUserContent(finalUserTextWithAttachments, attachments, includeImages, isAnthropic)
        });
        return messages;
    }

    private static JsonNode BuildUserContent(string text, IReadOnlyList<ChatAttachment> attachments, bool includeImages, bool isAnthropic)
    {
        var images = includeImages
            ? attachments.Where(attachment => attachment.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)).ToList()
            : new List<ChatAttachment>();
        if (images.Count == 0)
        {
            return JsonValue.Create(text)!;
        }

        var parts = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = text
            }
        };
        foreach (var image in images.Take(6))
        {
            var dataUri = image.DataUri;
            if (string.IsNullOrWhiteSpace(dataUri) && !string.IsNullOrWhiteSpace(image.Path) && System.IO.File.Exists(image.Path))
            {
                try
                {
                    var bytes = System.IO.File.ReadAllBytes(image.Path);
                    dataUri = $"data:{image.ContentType};base64,{Convert.ToBase64String(bytes)}";
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to read attachment {image.Path}: {ex}");
                    continue;
                }
            }

            if (string.IsNullOrWhiteSpace(dataUri))
                continue;

            if (isAnthropic)
            {
                var comma = dataUri.IndexOf(',');
                if (comma > 0)
                {
                    var base64 = dataUri.Substring(comma + 1);
                    var mime = dataUri.Substring(5, comma - 5);
                    if (mime.EndsWith(";base64"))
                    {
                        mime = mime.Substring(0, mime.Length - 7);
                    }
                    parts.Add(new JsonObject
                    {
                        ["type"] = "image",
                        ["source"] = new JsonObject
                        {
                            ["type"] = "base64",
                            ["media_type"] = mime,
                            ["data"] = base64
                        }
                    });
                }
            }
            else
            {
                parts.Add(new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject
                    {
                        ["url"] = dataUri
                    }
                });
            }
        }

        if (parts.Count == 1)
        {
            return JsonValue.Create(text)!;
        }

        return parts;
    }

    private static string BuildPortableUserMessage(string userMessage, IReadOnlyList<MemoryFact> facts, string verbosity)
    {
        var builder = new StringBuilder();
        builder.AppendLine("System Instructions: You are GhostClaw, a Windows-native autonomous agent. Follow markdown formatting.");
        builder.AppendLine("GREETING RULE: If the user just says hello, keep your greeting simple (e.g., 'I'm GhostClaw. How can I help?'). If the user asks about your capabilities, explain what you can do clearly. Never repeat the same greeting multiple times in a row, and do not include local time, OS version, or model ID unless explicitly asked.");
        builder.AppendLine("=== CRITICAL FILE CREATION RULE ===");
        builder.AppendLine("If the user asks you to create, design, or attach a file (such as a PowerPoint presentation, Excel spreadsheet, Word document, PDF, zip, image, text, or CSV), you MUST generate it by writing the complete, self-contained Python script to create that file, and wrap it inside a ```python ... ``` code block. NEVER say you have created, attached, or saved a file unless your response contains the ```python ... ``` block that actually generates it! The host platform automatically executes your code block, attaches the created file to your chat bubble, and presents a download link card to the user.");
        builder.AppendLine("CRITICAL PYTHON CODE QUALITY INSTRUCTIONS:");
        builder.AppendLine("1. Never include un-commented decorative headers, text lines, or sections (such as '── Colour palette ──' or box drawings) inside python blocks. Every line in a ```python block must be valid, executable Python syntax. Comment out decorative dividers using '#'.");
        builder.AppendLine("2. For ReportLab PDF generation: ALWAYS import standard alignment constants with underscores (e.g. TA_CENTER, TA_JUSTIFY, TA_LEFT, TA_RIGHT) from 'reportlab.lib.enums'. NEVER write TACENTER, TAJUSTIFY, TALEFT, or TARIGHT without underscores.");
        builder.AppendLine("3. For python-pptx presentations: NEVER use non-existent methods like '.fit_text()' or '.autofit()' on text frames or shapes. Use standard font sizing and word wrap features.");
        builder.AppendLine("===================================");
        builder.AppendLine($"Verbosity: {verbosity}.");
        if (facts.Count > 0)
        {
            builder.AppendLine("Relevant memory:");
            foreach (var fact in facts)
            {
                builder.AppendLine($"- {fact.Summary}: {fact.Content}");
            }
        }

        builder.AppendLine();
        builder.AppendLine(userMessage);
        return builder.ToString();
    }

    private static bool ShouldTryNextPayload(System.Net.HttpStatusCode statusCode, string body, string variant)
    {
        if (variant == "responses")
        {
            return false;
        }

        var message = ExtractErrorMessage(body);
        if ((int)statusCode == 404)
        {
            return true;
        }

        return (int)statusCode == 400 &&
               (message.Contains("value must be set", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("system", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("stream", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("max_tokens", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("max_completion_tokens", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("unsupported", StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildResponsesInput(
        string userMessage,
        IReadOnlyList<ChatMessage> history,
        IReadOnlyList<MemoryFact> facts,
        string verbosity)
    {
        var builder = new StringBuilder();
        foreach (var item in history.Where(message => message.Kind == "message" && !string.IsNullOrWhiteSpace(message.Content)).TakeLast(8))
        {
            builder.AppendLine($"{(item.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? "Assistant" : "User")}: {item.Content}");
        }

        if (facts.Count > 0 || !verbosity.Equals("Normal", StringComparison.OrdinalIgnoreCase))
        {
            builder.AppendLine(BuildPortableUserMessage(userMessage, facts, verbosity));
        }
        else
        {
            builder.AppendLine(userMessage);
        }

        return builder.ToString();
    }

    private static IReadOnlyList<string> CleanModels(IReadOnlyList<string>? models) =>
        (models ?? Array.Empty<string>())
        .Select(model => model.Trim())
        .Where(model => model.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(model => model)
        .ToList();

    private static IReadOnlyList<string> ExtractModels(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var models = new List<string>();

        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                {
                    models.Add(id.GetString()!);
                }
                else if (item.ValueKind == JsonValueKind.String)
                {
                    models.Add(item.GetString()!);
                }
            }
        }

        if (root.TryGetProperty("models", out var modelArray) && modelArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in modelArray.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    models.Add(item.GetString()!);
                }
            }
        }

        return CleanModels(models);
    }

    private static string ExtractAssistantContent(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
        {
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.TryGetProperty("message", out var message))
                {
                    string? contentStr = null;
                    string? reasoningStr = null;

                    if (message.TryGetProperty("content", out var content))
                    {
                        if (content.ValueKind == JsonValueKind.String)
                        {
                            contentStr = content.GetString();
                        }
                        else if (content.ValueKind == JsonValueKind.Array)
                        {
                            contentStr = ExtractContentParts(content);
                        }
                    }

                    if (message.TryGetProperty("reasoning_content", out var reasoning) && reasoning.ValueKind == JsonValueKind.String)
                    {
                        reasoningStr = reasoning.GetString();
                    }
                    else if (message.TryGetProperty("reasoning", out reasoning) && reasoning.ValueKind == JsonValueKind.String)
                    {
                        reasoningStr = reasoning.GetString();
                    }

                    if (!string.IsNullOrEmpty(reasoningStr))
                    {
                        string combined = contentStr ?? string.Empty;
                        if (!string.IsNullOrEmpty(contentStr))
                        {
                            var trimmedContent = contentStr.TrimStart();
                            bool alreadyHasThinkBlock = trimmedContent.StartsWith("<think>", StringComparison.OrdinalIgnoreCase) ||
                                                        trimmedContent.StartsWith("<thinking>", StringComparison.OrdinalIgnoreCase) ||
                                                        trimmedContent.StartsWith("<thought>", StringComparison.OrdinalIgnoreCase);

                            if (alreadyHasThinkBlock)
                            {
                                combined = contentStr;
                            }
                            else if (trimmedContent.StartsWith(reasoningStr, StringComparison.OrdinalIgnoreCase))
                            {
                                combined = $"<think>\n{reasoningStr}\n</think>\n" + trimmedContent.Substring(reasoningStr.Length);
                            }
                            else
                            {
                                combined = $"<think>\n{reasoningStr}\n</think>\n{contentStr}";
                            }
                        }
                        else
                        {
                            combined = $"<think>\n{reasoningStr}\n</think>";
                        }
                        return SanitizeProviderContent(combined);
                    }

                    if (contentStr != null)
                    {
                        return SanitizeProviderContent(contentStr);
                    }
                }

                if (choice.TryGetProperty("message", out var messageForTool) &&
                    messageForTool.TryGetProperty("tool_calls", out var toolCalls) &&
                    toolCalls.ValueKind == JsonValueKind.Array)
                {
                    return "I received a tool action request without a final answer. I hid the raw tool payload; try again with GhostClaw agent tools enabled or ask for a plain-language answer.";
                }

                if (choice.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    return SanitizeProviderContent(text.GetString() ?? string.Empty);
                }
            }
        }

        if (root.TryGetProperty("output_text", out var outputText) && outputText.ValueKind == JsonValueKind.String)
        {
            return SanitizeProviderContent(outputText.GetString() ?? string.Empty);
        }

        return body;
    }

    private static string ExtractResponsesContent(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.TryGetProperty("output_text", out var outputText) && outputText.ValueKind == JsonValueKind.String)
        {
            return SanitizeProviderContent(outputText.GetString() ?? string.Empty);
        }

        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            var builder = new StringBuilder();
            foreach (var item in output.EnumerateArray())
            {
                if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var part in content.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    {
                        builder.Append(text.GetString());
                    }
                    else if (part.TryGetProperty("image_url", out var imageUrl) && imageUrl.ValueKind == JsonValueKind.String)
                    {
                        builder.AppendLine();
                        builder.AppendLine($"![Generated image]({imageUrl.GetString()})");
                    }
                    else if (part.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
                    {
                        builder.AppendLine();
                        builder.AppendLine($"![Generated image]({url.GetString()})");
                    }
                }
            }

            if (builder.Length > 0)
            {
                return SanitizeProviderContent(builder.ToString());
            }
        }

        return body;
    }

    private static string HumanizeProviderError(System.Net.HttpStatusCode statusCode, string body, string variant)
    {
        var status = (int)statusCode;
        var providerMessage = ExtractErrorMessage(body);
        var hint = status switch
        {
            401 or 403 => "Authentication failed. Check the API key or provider permissions.",
            404 => "The endpoint was not found. Check whether the base URL should include a version path.",
            429 => "The provider is rate limiting requests.",
            400 when providerMessage.Contains("value must be set", StringComparison.OrdinalIgnoreCase) => "The provider rejected the request because a required value is missing. Check the API key, base URL, and selected model.",
            >= 500 => "The provider is having a server-side issue.",
            _ => "The provider rejected the request."
        };

        return string.IsNullOrWhiteSpace(providerMessage)
            ? $"{hint} HTTP {status}."
            : $"{hint} HTTP {status}. {providerMessage}";
    }

    private static string ExtractContentParts(JsonElement parts)
    {
        var builder = new StringBuilder();
        foreach (var part in parts.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String)
            {
                builder.Append(part.GetString());
                continue;
            }

            if (part.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                builder.Append(text.GetString());
            }
            else if (part.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            {
                builder.Append(content.GetString());
            }
            else if (part.TryGetProperty("image_url", out var imageUrl))
            {
                var url = imageUrl.ValueKind == JsonValueKind.String
                    ? imageUrl.GetString()
                    : imageUrl.ValueKind == JsonValueKind.Object &&
                      imageUrl.TryGetProperty("url", out var nestedUrl) &&
                      nestedUrl.ValueKind == JsonValueKind.String
                        ? nestedUrl.GetString()
                        : null;
                if (!string.IsNullOrWhiteSpace(url))
                {
                    builder.AppendLine();
                    builder.AppendLine($"![Generated image]({url})");
                }
            }
        }

        return SanitizeProviderContent(builder.ToString());
    }

    private static string SanitizeProviderContent(string content) =>
        ResponseTextSanitizer.CleanForStorage(content);

    private static string ExtractErrorMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                {
                    return error.GetString() ?? string.Empty;
                }

                if (error.ValueKind == JsonValueKind.Object &&
                    error.TryGetProperty("message", out var message) &&
                    message.ValueKind == JsonValueKind.String)
                {
                    return message.GetString() ?? string.Empty;
                }
            }

            if (root.TryGetProperty("message", out var topMessage) && topMessage.ValueKind == JsonValueKind.String)
            {
                return topMessage.GetString() ?? string.Empty;
            }
        }
        catch
        {
            // Provider bodies are often plain text.
        }

        return TrimBody(body);
    }

    private static string TrimBody(string body)
    {
        body = body.ReplaceLineEndings(" ").Trim();
        return body.Length <= 260 ? body : body[..260] + "...";
    }

    public async Task<string?> ExtractMemoryAsync(
        ProviderProfile provider,
        string apiKey,
        string model,
        string prompt,
        CancellationToken cancellationToken)
    {
        bool isAnthropic = provider.BaseUrl.Contains("anthropic.com", StringComparison.OrdinalIgnoreCase);
        var chatEndpoint = BuildChatEndpoint(provider.BaseUrl, isAnthropic);
        var payload = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = prompt
                }
            }
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, chatEndpoint);
            ApplyAuth(request, apiKey, isAnthropic);
            request.Content = new StringContent(payload.ToJsonString(PipeJson.Options), Encoding.UTF8, "application/json");
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return ExtractAssistantContent(body);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Provider check/extract failed: {ex}");
        }
        return null;
    }
}
