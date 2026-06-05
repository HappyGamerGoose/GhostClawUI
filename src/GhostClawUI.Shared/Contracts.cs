using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace GhostClawUI.Shared;

public static class GhostClawConstants
{
    public const string PipeName = "GhostClawUI.Agent";
    public const string ServiceName = "GhostClawUI.AgentService";
    public const string CredentialResourcePrefix = "GhostClawUI.Provider";
}

public static class PipeJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };
}

public sealed record PipeEnvelope(
    string Type,
    string Command,
    string CorrelationId,
    JsonNode? Payload = null,
    string? Error = null,
    DateTimeOffset? CreatedAt = null)
{
    public static PipeEnvelope Request<T>(string command, T payload) =>
        new("request", command, Guid.NewGuid().ToString("N"), JsonSerializer.SerializeToNode(payload, PipeJson.Options), null, DateTimeOffset.UtcNow);

    public static PipeEnvelope Response<T>(PipeEnvelope request, T payload) =>
        new("response", request.Command, request.CorrelationId, JsonSerializer.SerializeToNode(payload, PipeJson.Options), null, DateTimeOffset.UtcNow);

    public static PipeEnvelope ErrorResponse(PipeEnvelope request, string message) =>
        new("error", request.Command, request.CorrelationId, null, message, DateTimeOffset.UtcNow);

    public T? ReadPayload<T>() => Payload is null ? default : Payload.Deserialize<T>(PipeJson.Options);
}

public sealed record ProviderProfile(
    string Id,
    string Name,
    string BaseUrl,
    IReadOnlyList<string> Models,
    string? DefaultModel,
    bool IsEnabled,
    DateTimeOffset UpdatedAt);

public sealed record ProviderUpsertRequest(
    string? Id,
    string Name,
    string BaseUrl,
    IReadOnlyList<string> Models,
    string? DefaultModel,
    bool IsEnabled);

public sealed record ProviderValidationRequest(
    string Name,
    string BaseUrl,
    string? ApiKey,
    IReadOnlyList<string>? ManualModels);

public sealed record ProviderValidationResult(
    bool Success,
    IReadOnlyList<string> Models,
    string Message,
    bool UsedManualFallback);

public sealed record ProviderModelTestRequest(
    string Name,
    string BaseUrl,
    string? ApiKey,
    string Model);

public sealed record ConversationSummary(
    string Id,
    string Title,
    bool IsPinned,
    DateTimeOffset UpdatedAt);

public sealed record ConversationDetail(
    ConversationSummary Summary,
    IReadOnlyList<ChatMessage> Messages);

public sealed record ChatMessage(
    string Id,
    string ConversationId,
    string Role,
    string Content,
    string? ProviderId,
    string? Model,
    string Kind,
    DateTimeOffset CreatedAt,
    JsonNode? Metadata = null);

public sealed record ChatAttachment(
    string Name,
    string Path,
    string ContentType,
    long SizeBytes,
    string? TextPreview,
    string? DataUri = null);

public sealed record ChatSendRequest(
    string ConversationId,
    string ProviderId,
    string Model,
    string Content,
    bool WhisperMode,
    string Verbosity,
    IReadOnlyList<ChatAttachment>? Attachments,
    bool AgentMode = false);

public sealed record ChatSendResult(
    ChatMessage AssistantMessage,
    IReadOnlyList<AgentTraceCard> Trace,
    IReadOnlyList<MemoryFact> RetrievedFacts,
    bool Queued,
    string? Error);

public sealed record ActiveTracesResponse(
    bool IsRunning,
    IReadOnlyList<AgentTraceCard> Traces);

public sealed record AgentTraceCard(
    string Title,
    string Detail,
    string State);

public sealed record McpServerDefinition(
    string Id,
    string Name,
    string Description,
    string Command,
    IReadOnlyList<string> Args,
    string RegistryUrl,
    bool Installed,
    string? Version,
    DateTimeOffset UpdatedAt,
    string? IconUrl = null);

public sealed record McpServerRequest(
    string Id,
    string? Name,
    string? Command,
    IReadOnlyList<string>? Args,
    string? RegistryUrl);

public sealed record MemoryFact(
    string Id,
    string Summary,
    string Content,
    string Source,
    DateTimeOffset UpdatedAt);

public sealed record MemoryUpdateRequest(
    string? Id,
    string Summary,
    string Content,
    string Source);

public sealed record AppearanceSettings(
    string Theme,
    string AccentColor,
    string FontFamily,
    double FontSize,
    double LineHeight,
    string Density,
    string MessageAlignment,
    bool UseMica);

public sealed record AppSettings(
    AppearanceSettings Appearance,
    string Verbosity,
    IReadOnlyList<string> RegistryUrls,
    bool FallbackProvidersEnabled,
    bool SilentToolConfirmations,
    bool AutoUpdateEnabled,
    string? DefaultProviderId = null,
    string? DefaultModelId = null,
    string? VisionTranslatorProviderId = null,
    string? VisionTranslatorModel = null);

public sealed record ServiceStatus(
    bool ServiceReady,
    bool GhostClawRunning,
    int? GhostClawProcessId,
    string State,
    string Detail,
    int RestartCount,
    DateTimeOffset UpdatedAt);

public sealed record ServiceHealthReport(
    bool PipeReady,
    bool StoreReadable,
    bool StoreWritable,
    bool PayloadPresent,
    bool RuntimeExtracted,
    bool NodePresent,
    bool GhostClawEntryPresent,
    ServiceStatus Status,
    IReadOnlyList<string> Issues,
    DateTimeOffset CheckedAt);

public sealed record ExportRequest(string Id, string Format);

public sealed record ExportResult(string FileName, string Content);

public sealed record Preset(
    IReadOnlyList<ProviderProfile> Providers,
    IReadOnlyList<McpServerDefinition> Tools,
    AppSettings Settings,
    DateTimeOffset ExportedAt);

public sealed record SimpleIdRequest(string Id);

public sealed record SimpleTextRequest(string Text);

public sealed record MessageUpdateRequest(string Id, string Content);

public sealed record RenameConversationRequest(string Id, string Title);

public sealed record DeleteMessagesAfterRequest(string ConversationId, string MessageId);

public sealed record CommandResult(bool Success, string Message);

public sealed record ScheduledTask(
    string Id,
    string GroupFolder,
    string ChatJid,
    string Prompt,
    string? PreCheck,
    string ScheduleType,
    string ScheduleValue,
    string ContextMode,
    DateTimeOffset? NextRun,
    DateTimeOffset? LastRun,
    string? LastResult,
    string Status,
    DateTimeOffset CreatedAt
);

public sealed record TaskRunLog(
    int Id,
    string TaskId,
    string RunAt,
    long DurationMs,
    string Status,
    string? Result,
    string? Error
);

public sealed record RalphChecklistItem(
    string Title,
    bool Completed,
    int Index
);

public sealed record RalphStatusResponse(
    string RunId,
    string Status,
    string StartedAt,
    int CurrentIteration,
    int MaxIterations,
    string ProgressLog,
    IReadOnlyList<RalphChecklistItem> Checklist
);

public sealed record RalphStartRequest(
    string TaskFilePath,
    string WorkDir,
    string TargetJid,
    int MaxIterations,
    bool NotifyProgress
);

public sealed record McpSearchRequest(string? Query, int Page, int PageSize);

public sealed record McpSearchResponse(
    IReadOnlyList<McpServerDefinition> Servers,
    int CurrentPage,
    int TotalPages,
    int TotalCount);


public sealed record SkillSummary(
    string Id,
    string Name,
    string Description,
    string FilePath);

public sealed record SkillUpsertRequest(
    string Name,
    string Description,
    string Content);

public sealed record TelegramSettings(
    string BotToken,
    string ChatId,
    bool IsEnabled);

public sealed record WhatsAppSettings(
    string AccessToken,
    string PhoneNumberId,
    string VerifyToken,
    string WebhookPort,
    bool IsEnabled);

