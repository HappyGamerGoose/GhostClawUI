using System.IO;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Linq;
using System.Text.RegularExpressions;
using GhostClawUI.Service.Agent;
using GhostClawUI.Service.Infrastructure;
using GhostClawUI.Service.Providers;
using GhostClawUI.Service.Storage;
using GhostClawUI.Shared;

namespace GhostClawUI.Service.Ipc;

internal sealed class CommandRouter
{
    private readonly EncryptedStore _store;
    private readonly ProviderGateway _providerGateway;
    private readonly McpCatalog _mcpCatalog;
    private readonly McpToolRunner _mcpToolRunner;
    private readonly GhostClawAgentRunner _ghostClawAgentRunner;
    private readonly GhostClawSupervisor _supervisor;
    private readonly AppPaths _paths;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<AgentTraceCard>> _runningTraces = new();

    public event Action<string, AgentTraceCard>? OnAgentTraceEmitted;

    public CommandRouter(
        EncryptedStore store,
        ProviderGateway providerGateway,
        McpCatalog mcpCatalog,
        McpToolRunner mcpToolRunner,
        GhostClawAgentRunner ghostClawAgentRunner,
        GhostClawSupervisor supervisor,
        AppPaths paths)
    {
        _store = store;
        _providerGateway = providerGateway;
        _mcpCatalog = mcpCatalog;
        _mcpToolRunner = mcpToolRunner;
        _ghostClawAgentRunner = ghostClawAgentRunner;
        _supervisor = supervisor;
        _paths = paths;
    }

    public async Task<PipeEnvelope> HandleAsync(PipeEnvelope request, CancellationToken cancellationToken)
    {
        return request.Command switch
        {
            "status.get" => PipeEnvelope.Response(request, _supervisor.Status),
            "health.check" => PipeEnvelope.Response(request, BuildHealthReport()),
            "service.restart" => PipeEnvelope.Response(request, _supervisor.Restart()),
            "providers.list" => PipeEnvelope.Response(request, _store.ListProviders()),
            "providers.upsert" => PipeEnvelope.Response(request, _store.UpsertProvider(Require<ProviderUpsertRequest>(request))),
            "providers.remove" => PipeEnvelope.Response(request, RemoveProvider(Require<SimpleIdRequest>(request))),
            "providers.validate" => PipeEnvelope.Response(request, await _providerGateway.ValidateAsync(Require<ProviderValidationRequest>(request), cancellationToken).ConfigureAwait(false)),
            "providers.test" => PipeEnvelope.Response(request, await _providerGateway.ValidateAsync(Require<ProviderValidationRequest>(request), cancellationToken).ConfigureAwait(false)),
            "providers.testModel" => PipeEnvelope.Response(request, await _providerGateway.TestModelAsync(Require<ProviderModelTestRequest>(request), cancellationToken).ConfigureAwait(false)),
            "conversations.list" => PipeEnvelope.Response(request, _store.ListConversations(request.ReadPayload<SimpleTextRequest>()?.Text)),
            "conversations.create" => PipeEnvelope.Response(request, _store.GetOrCreateConversation()),
            "conversations.get" => PipeEnvelope.Response(request, _store.GetOrCreateConversation(Require<SimpleIdRequest>(request).Id)),
            "conversations.rename" => PipeEnvelope.Response(request, RenameConversation(request)),
            "conversations.pin" => PipeEnvelope.Response(request, PinConversation(request)),
            "conversations.delete" => PipeEnvelope.Response(request, DeleteConversation(Require<SimpleIdRequest>(request))),
            "conversations.deleteMessagesAfter" => PipeEnvelope.Response(request, DeleteMessagesAfter(request)),
            "messages.update" => PipeEnvelope.Response(request, UpdateMessage(request)),
            "conversations.clearContext" => PipeEnvelope.Response(request, ClearContext(Require<SimpleIdRequest>(request))),
            "conversations.export" => PipeEnvelope.Response(request, _store.ExportConversation(Require<ExportRequest>(request).Id, Require<ExportRequest>(request).Format)),
            "chat.send" => PipeEnvelope.Response(request, await SendChatAsync(Require<ChatSendRequest>(request), request.Payload, cancellationToken).ConfigureAwait(false)),
            "chat.activeTraces" => PipeEnvelope.Response(request, GetActiveTraces(Require<SimpleIdRequest>(request).Id)),
            "mcp.catalog" => PipeEnvelope.Response(request, await _mcpCatalog.RefreshAsync(cancellationToken).ConfigureAwait(false)),
            "mcp.list" => PipeEnvelope.Response(request, _mcpCatalog.List()),
            "mcp.search" => PipeEnvelope.Response(request, await HandleMcpSearchAsync(request, cancellationToken).ConfigureAwait(false)),
            "mcp.install" => PipeEnvelope.Response(request, _mcpCatalog.Install(Require<McpServerRequest>(request))),
            "mcp.update" => PipeEnvelope.Response(request, _mcpCatalog.Update(Require<McpServerRequest>(request))),
            "mcp.uninstall" => PipeEnvelope.Response(request, _mcpCatalog.Uninstall(Require<McpServerRequest>(request))),
            "mcp.manualAdd" => PipeEnvelope.Response(request, _mcpCatalog.AddManual(Require<SimpleTextRequest>(request))),
            "memory.list" => PipeEnvelope.Response(request, _store.ListMemory()),
            "memory.upsert" => PipeEnvelope.Response(request, _store.UpsertMemory(Require<MemoryUpdateRequest>(request))),
            "memory.delete" => PipeEnvelope.Response(request, DeleteMemory(Require<SimpleIdRequest>(request))),
            "memory.purge" => PipeEnvelope.Response(request, PurgeMemory()),
            "settings.get" => PipeEnvelope.Response(request, _store.GetSettings()),
            "settings.update" => PipeEnvelope.Response(request, SaveSettings(Require<AppSettings>(request))),
            "data.export" => PipeEnvelope.Response(request, _store.ExportAllData()),
            "data.purge" => PipeEnvelope.Response(request, PurgeAllData()),
            "preset.export" => PipeEnvelope.Response(request, BuildPreset()),
            "preset.import" => PipeEnvelope.Response(request, ImportPreset(Require<Preset>(request))),
            "tasks.list" => PipeEnvelope.Response(request, _store.ListScheduledTasks()),
            "tasks.upsert" => PipeEnvelope.Response(request, ExecuteUpsertScheduledTask(request)),
            "tasks.delete" => PipeEnvelope.Response(request, DeleteScheduledTask(Require<SimpleIdRequest>(request))),
            "tasks.logs" => PipeEnvelope.Response(request, _store.ListTaskRunLogs(Require<SimpleIdRequest>(request).Id)),
            "tasks.runNow" => PipeEnvelope.Response(request, RunScheduledTaskNow(Require<SimpleIdRequest>(request))),
            "ralph.list" => PipeEnvelope.Response(request, ListRalphRuns()),
            "ralph.start" => PipeEnvelope.Response(request, StartRalph(Require<RalphStartRequest>(request))),
            "ralph.stop" => PipeEnvelope.Response(request, StopRalph(Require<SimpleIdRequest>(request))),
            "ralph.checklists" => PipeEnvelope.Response(request, ListRalphChecklists()),
            "skills.list" => PipeEnvelope.Response(request, ListSkills()),
            "skills.read" => PipeEnvelope.Response(request, ReadSkill(Require<SimpleIdRequest>(request))),
            "skills.upsert" => PipeEnvelope.Response(request, UpsertSkill(Require<SkillUpsertRequest>(request))),
            "telegram.get" => PipeEnvelope.Response(request, _store.GetTelegramSettings()),
            "telegram.save" => PipeEnvelope.Response(request, SaveTelegramSettingsHelper(Require<TelegramSettings>(request))),
            "telegram.status" => PipeEnvelope.Response(request, GetTelegramStatus()),
            "whatsapp.get" => PipeEnvelope.Response(request, _store.GetWhatsAppSettings()),
            "whatsapp.save" => PipeEnvelope.Response(request, SaveWhatsAppSettingsHelper(Require<WhatsAppSettings>(request))),
            "whatsapp.status" => PipeEnvelope.Response(request, GetWhatsAppStatus()),
            "updates.check" => PipeEnvelope.Response(request, new CommandResult(true, "No update is currently staged.")),
            "updates.rollback" => PipeEnvelope.Response(request, new CommandResult(true, "No rollback package is currently staged.")),
            "undo.last" => PipeEnvelope.Response(request, _supervisor.UndoLastFileModification()),
            _ => PipeEnvelope.ErrorResponse(request, $"Unknown command: {request.Command}")
        };
    }

    private ServiceHealthReport BuildHealthReport()
    {
        var issues = new List<string>();
        var storeReadable = false;
        var storeWritable = false;
        try
        {
            _ = _store.ListProviders();
            storeReadable = true;
            storeWritable = _store.ProbeWritable();
        }
        catch (Exception ex)
        {
            issues.Add($"Store: {ex.Message}");
        }

        var payloadZip = Path.Combine(_paths.PackagedPayloadRoot, "payload.zip");
        var payloadPresent = File.Exists(payloadZip);
        if (!payloadPresent)
        {
            issues.Add($"Payload missing: {payloadZip}");
        }

        var runtimeExtracted = Directory.Exists(_paths.GhostClawRuntimeRoot) && Directory.Exists(_paths.NodeRuntimeRoot);
        if (!runtimeExtracted)
        {
            issues.Add($"Runtime not extracted: {_paths.RuntimeRoot}");
        }

        var nodeExe = _paths.ResolveNodeExe();
        var nodePresent = File.Exists(nodeExe) || nodeExe.Equals("node.exe", StringComparison.OrdinalIgnoreCase);
        if (!nodePresent)
        {
            issues.Add($"Node missing: {nodeExe}");
        }

        var entry = Path.Combine(_paths.GhostClawRuntimeRoot, "dist", "index.js");
        var entryPresent = File.Exists(entry);
        if (!entryPresent)
        {
            issues.Add($"GhostClaw entry missing: {entry}");
        }

        if (!_supervisor.Status.GhostClawRunning && !string.IsNullOrWhiteSpace(_supervisor.Status.Detail))
        {
            issues.Add(_supervisor.Status.Detail);
        }

        return new ServiceHealthReport(true, storeReadable, storeWritable, payloadPresent, runtimeExtracted, nodePresent, entryPresent, _supervisor.Status, issues.Distinct().ToList(), DateTimeOffset.UtcNow);
    }

    private async Task<ChatSendResult> SendChatAsync(ChatSendRequest chatRequest, JsonNode? rawPayload, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(chatRequest.ProviderId))
        {
            throw new InvalidOperationException("Choose a provider before sending.");
        }

        if (string.IsNullOrWhiteSpace(chatRequest.Model))
        {
            throw new InvalidOperationException("Choose a model before sending.");
        }

        var rawAttachments = chatRequest.Attachments ?? Array.Empty<ChatAttachment>();
        var processedAttachments = new List<ChatAttachment>();
        foreach (var att in rawAttachments)
        {
            if (string.IsNullOrWhiteSpace(att.TextPreview) && !string.IsNullOrWhiteSpace(att.Path) && File.Exists(att.Path))
            {
                var text = FileTextExtractor.ReadTextPreviewAsync(att.Path, att.SizeBytes, 200000).GetAwaiter().GetResult();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    processedAttachments.Add(new ChatAttachment(att.Name, att.Path, att.ContentType, att.SizeBytes, text, att.DataUri));
                    continue;
                }
            }
            processedAttachments.Add(att);
        }
        // Extract URLs from chatRequest.Content and append them as processedAttachments
        var urls = System.Text.RegularExpressions.Regex.Matches(chatRequest.Content ?? "", @"https?://[^\s]+").Select(m => m.Value).Distinct().ToList();
        if (urls.Count > 0)
        {
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36");
            foreach (var url in urls.Take(3))
            {
                try
                {
                    using var response = await client.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
                        if (mediaType.Contains("text/") || mediaType.Contains("json"))
                        {
                            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                            using var reader = new System.IO.StreamReader(stream);
                            var buffer = new char[200000];
                            int read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                            var html = new string(buffer, 0, read);

                            var plainText = System.Text.RegularExpressions.Regex.Replace(html, "<style.*?>[\\s\\S]*?</style>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            plainText = System.Text.RegularExpressions.Regex.Replace(plainText, "<script.*?>[\\s\\S]*?</script>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            plainText = System.Text.RegularExpressions.Regex.Replace(plainText, "<[^>]+>", " ");
                            plainText = System.Text.RegularExpressions.Regex.Replace(plainText, @"\s+", " ").Trim();
                            if (plainText.Length > 20000) plainText = plainText.Substring(0, 20000) + "...";

                            var uri = new Uri(url);
                            processedAttachments.Add(new ChatAttachment(
                                uri.Host,
                                url,
                                "text/html",
                                plainText.Length,
                                $"[Live Web Content from {url}]\n" + plainText,
                                null));
                        }
                    }
                }
                catch { /* Ignore fetch errors */ }
            }
        }

        var attachments = processedAttachments.ToList();

        if (string.IsNullOrWhiteSpace(chatRequest.Content) && attachments.Count == 0)
        {
            throw new InvalidOperationException("Message text or attachment is required.");
        }

        var provider = _store.GetProvider(chatRequest.ProviderId);
        var conversation = _store.GetOrCreateConversation(chatRequest.ConversationId);

        var settings = _store.GetSettings();
        if (attachments.Count > 0 &&
            attachments.Any(a => a.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrWhiteSpace(settings.VisionTranslatorProviderId) &&
            !string.IsNullOrWhiteSpace(settings.VisionTranslatorModel) &&
            chatRequest.ProviderId != settings.VisionTranslatorProviderId)
        {
            var visionProvider = _store.GetProvider(settings.VisionTranslatorProviderId);
            if (visionProvider != null && visionProvider.IsEnabled)
            {
                var vApiKey = GhostClawUI.Service.Infrastructure.PasswordVaultHelper.ReadProviderKey(visionProvider.Id) ?? string.Empty;
                var translatedAttachments = new List<ChatAttachment>();
                foreach (var attachment in attachments)
                {
                    if (attachment.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    {
                        var vAtts = new List<ChatAttachment> { attachment };
                        var (desc, err) = await _providerGateway.SendChatAsync(
                            visionProvider,
                            vApiKey,
                            settings.VisionTranslatorModel,
                            "Describe this image in detail so that a non-vision AI can understand it completely.",
                            Array.Empty<ChatMessage>(),
                            Array.Empty<MemoryFact>(),
                            vAtts,
                            "Normal",
                            cancellationToken).ConfigureAwait(false);

                        if (!string.IsNullOrWhiteSpace(desc))
                        {
                            translatedAttachments.Add(new ChatAttachment(
                                attachment.Name,
                                attachment.Path,
                                "text/plain",
                                attachment.SizeBytes,
                                $"[Image Translator Description for '{attachment.Name}']\n{desc}",
                                null));
                        }
                        else
                        {
                            translatedAttachments.Add(attachment);
                        }
                    }
                    else
                    {
                        translatedAttachments.Add(attachment);
                    }
                }
                attachments = translatedAttachments;
            }
        }
        var conversationId = conversation.Summary.Id;
        var priorMessages = conversation.Messages;
        var userContent = string.IsNullOrWhiteSpace(chatRequest.Content) ? "Attached file" : chatRequest.Content;
        var basePrompt = BuildPromptWithAttachments(chatRequest.Content, attachments);
        var providerContent = AppendRuntimeContext(basePrompt, chatRequest.Content ?? string.Empty);
        providerContent = AppendFilesystemContext(providerContent);
        var user = _store.AddMessage(conversationId, "user", userContent, chatRequest.ProviderId, chatRequest.Model, "message", BuildAttachmentMetadata(attachments));

        McpToolSearchResult? searchResult = null;
        var userPrompt = chatRequest.Content ?? string.Empty;
        if (NeedsFreshInformation(userPrompt))
        {
            _mcpCatalog.EnsureGhostClawSettings();
            searchResult = await _mcpToolRunner.TrySearchAsync(userPrompt, _store.ListMcpServers(), cancellationToken).ConfigureAwait(false);
            if (searchResult is not null)
            {
                providerContent = AppendSearchContext(providerContent, searchResult);
            }
        }

        var facts = _store.SearchMemory(basePrompt);
        var trace = string.Equals(chatRequest.Verbosity, "Verbose", StringComparison.OrdinalIgnoreCase)
            ? new List<AgentTraceCard>
            {
                new("Context", $"Loaded {facts.Count} relevant memory fact(s).", "done"),
                new("Tools", "MCP tools are synchronized into GhostClaw settings.json for agent runs.", "ready")
            }
            : new List<AgentTraceCard>();
        if (searchResult is not null && string.Equals(chatRequest.Verbosity, "Verbose", StringComparison.OrdinalIgnoreCase))
        {
            trace.Add(new AgentTraceCard("Search", $"Ran {searchResult.ToolName} through {searchResult.ServerName}.", "done"));
        }

        if (provider is null)
        {
            var error = _store.AddMessage(conversationId, "assistant", "Provider not found. Choose another model or re-add the provider.", chatRequest.ProviderId, chatRequest.Model, "error");
            return new ChatSendResult(error, trace, facts, false, error.Content);
        }

        var apiKey = GhostClawUI.Service.Infrastructure.PasswordVaultHelper.ReadProviderKey(provider.Id) ?? string.Empty;

        // Save user message memory background extraction
        StoreRememberedFacts(userContent, provider, chatRequest.Model, apiKey);

        var shouldTryAgent = chatRequest.AgentMode || ShouldTryGhostClawAgent(provider, userPrompt);
        if (shouldTryAgent)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                apiKey = "none";
            }

            if (facts.Count > 0)
            {
                var memoryBuilder = new StringBuilder();
                memoryBuilder.AppendLine();
                memoryBuilder.AppendLine("=== RELEVANT CROSS-CHAT MEMORIES ===");
                foreach (var fact in facts)
                {
                    memoryBuilder.AppendLine($"- {fact.Summary}: {fact.Content}");
                }
                memoryBuilder.AppendLine("=====================================");
                providerContent = memoryBuilder.ToString() + "\n" + providerContent;
            }

            _runningTraces[conversationId] = new List<AgentTraceCard>();
            GhostClawAgentResult agentResult;
            try
            {
                agentResult = await _ghostClawAgentRunner.TryRunAsync(
                    provider,
                    apiKey,
                    chatRequest.Model,
                    providerContent,
                    conversationId,
                    traceCard =>
                    {
                        OnAgentTraceEmitted?.Invoke(conversationId, traceCard);
                        if (_runningTraces.TryGetValue(conversationId, out var existingList))
                        {
                            lock (existingList)
                            {
                                var index = existingList.FindIndex(t => t.Title == traceCard.Title);
                                if (index >= 0)
                                {
                                    existingList[index] = traceCard;
                                }
                                else
                                {
                                    existingList.Add(traceCard);
                                }
                            }
                        }
                    },
                    cancellationToken,
                    attachments).ConfigureAwait(false);
            }
            finally
            {
                _runningTraces.TryRemove(conversationId, out _);
            }

            if (agentResult.Success && !string.IsNullOrWhiteSpace(agentResult.Content))
            {
                if (string.Equals(chatRequest.Verbosity, "Verbose", StringComparison.OrdinalIgnoreCase))
                {
                    trace.Add(new AgentTraceCard("GhostClaw Agent", "Ran through GhostClaw's agent runner with tool access.", "done"));
                }

                var agentContent = ResponseTextSanitizer.CleanForStorage(agentResult.Content);
                var inlineAttachments = await AutoExecuteFileGeneratorsAsync(agentContent).ConfigureAwait(false);
                var mergedAttachments = new List<ChatAttachment>(inlineAttachments);
                if (agentResult.Attachments is { Count: > 0 })
                {
                    mergedAttachments.AddRange(agentResult.Attachments);
                }

                var metadata = new JsonObject();
                if (mergedAttachments.Count > 0)
                {
                    metadata["attachments"] = JsonSerializer.SerializeToNode(mergedAttachments, PipeJson.Options);
                }
                if (agentResult.Traces is { Count: > 0 })
                {
                    metadata["traces"] = JsonSerializer.SerializeToNode(agentResult.Traces, PipeJson.Options);
                }

                var metadataNode = metadata.Count > 0 ? metadata : null;
                var agentAssistant = _store.AddMessage(conversationId, "assistant", agentContent, chatRequest.ProviderId, chatRequest.Model, "message", metadataNode);
                StoreRememberedFacts(agentResult.Content, provider, chatRequest.Model, apiKey);

                var finalTraces = agentResult.Traces ?? new List<AgentTraceCard>();
                return new ChatSendResult(agentAssistant, trace, facts, false, null);
            }

            if (string.Equals(chatRequest.Verbosity, "Verbose", StringComparison.OrdinalIgnoreCase))
            {
                trace.Add(new AgentTraceCard("GhostClaw Agent", $"Agent mode fell back to direct chat: {agentResult.Error}", "fallback"));
            }
        }

        var (content, errorMessage) = await _providerGateway.SendChatAsync(provider, apiKey, chatRequest.Model, providerContent, priorMessages, facts, attachments, chatRequest.Verbosity, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            if (_store.GetSettings().FallbackProvidersEnabled)
            {
                var fallbackProviders = _store.ListProviders()
                    .Where(p => p.IsEnabled && p.Id != chatRequest.ProviderId)
                    .ToList();

                foreach (var fallbackProvider in fallbackProviders)
                {
                    var fallbackModel = fallbackProvider.DefaultModel ?? fallbackProvider.Models.FirstOrDefault();
                    if (string.IsNullOrEmpty(fallbackModel)) continue;

                    var fallbackApiKey = PasswordVaultHelper.ReadProviderKey(fallbackProvider.Id) ?? string.Empty;
                    trace.Add(new AgentTraceCard("Failover", $"Attempting failover to provider {fallbackProvider.Name} ({fallbackModel})...", "running"));

                    var (fbContent, fbError) = await _providerGateway.SendChatAsync(
                        fallbackProvider,
                        fallbackApiKey,
                        fallbackModel,
                        providerContent,
                        priorMessages,
                        facts,
                        attachments,
                        chatRequest.Verbosity,
                        cancellationToken).ConfigureAwait(false);

                    if (string.IsNullOrWhiteSpace(fbError) && !string.IsNullOrWhiteSpace(fbContent))
                    {
                        var lastRun = trace.LastOrDefault(t => t.Title == "Failover" && t.State == "running");
                        if (lastRun != null) trace.Remove(lastRun);
                        trace.Add(new AgentTraceCard("Failover", $"Succeeded using fallback provider {fallbackProvider.Name}.", "done"));

                        var generatedfbAttachments = await AutoExecuteFileGeneratorsAsync(fbContent).ConfigureAwait(false);
                        generatedfbAttachments = ScanAndAttachMentionedFiles(fbContent, generatedfbAttachments);

                        JsonObject? fbMetadata = null;
                        if (generatedfbAttachments.Count > 0)
                        {
                            fbMetadata = new JsonObject
                            {
                                ["attachments"] = JsonSerializer.SerializeToNode(generatedfbAttachments, PipeJson.Options)
                            };
                        }

                        var fbAssistant = _store.AddMessage(conversationId, "assistant", fbContent, fallbackProvider.Id, fallbackModel, "message", fbMetadata);
                        StoreRememberedFacts(fbContent, fallbackProvider, fallbackModel, fallbackApiKey);

                        return new ChatSendResult(fbAssistant, trace, facts, false, null);
                    }
                    else
                    {
                        var lastRun = trace.LastOrDefault(t => t.Title == "Failover" && t.State == "running");
                        if (lastRun != null) trace.Remove(lastRun);
                        trace.Add(new AgentTraceCard("Failover", $"Failover to {fallbackProvider.Name} failed: {fbError}", "failed"));
                    }
                }
            }

            var queued = ShouldQueueProviderError(errorMessage);
            if (queued)
            {
                _store.QueueTask("chat.send", rawPayload);
            }

            var error = _store.AddMessage(conversationId, "assistant", errorMessage, chatRequest.ProviderId, chatRequest.Model, "error");
            return new ChatSendResult(
                error,
                queued ? trace.Append(new AgentTraceCard("Retry", "Request queued for reconnect and retried with backoff.", "queued")).ToList() : trace,
                facts,
                queued,
                errorMessage);
        }

        var generatedAttachments = new List<ChatAttachment>();
        if (!string.IsNullOrWhiteSpace(content))
        {
            generatedAttachments = await AutoExecuteFileGeneratorsAsync(content).ConfigureAwait(false);
            generatedAttachments = ScanAndAttachMentionedFiles(content, generatedAttachments);
        }

        JsonObject? assistantMetadata = null;
        if (generatedAttachments.Count > 0)
        {
            assistantMetadata = new JsonObject
            {
                ["attachments"] = JsonSerializer.SerializeToNode(generatedAttachments, PipeJson.Options)
            };
        }

        var assistant = _store.AddMessage(conversationId, "assistant", content ?? string.Empty, chatRequest.ProviderId, chatRequest.Model, "message", assistantMetadata);
        StoreRememberedFacts(content ?? string.Empty, provider, chatRequest.Model, apiKey);
        return new ChatSendResult(assistant, trace, facts, false, null);
    }

    private static JsonObject? BuildAttachmentMetadata(List<ChatAttachment> attachments) =>
        attachments.Count == 0
            ? null
            : new JsonObject { ["attachments"] = JsonSerializer.SerializeToNode(attachments, PipeJson.Options) };

    private static string BuildPromptWithAttachments(string content, List<ChatAttachment> attachments)
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

    private static string AppendSearchContext(string prompt, McpToolSearchResult result)
    {
        var builder = new StringBuilder(prompt.TrimEnd());
        builder.AppendLine();
        builder.AppendLine();
        builder.AppendLine("Search evidence:");
        builder.AppendLine($"Source tool: {result.ServerName} / {result.ToolName}");
        builder.AppendLine($"Query: {result.Query}");
        builder.AppendLine(result.Content);
        builder.AppendLine();
        builder.AppendLine("Use the search evidence only where relevant. Do not show raw tool plumbing or mention internal MCP request details.");
        return builder.ToString();
    }

    private string AppendRuntimeContext(string userPrompt, string rawUserPrompt)
    {
        var installedTools = _store.ListMcpServers()
            .Where(server => server.Installed)
            .OrderBy(server => server.Name)
            .Take(16)
            .ToList();
        var builder = new StringBuilder(userPrompt.TrimEnd());
        builder.AppendLine();
        builder.AppendLine();
        builder.AppendLine("<ghostclaw_runtime_context>");
        builder.AppendLine("greeting_introduction_policy: Keep introductions and greetings extremely simple, direct, and concise (e.g., 'I'm GhostClaw. How can I help you today?'). NEVER include details like your local/UTC time, timezone, OS version, or specific model name/ID in your greetings or introductions unless the user explicitly asks for them.");
        builder.AppendLine($"local_time: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"utc_time: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
        builder.AppendLine(installedTools.Count == 0
            ? "installed_mcp_tools: none"
            : "installed_mcp_tools: " + string.Join(", ", installedTools.Select(server => server.Name)));

        if (NeedsFreshInformation(userPrompt))
        {
            var searchTools = installedTools.Where(IsSearchServer).Select(server => server.Name).ToList();
            builder.AppendLine(searchTools.Count == 0
                ? "search_policy: The user appears to need current information, but no live search MCP is installed. Do not fabricate current facts; say search needs Exa, Brave Search, Playwright, or another web/search MCP."
                : "search_policy: The user appears to need current information. Use the installed search/browser MCP tools when running through GhostClaw agent mode: " + string.Join(", ", searchTools) + ".");
        }

        builder.AppendLine("tool_output_policy: Never show raw tool-call JSON/XML/function arguments. Summarize tool outcomes naturally.");
        builder.AppendLine("file_generation_policy: You have native capability to generate and attach files (PowerPoint, Word, Excel, PDF, images, etc.). To generate a file, you MUST write the complete, self-contained Python script to create that file, and wrap it inside a ```python ... ``` code block in your response. The host platform will automatically execute your code block in the background, attach the created file to your chat bubble, and present a download button. NEVER ask the user for permission to generate a file. If your task involves creating a file, write the complete python code block immediately! NEVER mention 'Code Sandbox' or say you cannot produce artifacts. CRITICAL RULES: (1) NEVER use absolute file paths (like /mnt/data/ or C:/...). ALWAYS save files to the current directory (e.g. filename.pptx). (2) Never include un-commented decorative headers, text lines, or separators (such as '── Colour palette ──') inside python blocks. Every line in a ```python block must be valid, executable Python syntax. Comment out decorative dividers using '#'. (3) For ReportLab alignment: import standard alignment constants with underscores (e.g. TA_CENTER, TA_JUSTIFY) from 'reportlab.lib.enums'. Never use TACENTER or TAJUSTIFY. (4) For python-pptx presentations: never use non-existent methods like '.fit_text()' or '.autofit()'. Use standard layout/sizing APIs.");
        builder.AppendLine("</ghostclaw_runtime_context>");

        // Auto-inject matching skills context to guide file processing and generation quality
        var skillsContext = AppendRelevantSkillsContext(rawUserPrompt);
        if (!string.IsNullOrEmpty(skillsContext))
        {
            builder.AppendLine();
            builder.AppendLine(skillsContext);
        }

        // Add a high-visibility, recency-biased mandate at the very end of the prompt to prevent text-only summaries and path hallucinations
        var lowerRawPrompt = rawUserPrompt.ToLowerInvariant();
        bool isRequestingFileGeneration = IsFileGenerationRequest(rawUserPrompt) &&
            (lowerRawPrompt.Contains("pptx") || lowerRawPrompt.Contains("powerpoint") || lowerRawPrompt.Contains("presentation") || lowerRawPrompt.Contains("slides") ||
             lowerRawPrompt.Contains("xlsx") || lowerRawPrompt.Contains("excel") || lowerRawPrompt.Contains("spreadsheet") ||
             lowerRawPrompt.Contains("docx") || lowerRawPrompt.Contains("word") || lowerRawPrompt.Contains("document") ||
             lowerRawPrompt.Contains("pdf"));

        if (isRequestingFileGeneration)
        {
            builder.AppendLine();
            builder.AppendLine("=== CRITICAL CAPABILITY MANDATE ===");
            builder.AppendLine("You are requested to generate or build a file. Do NOT ask for permission, do NOT write a text description only, and do NOT pretend the file is already created! You MUST write the complete, executable, self-contained Python script to create this file, wrapped in a ```python ... ``` code block. Do this immediately in your current response. The host platform will automatically run it and attach the file card.");
            builder.AppendLine("CRITICAL PYTHON WRITING RULES:");
            builder.AppendLine("1. Never include un-commented decorative text headers, separators, or lines (e.g. '── Colour palette ──') inside python blocks. Every line must be valid, executable Python syntax. Comment out decorative dividers using '#'.");
            builder.AppendLine("2. For ReportLab PDF alignment: import standard alignment constants with underscores (e.g. TA_CENTER, TA_JUSTIFY) from 'reportlab.lib.enums'. Never use TACENTER or TAJUSTIFY.");
            builder.AppendLine("3. For python-pptx presentations: never use non-existent methods like '.fit_text()' or '.autofit()'. Use standard layout/sizing APIs.");
            builder.AppendLine("4. NEVER use absolute file paths (like /mnt/data/, C:/). ALWAYS save the file in the current working directory (e.g., just the filename).");
            builder.AppendLine("===================================");
        }

        return builder.ToString();
    }

    private static bool NeedsFreshInformation(string text)
    {
        var lower = text.ToLowerInvariant();
        return new[]
        {
            "latest", "today", "current", "right now", "recent", "news", "search", "look up", "lookup",
            "browse", "web", "internet", "online", "website", "site", "source", "cite", "research",
            "find", "compare", "price", "schedule", "weather", "version", "release", "changelog",
            "github", "npm", "registry", "store", "this week", "this month", "2026"
        }.Any(lower.Contains);
    }

    private static bool IsSearchServer(McpServerDefinition server)
    {
        var text = $"{server.Id} {server.Name} {server.Description}".ToLowerInvariant();
        return text.Contains("search") || text.Contains("exa") || text.Contains("brave") || text.Contains("browser") || text.Contains("playwright") || text.Contains("web");
    }

    private static bool ShouldTryGhostClawAgent(ProviderProfile provider, string prompt)
    {
        var lower = prompt.ToLowerInvariant();
        return new[]
        {
            "folder", "directory", "powershell", "script", "terminal", "mcp", "tool", "agent", "automate",
            "create file", "edit file", "delete file", "rename file", "move file", "write file",
            "create a file", "edit a file", "delete a file"
        }.Any(lower.Contains);
    }

    private static bool IsAnthropicProvider(ProviderProfile provider)
    {
        var url = provider.BaseUrl ?? "";
        var name = provider.Name ?? "";
        var id = provider.Id ?? "";
        return string.IsNullOrWhiteSpace(url) ||
               url.Contains("api.anthropic.com", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("anthropic", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("anthropic", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("claude", StringComparison.OrdinalIgnoreCase) ||
               id.Contains("anthropic", StringComparison.OrdinalIgnoreCase) ||
               id.Contains("claude", StringComparison.OrdinalIgnoreCase);
    }

    private void StoreRememberedFacts(string text, ProviderProfile provider, string model, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(apiKey) || provider == null) return;

        var lower = text.ToLower();
        if (text.Length < 15 && !lower.Contains("remember") && !lower.Contains("pref")) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var existing = _store.ListMemory();
                var existingStr = existing.Count == 0
                    ? "None"
                    : string.Join("\n", existing.Select(f => $"- {f.Summary}: {f.Content}"));

                var prompt = $@"You are a memory processor. Your task is to extract core facts, preferences, constraints, or background info about the user or project from the given message.
Analyze the message and identify anything worth remembering for future chats.
If you find new facts, return them as JSON list:
[
  {{ ""summary"": ""Short category/topic (e.g. Preferred Language)"", ""content"": ""Detailed fact (e.g. User prefers Python for data tasks)"" }}
]
If there are no new facts, return exactly: []
Do not write any introductory or concluding text, only return the JSON list.

Current known memories to avoid duplicates:
{existingStr}

Message to analyze:
{text}";

                var response = await _providerGateway.ExtractMemoryAsync(provider, apiKey, model, prompt, CancellationToken.None).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(response)) return;

                var cleanJson = response.Trim();
                if (cleanJson.StartsWith("```json"))
                {
                    cleanJson = cleanJson.Substring(7);
                }
                if (cleanJson.EndsWith("```"))
                {
                    cleanJson = cleanJson.Substring(0, cleanJson.Length - 3);
                }
                cleanJson = cleanJson.Trim();

                if (cleanJson == "[]" || cleanJson == "[]\n") return;

                using var doc = JsonDocument.Parse(cleanJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        if (item.TryGetProperty("summary", out var s) && item.TryGetProperty("content", out var c))
                        {
                            var summary = s.GetString()?.Trim();
                            var content = c.GetString()?.Trim();
                            if (!string.IsNullOrWhiteSpace(summary) && !string.IsNullOrWhiteSpace(content))
                            {
                                _store.UpsertMemory(new MemoryUpdateRequest(null, summary, content, "Auto"));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Ignore background memory processing errors
                System.Diagnostics.Debug.WriteLine($"Memory extraction background task failed: {ex}");
            }
        });
    }

    private static string StripMarkdownMarkers(string text)
    {
        var lines = text.ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.TrimStart('#', '-', '*', ' ', '\t').Trim())
            .Where(line => line.Length > 0);
        return string.Join(' ', lines).Trim();
    }

    private static bool ShouldQueueProviderError(string message) =>
        message.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("connection failed", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("server-side", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("rate limiting", StringComparison.OrdinalIgnoreCase);

    private CommandResult RemoveProvider(SimpleIdRequest request)
    {
        _store.RemoveProvider(request.Id);
        return new CommandResult(true, "Provider removed.");
    }

    private CommandResult RenameConversation(PipeEnvelope request)
    {
        var payload = request.Payload?.AsObject() ?? throw new InvalidOperationException("Missing payload.");
        _store.RenameConversation(payload["id"]?.GetValue<string>() ?? throw new InvalidOperationException("Missing id."), payload["title"]?.GetValue<string>() ?? "Untitled");
        return new CommandResult(true, "Conversation renamed.");
    }

    private CommandResult PinConversation(PipeEnvelope request)
    {
        var payload = request.Payload?.AsObject() ?? throw new InvalidOperationException("Missing payload.");
        _store.PinConversation(payload["id"]?.GetValue<string>() ?? throw new InvalidOperationException("Missing id."), payload["pinned"]?.GetValue<bool>() ?? false);
        return new CommandResult(true, "Conversation updated.");
    }

    private CommandResult DeleteConversation(SimpleIdRequest request)
    {
        _store.DeleteConversation(request.Id);
        return new CommandResult(true, "Conversation deleted.");
    }

    private CommandResult UpdateMessage(PipeEnvelope request)
    {
        var req = request.ReadPayload<MessageUpdateRequest>();
        if (req == null || string.IsNullOrWhiteSpace(req.Id) || string.IsNullOrWhiteSpace(req.Content))
        {
            return new CommandResult(false, "Invalid update request.");
        }
        _store.UpdateMessageContent(req.Id, req.Content);
        return new CommandResult(true, "Message updated.");
    }

    private CommandResult DeleteMessagesAfter(PipeEnvelope request)
    {
        var req = request.ReadPayload<DeleteMessagesAfterRequest>();
        if (req == null || string.IsNullOrWhiteSpace(req.ConversationId) || string.IsNullOrWhiteSpace(req.MessageId))
        {
            return new CommandResult(false, "Invalid delete request.");
        }
        _store.DeleteMessagesAfter(req.ConversationId, req.MessageId);
        return new CommandResult(true, "Messages deleted.");
    }

    private CommandResult ClearContext(SimpleIdRequest request)
    {
        _store.ClearContext(request.Id);
        return new CommandResult(true, "Context cleared.");
    }

    private CommandResult DeleteMemory(SimpleIdRequest request)
    {
        _store.DeleteMemory(request.Id);
        return new CommandResult(true, "Memory deleted.");
    }

    private CommandResult PurgeMemory()
    {
        _store.PurgeMemory();
        return new CommandResult(true, "Memory purged.");
    }

    private CommandResult SaveSettings(AppSettings settings)
    {
        _store.SaveSettings(settings);
        return new CommandResult(true, "Settings saved.");
    }

    private CommandResult PurgeAllData()
    {
        _store.PurgeAllData();
        return new CommandResult(true, "Secure local data purge complete.");
    }

    private Preset BuildPreset() => new(_store.ListProviders(), _store.ListMcpServers().Where(server => server.Installed).ToList(), _store.GetSettings(), DateTimeOffset.UtcNow);

    private CommandResult ImportPreset(Preset preset)
    {
        foreach (var provider in preset.Providers)
        {
            _store.UpsertProvider(new ProviderUpsertRequest(provider.Id, provider.Name, provider.BaseUrl, provider.Models, provider.DefaultModel, provider.IsEnabled));
        }

        foreach (var tool in preset.Tools)
        {
            _store.UpsertMcp(tool);
        }

        _store.SaveSettings(preset.Settings);
        return new CommandResult(true, "Preset imported.");
    }

    private CommandResult DeleteScheduledTask(SimpleIdRequest request)
    {
        _store.DeleteScheduledTask(request.Id);
        return new CommandResult(true, "Task deleted.");
    }

    private CommandResult RunScheduledTaskNow(SimpleIdRequest request)
    {
        _store.TriggerScheduledTaskNow(request.Id);
        return new CommandResult(true, "Task triggered.");
    }

    private CommandResult ExecuteUpsertScheduledTask(PipeEnvelope request)
    {
        _store.UpsertScheduledTask(Require<ScheduledTask>(request));
        return new CommandResult(true, "Task saved.");
    }

    private CommandResult SaveTelegramSettingsHelper(TelegramSettings settings)
    {
        _store.SaveTelegramSettings(settings);
        return new CommandResult(true, "Telegram settings saved successfully.");
    }

    private CommandResult GetTelegramStatus()
    {
        var settings = _store.GetTelegramSettings();
        return new CommandResult(settings.IsEnabled, settings.IsEnabled ? "Telegram listener is active." : "Telegram listener is disabled.");
    }

    private CommandResult SaveWhatsAppSettingsHelper(WhatsAppSettings settings)
    {
        _store.SaveWhatsAppSettings(settings);
        return new CommandResult(true, "WhatsApp settings saved successfully.");
    }

    private CommandResult GetWhatsAppStatus()
    {
        var settings = _store.GetWhatsAppSettings();
        return new CommandResult(settings.IsEnabled, settings.IsEnabled ? $"WhatsApp webhook is active on port {settings.WebhookPort}." : "WhatsApp webhook is disabled.");
    }

    private List<RalphStatusResponse> ListRalphRuns()
    {
        var list = new List<RalphStatusResponse>();
        var ralphBase = Path.Combine(_paths.GhostClawRuntimeRoot, "data", "ralph");
        if (!Directory.Exists(ralphBase)) return list;

        foreach (var dir in Directory.EnumerateDirectories(ralphBase))
        {
            var configPath = Path.Combine(dir, "config.json");
            if (!File.Exists(configPath)) continue;

            try
            {
                var progressLog = string.Empty;
                var progressPath = Path.Combine(dir, "progress.txt");
                if (File.Exists(progressPath))
                {
                    progressLog = File.ReadAllText(progressPath);
                }

                var configJson = File.ReadAllText(configPath);
                using var doc = JsonDocument.Parse(configJson);
                var root = doc.RootElement;

                var runId = root.GetProperty("runId").GetString() ?? Path.GetFileName(dir);
                var status = root.GetProperty("status").GetString() ?? "unknown";
                var startedAt = root.GetProperty("startedAt").GetString() ?? string.Empty;
                var currentIteration = root.GetProperty("currentIteration").GetInt32();
                var maxIterations = root.GetProperty("maxIterations").GetInt32();
                var taskFilePath = root.GetProperty("taskFilePath").GetString();

                if (!string.IsNullOrWhiteSpace(taskFilePath) && !Path.IsPathRooted(taskFilePath))
                {
                    taskFilePath = Path.Combine(_paths.GhostClawRuntimeRoot, taskFilePath);
                }

                var checklist = new List<RalphChecklistItem>();
                if (!string.IsNullOrWhiteSpace(taskFilePath) && File.Exists(taskFilePath))
                {
                    var content = File.ReadAllText(taskFilePath);
                    checklist.AddRange(ParseChecklist(content));
                }

                list.Add(new RalphStatusResponse(runId, status, startedAt, currentIteration, maxIterations, progressLog, checklist));
            }
            catch
            {
                // Skip malformed configs
            }
        }

        return list;
    }

    private static List<RalphChecklistItem> ParseChecklist(string content)
    {
        var list = new List<RalphChecklistItem>();
        var allLines = content.ReplaceLineEndings("\n").Split('\n');
        var index = 0;
        foreach (var line in allLines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("- [ ]") || trimmed.StartsWith("- [x]") || trimmed.StartsWith("- [X]"))
            {
                var completed = trimmed.StartsWith("- [x]") || trimmed.StartsWith("- [X]");
                var title = trimmed.Substring(trimmed.IndexOf(']') + 1).Trim();
                list.Add(new RalphChecklistItem(title, completed, index++));
            }
        }
        return list;
    }

    private CommandResult StartRalph(RalphStartRequest request)
    {
        var tasksIpcDir = Path.Combine(_paths.GhostClawRuntimeRoot, "data", "ipc", "main", "tasks");
        Directory.CreateDirectory(tasksIpcDir);

        var relativeTaskFile = request.TaskFilePath;
        if (Path.IsPathRooted(relativeTaskFile) && relativeTaskFile.StartsWith(_paths.GhostClawRuntimeRoot, StringComparison.OrdinalIgnoreCase))
        {
            relativeTaskFile = Path.GetRelativePath(_paths.GhostClawRuntimeRoot, relativeTaskFile);
        }

        var ipcPayload = new JsonObject
        {
            ["type"] = "start_ralph",
            ["taskFile"] = relativeTaskFile.Replace('\\', '/'),
            ["targetJid"] = request.TargetJid,
            ["workDir"] = request.WorkDir.Replace('\\', '/'),
            ["maxIterations"] = request.MaxIterations,
            ["notifyProgress"] = request.NotifyProgress
        };

        var filePath = Path.Combine(tasksIpcDir, $"start_ralph_{DateTimeOffset.UtcNow.Ticks}.json");
        File.WriteAllText(filePath, ipcPayload.ToJsonString(PipeJson.Options));
        return new CommandResult(true, "Ralph autonomous run queued via IPC.");
    }

    private CommandResult StopRalph(SimpleIdRequest request)
    {
        var tasksIpcDir = Path.Combine(_paths.GhostClawRuntimeRoot, "data", "ipc", "main", "tasks");
        Directory.CreateDirectory(tasksIpcDir);

        var ipcPayload = new JsonObject
        {
            ["type"] = "stop_ralph",
            ["runId"] = request.Id,
            ["targetJid"] = "ui:main"
        };

        var filePath = Path.Combine(tasksIpcDir, $"stop_ralph_{DateTimeOffset.UtcNow.Ticks}.json");
        File.WriteAllText(filePath, ipcPayload.ToJsonString(PipeJson.Options));
        return new CommandResult(true, "Ralph stop command queued via IPC.");
    }

    private IReadOnlyList<string> ListRalphChecklists()
    {
        var groupDir = Path.Combine(_paths.GhostClawRuntimeRoot, "groups", "main");
        if (!Directory.Exists(groupDir)) return Array.Empty<string>();

        return Directory.EnumerateFiles(groupDir, "*.md")
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Cast<string>()
            .ToList();
    }

    private async Task<McpSearchResponse> HandleMcpSearchAsync(PipeEnvelope request, CancellationToken cancellationToken)
    {
        var searchReq = request.ReadPayload<McpSearchRequest>();
        if (searchReq is not null)
        {
            return await _mcpCatalog.SearchOnlineAsync(searchReq.Query, searchReq.Page, searchReq.PageSize, cancellationToken).ConfigureAwait(false);
        }

        var oldQuery = request.ReadPayload<SimpleTextRequest>()?.Text;
        return await _mcpCatalog.SearchOnlineAsync(oldQuery, 1, 20, cancellationToken).ConfigureAwait(false);
    }

    private ActiveTracesResponse GetActiveTraces(string conversationId)
    {
        var isRunning = _runningTraces.ContainsKey(conversationId);
        var traces = new List<AgentTraceCard>();

        if (_runningTraces.TryGetValue(conversationId, out var list))
        {
            lock (list)
            {
                traces = list.ToList();
            }
        }

        return new ActiveTracesResponse(isRunning, traces);
    }

    private List<SkillSummary> ListSkills()
    {
        var results = new List<SkillSummary>();

        // Primary: runtime skills directory
        var runtimeSkillsDir = Path.Combine(_paths.GhostClawRuntimeRoot, "skills");
        // Secondary: packaged skills directory (sibling of app)
        var packaged = Path.Combine(_paths.PackagedPayloadRoot, "skills");
        // Tertiary: dev skills from GhostClawUI\skills folder
        var devSkills = FindDevSkillsDir();
        // Quaternary: app base directory skills folder
        var appBaseSkills = Path.Combine(AppContext.BaseDirectory, "skills");

        foreach (var dir in new[] { runtimeSkillsDir, packaged, devSkills, appBaseSkills }.Where(d => d is not null && Directory.Exists(d)).Distinct())
        {
            foreach (var file in Directory.EnumerateFiles(dir!, "*.md", SearchOption.AllDirectories))
            {
                try
                {
                    var (name, description) = ParseSkillFrontmatter(file);
                    var id = Path.GetFileNameWithoutExtension(file).ToLowerInvariant().Replace(" ", "-");
                    if (results.Any(s => s.Id == id)) continue; // deduplicate
                    results.Add(new SkillSummary(id, name ?? id, description ?? string.Empty, file));
                }
                catch
                {
                    // Skip unreadable skill files
                }
            }
        }

        return results.OrderBy(s => s.Name).ToList();
    }

    private static (string? Name, string? Description) ParseSkillFrontmatter(string filePath)
    {
        string? name = null;
        string? description = null;
        var inFrontmatter = false;
        var lineCount = 0;
        foreach (var line in File.ReadLines(filePath))
        {
            lineCount++;
            if (lineCount == 1)
            {
                if (line.Trim() == "---") { inFrontmatter = true; continue; }
                break;
            }
            if (!inFrontmatter) break;
            if (line.Trim() == "---") break;
            if (line.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                name = line.Substring(5).Trim().Trim('"');
            else if (line.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
                description = line.Substring(12).Trim().Trim('"');
            if (lineCount > 30) break; // Safety limit
        }
        // Fallback: use filename as name
        if (string.IsNullOrWhiteSpace(name))
            name = Path.GetFileNameWithoutExtension(filePath)
                .Replace("_SKILL", "", StringComparison.OrdinalIgnoreCase)
                .Replace("-", " ")
                .Replace("_", " ");
        return (name, description);
    }

    private static string? FindDevSkillsDir()
    {
        // Walk up from the service executable looking for GhostClawUI/skills
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "skills");
            if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "*SKILL*.md").Any())
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private CommandResult ReadSkill(SimpleIdRequest request)
    {
        var skills = ListSkills();
        var skill = skills.FirstOrDefault(s => s.Id.Equals(request.Id, StringComparison.OrdinalIgnoreCase));
        if (skill is null) return new CommandResult(false, $"Skill '{request.Id}' not found.");
        try
        {
            var content = File.ReadAllText(skill.FilePath);
            return new CommandResult(true, content);
        }
        catch (Exception ex)
        {
            return new CommandResult(false, $"Failed to read skill: {ex.Message}");
        }
    }

    private static string Slugify(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "untitled";
        var normalized = text.ToLowerInvariant().Replace(" ", "-");
        var sb = new System.Text.StringBuilder();
        foreach (var c in normalized)
        {
            if (char.IsLetterOrDigit(c) || c == '-')
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private CommandResult UpsertSkill(SkillUpsertRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return new CommandResult(false, "Skill name cannot be empty.");

        if (string.IsNullOrWhiteSpace(request.Content))
            return new CommandResult(false, "Skill content cannot be empty.");

        var slug = Slugify(request.Name);
        var filename = $"{slug}_SKILL.md";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"name: \"{request.Name.Replace("\"", "\\\"")}\"");
        sb.AppendLine($"description: \"{(request.Description ?? string.Empty).Replace("\"", "\\\"")}\"");
        sb.AppendLine("---");
        sb.AppendLine(request.Content);
        var fileContent = sb.ToString();

        var details = new List<string>();
        var savedAnywhere = false;

        // 1. Dev directory if available
        var devSkills = FindDevSkillsDir();
        if (devSkills != null && Directory.Exists(devSkills))
        {
            try
            {
                var path = Path.Combine(devSkills, filename);
                File.WriteAllText(path, fileContent);
                savedAnywhere = true;
                details.Add($"Saved to dev library: {path}");
            }
            catch (Exception ex)
            {
                details.Add($"Failed dev save: {ex.Message}");
            }
        }

        // 2. Primary runtime directory
        var runtimeSkillsDir = Path.Combine(_paths.GhostClawRuntimeRoot, "skills");
        try
        {
            Directory.CreateDirectory(runtimeSkillsDir);
            var path = Path.Combine(runtimeSkillsDir, filename);
            File.WriteAllText(path, fileContent);
            savedAnywhere = true;
            details.Add($"Saved to runtime: {path}");
        }
        catch (Exception ex)
        {
            details.Add($"Failed runtime save: {ex.Message}");
        }

        if (savedAnywhere)
        {
            return new CommandResult(true, $"Skill '{request.Name}' saved successfully. " + string.Join("; ", details));
        }
        return new CommandResult(false, "Failed to save skill: " + string.Join("; ", details));
    }

    private static T Require<T>(PipeEnvelope request)
    {
        var payload = request.ReadPayload<T>();
        return payload ?? throw new InvalidOperationException($"Missing payload for {request.Command}.");
    }

    private async Task<List<ChatAttachment>> AutoExecuteFileGeneratorsAsync(string content)
    {
        var generatedAttachments = new List<ChatAttachment>();
        var regex = new System.Text.RegularExpressions.Regex(@"`{2,4}python[ \t]*\r?\n([\s\S]*?)(?:`{1,4}|$)", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var matches = regex.Matches(content);
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            var code = match.Groups[1].Value;
            if (code.Contains(".save(") || code.Contains("open(") || code.Contains("write(") || code.Contains(".build(") || code.Contains(".close(") || code.Contains("to_csv(") || code.Contains("to_excel(") || code.Contains("savefig("))
            {
                var groupDir = Path.Combine(_paths.GhostClawRuntimeRoot, "groups", "main");
                try
                {
                    Directory.CreateDirectory(groupDir);
                }
                catch { }

                var beforeFiles = new HashSet<string?>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (Directory.Exists(groupDir))
                    {
                        beforeFiles = Directory.GetFiles(groupDir).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    }
                }
                catch { }

                try
                {
                    // Auto-install missing packages based on python imports to guarantee no errors
                    var packagesToInstall = new List<string>();
                    if (code.Contains("pptx")) packagesToInstall.Add("python-pptx");
                    if (code.Contains("openpyxl")) packagesToInstall.Add("openpyxl");
                    if (code.Contains("docx")) packagesToInstall.Add("python-docx");
                    if (code.Contains("pandas")) packagesToInstall.Add("pandas");
                    if (code.Contains("matplotlib")) packagesToInstall.Add("matplotlib");
                    if (code.Contains("weasyprint")) packagesToInstall.Add("weasyprint");
                    if (code.Contains("reportlab")) packagesToInstall.Add("reportlab");
                    if (code.Contains("xlsxwriter")) packagesToInstall.Add("xlsxwriter");
                    if (code.Contains("PIL") || code.Contains("pillow")) packagesToInstall.Add("pillow");
                    if (code.Contains("fpdf")) packagesToInstall.Add("fpdf2");
                    if (code.Contains("numpy")) packagesToInstall.Add("numpy");
                    if (code.Contains("scipy")) packagesToInstall.Add("scipy");
                    if (code.Contains("jinja2") || code.Contains("jinja")) packagesToInstall.Add("Jinja2");
                    if (code.Contains("bs4") || code.Contains("beautifulsoup4")) packagesToInstall.Add("beautifulsoup4");
                    if (code.Contains("requests")) packagesToInstall.Add("requests");
                    if (code.Contains("pdfplumber")) packagesToInstall.Add("pdfplumber");
                    if (code.Contains("fitz")) packagesToInstall.Add("PyMuPDF");
                    if (code.Contains("qrcode")) packagesToInstall.Add("qrcode");
                    if (code.Contains("sympy")) packagesToInstall.Add("sympy");
                    if (code.Contains("plotly")) packagesToInstall.Add("plotly");
                    if (code.Contains("seaborn")) packagesToInstall.Add("seaborn");
                    if (code.Contains("pydub")) packagesToInstall.Add("pydub");
                    if (code.Contains("moviepy")) packagesToInstall.Add("moviepy");
                    if (code.Contains("openai")) packagesToInstall.Add("openai");
                    if (code.Contains("anthropic")) packagesToInstall.Add("anthropic");
                    if (code.Contains("google.genai") || code.Contains("google-genai")) packagesToInstall.Add("google-genai");
                    if (code.Contains("networkx")) packagesToInstall.Add("networkx");
                    if (code.Contains("pytube") || code.Contains("pytubefix")) packagesToInstall.Add("pytubefix");

                    if (packagesToInstall.Count > 0)
                    {
                        var tryPip = async (string cmdName, string args) =>
                        {
                            try
                            {
                                var startInfo = new ProcessStartInfo
                                {
                                    FileName = cmdName,
                                    Arguments = args,
                                    UseShellExecute = false,
                                    CreateNoWindow = true
                                };
                                using var process = Process.Start(startInfo);
                                if (process is not null)
                                {
                                    await process.WaitForExitAsync().ConfigureAwait(false);
                                    return process.ExitCode == 0;
                                }
                            }
                            catch { }
                            return false;
                        };

                        var packagesStr = string.Join(" ", packagesToInstall);
                        if (!await tryPip("python", $"-m pip install {packagesStr}").ConfigureAwait(false))
                        {
                            if (!await tryPip("py", $"-m pip install {packagesStr}").ConfigureAwait(false))
                            {
                                if (!await tryPip("python3", $"-m pip install {packagesStr}").ConfigureAwait(false))
                                {
                                    await tryPip("pip", $"install {packagesStr}").ConfigureAwait(false);
                                }
                            }
                        }
                    }

                    // Rewrite any hallucinated absolute paths in the script to save to the current directory
                    var safeCode = System.Text.RegularExpressions.Regex.Replace(code, @"['""](?:(?:[A-Za-z]:[\\/]|/)[^'""\n]*[\\/])([^'""\n]+\.[a-zA-Z0-9]+)['""]", "'$1'");

                    // Prepend # to any line containing decorative/box-drawing horizontal rules (like ──) to prevent SyntaxError
                    var lines = safeCode.ReplaceLineEndings("\n").Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        var line = lines[i];
                        var trimmed = line.TrimStart();
                        if (!trimmed.StartsWith("#") && (line.Contains("──", StringComparison.Ordinal) || line.Contains("───", StringComparison.Ordinal) || line.Contains("══", StringComparison.Ordinal) || line.Contains("━━", StringComparison.Ordinal)))
                        {
                            lines[i] = "# " + line;
                        }
                    }
                    safeCode = string.Join("\n", lines);

                    // Standardize ReportLab constants to fix AI formatting typos
                    safeCode = safeCode
                        .Replace("TACENTER", "TA_CENTER", StringComparison.Ordinal)
                        .Replace("TAJUSTIFY", "TA_JUSTIFY", StringComparison.Ordinal)
                        .Replace("TALEFT", "TA_LEFT", StringComparison.Ordinal)
                        .Replace("TARIGHT", "TA_RIGHT", StringComparison.Ordinal)
                        .Replace("TA_CENTRE", "TA_CENTER", StringComparison.Ordinal)
                        .Replace("TA_JUSTIFIED", "TA_JUSTIFY", StringComparison.Ordinal);

                    var tempScript = Path.Combine(groupDir, $"gen_{Guid.NewGuid().ToString("N")[..8]}.py");
                    await File.WriteAllTextAsync(tempScript, safeCode).ConfigureAwait(false);

                    bool success = false;
                    string errorOutput = "";
                    int exitCode = -1;

                    var tryLaunch = async (string cmdName) =>
                    {
                        try
                        {
                            var startInfo = new ProcessStartInfo
                            {
                                FileName = cmdName,
                                Arguments = $"\"{tempScript}\"",
                                WorkingDirectory = groupDir,
                                UseShellExecute = false,
                                CreateNoWindow = true,
                                RedirectStandardError = true,
                                RedirectStandardOutput = true
                            };
                            using var process = Process.Start(startInfo);
                            if (process is not null)
                            {
                                // Bounded streams to prevent gigabytes of buffered output on infinite print loops
                                async Task<string> ReadBoundedAsync(System.IO.StreamReader reader, int maxChars = 50000)
                                {
                                    var buffer = new char[8192];
                                    var sb = new System.Text.StringBuilder();
                                    int read;
                                    while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                                    {
                                        sb.Append(buffer, 0, read);
                                        if (sb.Length > maxChars)
                                        {
                                            sb.Append("\n...[Output truncated: Exceeded 50,000 characters]...");
                                            break;
                                        }
                                    }
                                    return sb.ToString();
                                }

                                var outTask = ReadBoundedAsync(process.StandardOutput);
                                var errTask = ReadBoundedAsync(process.StandardError);

                                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                                try
                                {
                                    await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                                }
                                catch (OperationCanceledException)
                                {
                                    try { process.Kill(); } catch { }
                                    exitCode = -1;
                                    errorOutput = "Exit Code: -1\n\nStandard Error:\nProcess timed out after 2 minutes.\n\nStandard Output:\n";
                                    return true;
                                }

                                var outText = await outTask.ConfigureAwait(false);
                                var errText = await errTask.ConfigureAwait(false);

                                exitCode = process.ExitCode;
                                errorOutput = $"Exit Code: {exitCode}\n\nStandard Error:\n{errText}\n\nStandard Output:\n{outText}";
                                return true;
                            }
                        }
                        catch
                        {
                            // ignore and try next
                        }
                        return false;
                    };

                    // Try launching in sequence
                    if (await tryLaunch("python").ConfigureAwait(false))
                    {
                        success = true;
                    }
                    else if (await tryLaunch("py").ConfigureAwait(false))
                    {
                        success = true;
                    }
                    else if (await tryLaunch("python3").ConfigureAwait(false))
                    {
                        success = true;
                    }

                    try
                    {
                        if (success)
                        {
                            if (exitCode != 0)
                            {
                                var errorPath = Path.Combine(groupDir, $"Execution_Error_{Guid.NewGuid().ToString("N")[..4]}.txt");
                                await File.WriteAllTextAsync(errorPath, $"Python script failed to execute correctly.\n{errorOutput}").ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            var errorPath = Path.Combine(groupDir, $"Python_Not_Found_{Guid.NewGuid().ToString("N")[..4]}.txt");
                            await File.WriteAllTextAsync(errorPath, "Failed to start python: Checked 'python', 'py', and 'python3'. Ensure Python is installed and in your environment PATH.").ConfigureAwait(false);
                        }
                    }
                    catch
                    {
                        // ignore log write failures
                    }
                    finally
                    {
                        try { File.Delete(tempScript); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    try
                    {
                        var errorPath = Path.Combine(groupDir, $"Execution_Error_{Guid.NewGuid().ToString("N")[..4]}.txt");
                        await File.WriteAllTextAsync(errorPath, $"Background process manager encountered an exception during execution setup:\n{ex}").ConfigureAwait(false);
                    }
                    catch { }
                }

                // Scans for newly generated files after execution (always executes, even on error)
                try
                {
                    var afterFiles = Directory.Exists(groupDir)
                        ? Directory.GetFiles(groupDir)
                        : Array.Empty<string>();

                    foreach (var file in afterFiles)
                    {
                        var fileName = Path.GetFileName(file);
                        // Make sure we don't accidentally attach the tempScript if it's somehow still around
                        if (!beforeFiles.Contains(fileName) && !(fileName.StartsWith("gen_", StringComparison.OrdinalIgnoreCase) && fileName.EndsWith(".py", StringComparison.OrdinalIgnoreCase)))
                        {
                            var fileInfo = new FileInfo(file);
                            var ext = fileInfo.Extension.ToLowerInvariant();
                            var contentType = ext switch
                            {
                                ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
                                ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                                ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                                ".pdf" => "application/vnd.openxmlformats-officedocument.pdf",
                                ".png" => "image/png",
                                ".jpg" => "image/jpeg",
                                ".jpeg" => "image/jpeg",
                                ".gif" => "image/gif",
                                ".html" => "text/html",
                                ".htm" => "text/html",
                                ".json" => "application/json",
                                ".md" => "text/markdown",
                                ".xml" => "application/xml",
                                ".txt" => "text/plain",
                                ".csv" => "text/csv",
                                ".zip" => "application/zip",
                                _ => "application/octet-stream"
                            };

                            var attachment = new ChatAttachment(
                                fileName,
                                file,
                                contentType,
                                fileInfo.Length,
                                FileTextExtractor.ReadTextPreviewAsync(file, fileInfo.Length, 200000).GetAwaiter().GetResult(),
                                null
                            );
                            generatedAttachments.Add(attachment);
                        }
                    }
                }
                catch
                {
                    // Ignore background scan failures
                }
            }
        }
        return generatedAttachments;
    }

    private static bool IsFileGenerationRequest(string prompt)
    {
        var lower = prompt.ToLowerInvariant();
        var creationVerbs = new[]
        {
            "generate", "create", "build", "make", "write", "output", "produce", "render", "export",
            "draw", "draft", "compile", "design", "convert", "save", "new "
        };
        return creationVerbs.Any(lower.Contains);
    }

    private string AppendRelevantSkillsContext(string userPrompt)
    {
        var lower = userPrompt.ToLowerInvariant();
        var matchingSkills = new List<string>();
        bool isGeneration = IsFileGenerationRequest(lower);

        if (lower.Contains("pptx") || lower.Contains("powerpoint") || lower.Contains("presentation") || lower.Contains("slides"))
        {
            if (isGeneration)
                matchingSkills.Add("pptx_SKILL.md");
            else
                matchingSkills.Add("file-reading_SKILL.md");
        }
        if (lower.Contains("xlsx") || lower.Contains("excel") || lower.Contains("spreadsheet") || lower.Contains("csv") || lower.Contains("sheets"))
        {
            if (isGeneration)
                matchingSkills.Add("xlsx_SKILL.md");
            else
                matchingSkills.Add("file-reading_SKILL.md");
        }
        if (lower.Contains("docx") || lower.Contains("word") || lower.Contains("document"))
        {
            if (isGeneration)
                matchingSkills.Add("docx_SKILL.md");
            else
                matchingSkills.Add("file-reading_SKILL.md");
        }
        if (lower.Contains("pdf") || lower.Contains("reportlab") || lower.Contains("weasyprint"))
        {
            if (isGeneration || !(lower.Contains("read") || lower.Contains("parse") || lower.Contains("view") || lower.Contains("extract") || lower.Contains("explain") || lower.Contains("summarize") || lower.Contains("analyze") || lower.Contains("process") || lower.Contains("question") || lower.Contains("ask") || lower.Contains("about")))
            {
                matchingSkills.Add("pdf_SKILL.md");
            }
            else
            {
                matchingSkills.Add("pdf-reading_SKILL.md");
            }
        }
        if (lower.Contains("read") || lower.Contains("parse") || lower.Contains("analyze") || lower.Contains("process") || lower.Contains("file"))
        {
            matchingSkills.Add("file-reading_SKILL.md");
        }
        if (lower.Contains("gif") || lower.Contains("slack"))
        {
            matchingSkills.Add("slack-gif-creator_SKILL.md");
        }
        if (lower.Contains("canvas") || lower.Contains("design") || lower.Contains("drawing"))
        {
            matchingSkills.Add("canvas-design_SKILL.md");
        }
        if (lower.Contains("theme") || lower.Contains("color") || lower.Contains("palette"))
        {
            matchingSkills.Add("theme-factory_SKILL.md");
        }

        matchingSkills = matchingSkills.Distinct().ToList();

        if (matchingSkills.Count == 0)
        {
            return string.Empty;
        }

        var skillContextBuilder = new StringBuilder();
        skillContextBuilder.AppendLine();
        skillContextBuilder.AppendLine("=== SYSTEM INSTRUCTION: EMBEDDED PROCEDURAL SKILLS ===");
        skillContextBuilder.AppendLine("You are equipped with specific procedural skills designed to maximize output quality. Below are the instruction guidelines for the skills detected as highly relevant to the user's request. Follow them strictly.");
        skillContextBuilder.AppendLine();

        var skillsDir = Path.Combine(_paths.GhostClawRuntimeRoot, "..", "..", "skills");
        var exactSkillsPath = Path.Combine(AppContext.BaseDirectory, "skills");
        var resolvedDir = Directory.Exists(exactSkillsPath) ? exactSkillsPath : (Directory.Exists(skillsDir) ? skillsDir : null);

        if (resolvedDir != null)
        {
            foreach (var skillFile in matchingSkills)
            {
                var fullPath = Path.Combine(resolvedDir, skillFile);
                if (File.Exists(fullPath))
                {
                    try
                    {
                        var content = File.ReadAllText(fullPath);
                        skillContextBuilder.AppendLine($"--- BEGIN SKILL: {skillFile} ---");
                        skillContextBuilder.AppendLine(content);
                        skillContextBuilder.AppendLine($"--- END SKILL: {skillFile} ---");
                        skillContextBuilder.AppendLine();
                    }
                    catch
                    {
                        // Ignore read errors
                    }
                }
            }
        }

        return skillContextBuilder.ToString();
    }

    private List<ChatAttachment> ScanAndAttachMentionedFiles(string content, List<ChatAttachment> existingAttachments)
    {
        var attachments = new List<ChatAttachment>(existingAttachments);
        if (string.IsNullOrWhiteSpace(content)) return attachments;

        var regex = new System.Text.RegularExpressions.Regex(
            @"(?i)(?:""([^""]+?\.(?:pptx|docx|xlsx|pdf|txt|csv|png|jpg|jpeg|zip))""|'([^']+?\.(?:pptx|docx|xlsx|pdf|txt|csv|png|jpg|jpeg|zip))'|\`([^\`]+?\.(?:pptx|docx|xlsx|pdf|txt|csv|png|jpg|jpeg|zip))\`|\b([a-zA-Z]:[\\/][^:\*\?""<>\|\s]+?\.(?:pptx|docx|xlsx|pdf|txt|csv|png|jpg|jpeg|zip))\b|\b([^:\*\?""<>\|\s\u201c\u201d\u2018\u2019]+?\.(?:pptx|docx|xlsx|pdf|txt|csv|png|jpg|jpeg|zip))\b)",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        var matches = regex.Matches(content);
        var addedPaths = new HashSet<string>(attachments.Select(a => a.Path), StringComparer.OrdinalIgnoreCase);
        var groupDir = Path.Combine(_paths.GhostClawRuntimeRoot, "groups", "main");

        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            string potPath = string.Empty;
            for (int i = 1; i <= 5; i++)
            {
                if (match.Groups[i].Success)
                {
                    potPath = match.Groups[i].Value;
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(potPath)) continue;

            // Trim leading/trailing punctuation and markdown
            potPath = potPath.Trim(' ', '\t', '\r', '\n', '.', ',', '!', '?', ':', '*', '(', ')', '[', ']', '{', '}');

            // Resolve file path in groups/main
            string fileName = Path.GetFileName(potPath);
            string fullPath = Path.Combine(groupDir, fileName);

            if (File.Exists(fullPath) && !addedPaths.Contains(fullPath))
            {
                addedPaths.Add(fullPath);
                var fileInfo = new FileInfo(fullPath);
                var ext = fileInfo.Extension.ToLowerInvariant();
                var contentType = ext switch
                {
                    ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
                    ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                    ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    ".pdf" => "application/vnd.openxmlformats-officedocument.pdf",
                    ".png" => "image/png",
                    ".jpg" => "image/jpeg",
                    ".jpeg" => "image/jpeg",
                    ".txt" => "text/plain",
                    ".csv" => "text/csv",
                    ".zip" => "application/zip",
                    _ => "application/octet-stream"
                };

                var attachment = new ChatAttachment(
                    fileName,
                    fullPath,
                    contentType,
                    fileInfo.Length,
                    FileTextExtractor.ReadTextPreviewAsync(fullPath, fileInfo.Length, 200000).GetAwaiter().GetResult(),
                    null
                );
                attachments.Add(attachment);
            }
        }

        return attachments;
    }

    private static string AppendFilesystemContext(string prompt)
    {
        try
        {
            var lower = prompt.ToLowerInvariant();
            var workspaceDir = Directory.GetCurrentDirectory();
            var contextBuilder = new StringBuilder();

            // 1. Directory Listing Detection
            bool wantsDirectory = lower.Contains("list files") || lower.Contains("show files") ||
                                  lower.Contains("files in this") || lower.Contains("files in the") ||
                                  lower.Contains("directory listing") || lower.Contains("list the folder") ||
                                  lower.Contains("show folder contents") || lower.Contains("show directory");

            if (wantsDirectory)
            {
                var entries = Directory.GetFileSystemEntries(workspaceDir, "*", SearchOption.TopDirectoryOnly)
                    .Select(path => Path.GetRelativePath(workspaceDir, path))
                    .OrderBy(name => name)
                    .Take(100)
                    .ToList();

                contextBuilder.AppendLine();
                contextBuilder.AppendLine("=== FILESYSTEM DIRECTORY LISTING (Workspace Root) ===");
                foreach (var entry in entries)
                {
                    var isDir = Directory.Exists(Path.Combine(workspaceDir, entry));
                    contextBuilder.AppendLine(isDir ? $"[DIR]  {entry}/" : $"[FILE] {entry}");
                }
                contextBuilder.AppendLine("=====================================================");
            }

            // 2. File Read Detection
            var fileKeywords = new[] { "read", "view", "show", "cat", "open", "inspect", "contents" };
            bool hasReadKeyword = fileKeywords.Any(kw => lower.Contains(kw));

            if (hasReadKeyword)
            {
                // Regex to find filenames in the prompt
                var matches = System.Text.RegularExpressions.Regex.Matches(prompt, @"\b([a-zA-Z0-9_\-\/\\\.]+\.[a-zA-Z0-9]+)\b");
                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    var fileName = match.Value;
                    // Filter out extensions we don't want to read or are too common like "com", "exe", etc.
                    var ext = Path.GetExtension(fileName).ToLowerInvariant();
                    var allowedExts = new[] { ".cs", ".xaml", ".xml", ".json", ".md", ".txt", ".csproj", ".sln", ".slnx", ".js", ".ts", ".yml", ".yaml", ".bat", ".ps1", ".manifest", ".appxmanifest" };
                    if (!allowedExts.Contains(ext)) continue;

                    // Resolve absolute path or search for file
                    string? targetPath = null;
                    if (File.Exists(Path.Combine(workspaceDir, fileName)))
                    {
                        targetPath = Path.Combine(workspaceDir, fileName);
                    }
                    else
                    {
                        // Safe recursive search for file matching the name (excluding bin/obj/node_modules/temp/git)
                        var baseName = Path.GetFileName(fileName);
                        targetPath = FindFileSafely(workspaceDir, baseName);
                    }

                    if (targetPath != null && File.Exists(targetPath))
                    {
                        var relative = Path.GetRelativePath(workspaceDir, targetPath);

                        string content;
                        using (var reader = new StreamReader(new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
                        {
                            var buffer = new char[8000];
                            int read = reader.ReadBlock(buffer, 0, buffer.Length);
                            content = new string(buffer, 0, read);
                            if (!reader.EndOfStream)
                            {
                                content += "\n... [TRUNCATED] ...";
                            }
                        }

                        contextBuilder.AppendLine();
                        contextBuilder.AppendLine($"=== FILESYSTEM FILE CONTENT: {relative} ===");
                        contextBuilder.AppendLine(content);
                        contextBuilder.AppendLine("=====================================================");

                        // Stop after reading one file to prevent prompt overflow
                        break;
                    }
                }
            }

            if (contextBuilder.Length > 0)
            {
                return prompt + "\n" + contextBuilder.ToString();
            }
        }
        catch
        {
            // Fail silently so it never breaks chat
        }

        return prompt;
    }

    private static string? FindFileSafely(string rootPath, string fileName)
    {
        var queue = new Queue<string>();
        queue.Enqueue(rootPath);

        while (queue.Count > 0)
        {
            var dir = queue.Dequeue();
            try
            {
                var filePath = Path.Combine(dir, fileName);
                if (File.Exists(filePath)) return filePath;

                foreach (var subDir in Directory.GetDirectories(dir))
                {
                    var name = Path.GetFileName(subDir).ToLowerInvariant();
                    if (name == "bin" || name == "obj" || name == "node_modules" || name == ".git" || name == "temp")
                        continue;
                    queue.Enqueue(subDir);
                }
            }
            catch { /* Ignore access denied */ }
        }
        return null;
    }
}
