using SharpClaw.Core;

namespace SharpClaw.Api.Models;

public interface IApiPayload;

public sealed record ApiResponse<TPayload>(int StatusCode, TPayload Payload)
    where TPayload : IApiPayload;

public sealed record ErrorResponse(string Error) : IApiPayload;

public sealed record ProblemResponse(string Title, string Detail) : IApiPayload;

public sealed record HealthResponse(string Status, string Service) : IApiPayload;

public sealed record AuthStatusResponse(bool IsConfigured) : IApiPayload;

public sealed record AuthUserResponse(string Username) : IApiPayload;

public sealed record LoginResponse(string Username, string Token) : IApiPayload;

public sealed record LogoutResponse(bool LoggedOut) : IApiPayload;

public sealed record PersonaDto(
    string Id,
    string Name,
    string Description,
    string Backend,
    string Model,
    IReadOnlyList<string> McpServers,
    IReadOnlyDictionary<string, string> PermissionPolicy,
    string SystemPrompt,
    bool IsEnabled) : IApiPayload;

public sealed record AgentDto(
    string Id,
    string Name,
    string Description,
    string Backend,
    string Model,
    IReadOnlyList<string> McpServers,
    IReadOnlyDictionary<string, string> PermissionPolicy,
    string SystemPrompt,
    bool IsEnabled,
    int SessionCount,
    long? DailyTokenLimit) : IApiPayload;

public sealed record McpDto(
    string Slug,
    string Name,
    string Description,
    string Command,
    IReadOnlyList<string> Args,
    bool IsEnabled,
    int LinkedAgentCount,
    string? Url) : IApiPayload;

public sealed record BackendModelDto(string Id, string DisplayName);

public sealed record BackendModelsResponse(
    IReadOnlyList<BackendModelDto> Models,
    string Source,
    DateTimeOffset? CachedAt,
    string? Warning) : IApiPayload;

public sealed record BackendSettingsDto(
    string Backend,
    bool IsEnabled,
    bool HasApiKey,
    string? MaskedApiKey,
    bool RequiresApiKey,
    DateTimeOffset? UpdatedAt,
    long DailyTokenLimit) : IApiPayload;

public sealed record MessageDto(string Role, string Content);

public sealed record StoredEventLogItemDto(AgentEvent Event, ToolResultEvent? Result);

public sealed record SessionDto(
    string SessionId,
    string Persona,
    string AgentId,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    IReadOnlyList<MessageDto> Messages,
    IReadOnlyList<IReadOnlyList<StoredEventLogItemDto>> EventLogs) : IApiPayload;

public sealed record EnabledStateResponse(string Id, bool IsEnabled) : IApiPayload;

public sealed record McpEnabledStateResponse(string Slug, bool IsEnabled) : IApiPayload;

public sealed record AgentDeletedResponse(string Id, int DeletedSessions) : IApiPayload;

public sealed record McpDeletedResponse(string Slug, int DetachedAgents) : IApiPayload;

public sealed record SessionDeletedResponse(string SessionId, bool Deleted) : IApiPayload;

public sealed record SessionCreatedResponse(string SessionId, string Persona, string AgentId) : IApiPayload;

public sealed record MessageQueuedResponse(string SessionId, string MessageId) : IApiPayload;

public sealed record PermissionResolvedResponse(string RequestId, bool Allowed) : IApiPayload;

public sealed record AgentDeleteConflictResponse(string Error, int LinkedSessionCount, bool RequiresSessionPurge) : IApiPayload;

public sealed record McpDeleteConflictResponse(string Error, int LinkedAgentCount, bool RequiresAgentDetach) : IApiPayload;

public sealed record TelegramSettingsDto(
    bool IsEnabled,
    bool HasBotToken,
    string? MaskedBotToken,
    IReadOnlyList<long> AllowedUserIds,
    IReadOnlyList<string> AllowedUsernames,
    string MappingStorePath) : IApiPayload;

public sealed record TelegramRuntimeSettingsDto(
    bool IsEnabled,
    string? BotToken,
    IReadOnlyList<long> AllowedUserIds,
    IReadOnlyList<string> AllowedUsernames,
    string MappingStorePath) : IApiPayload;

public sealed record TelegramWorkerTokenDto(
    string Token,
    DateTimeOffset ExpiresAt) : IApiPayload;

public sealed record AppSettingsDto(string WorkspacePath) : IApiPayload;

public sealed record HeartbeatSettingsDto(
    bool Enabled,
    int IntervalSeconds,
    int StuckThresholdSeconds,
    bool AutoCleanupEnabled,
    int AutoCleanupThresholdSeconds) : IApiPayload;

public sealed record CreateSessionRequest(string? AgentId);

public sealed record SendMessageRequest(string? Message);

public sealed record PermissionDecision(bool Allow);

public sealed record AgentEnabledRequest(bool IsEnabled);

public sealed record McpEnabledRequest(bool IsEnabled);

public sealed record UpdateTelegramSettingsRequest(
    bool IsEnabled,
    string? BotToken,
    bool? ClearBotToken,
    IReadOnlyList<long>? AllowedUserIds,
    IReadOnlyList<string>? AllowedUsernames,
    string? MappingStorePath,
    bool? ClearMappingStorePath);

public sealed record UpdateBackendSettingsRequest(
    bool IsEnabled,
    string? ApiKey,
    bool? ClearApiKey,
    long? DailyTokenLimit);

public sealed record UpdateAppSettingsRequest(
    string? WorkspacePath,
    bool? ClearWorkspacePath);

public sealed record UpdateHeartbeatSettingsRequest(
    bool? Enabled,
    int? IntervalSeconds,
    int? StuckThresholdSeconds,
    bool? AutoCleanupEnabled,
    int? AutoCleanupThresholdSeconds);

public sealed record SetupAuthRequest(
    string? Username,
    string? Password,
    string? ConfirmPassword);

public sealed record LoginRequest(
    string? Username,
    string? Password);

public sealed record AgentDefinitionRequest(
    string? Name,
    string? Description,
    string? Backend,
    string? Model,
    List<string>? McpServers,
    Dictionary<string, string>? PermissionPolicy,
    string? SystemPrompt,
    bool? IsEnabled,
    long? DailyTokenLimit);

public sealed record McpDefinitionRequest(
    string? Slug,
    string? Name,
    string? Description,
    string? Command,
    List<string>? Args,
    bool? IsEnabled,
    string? Url);

public sealed record ProviderDailyUsageDto(
    string Provider,
    long TotalTokens,
    long DailyLimit,
    double UsagePercent) : IApiPayload;

public sealed record AgentDailyUsageDto(
    string AgentSlug,
    long TotalTokens,
    long? DailyLimit,
    double? UsagePercent) : IApiPayload;

public sealed record TokenUsageSummaryDto(
    IReadOnlyList<ProviderDailyUsageDto> Providers,
    IReadOnlyList<AgentDailyUsageDto> Agents) : IApiPayload;

public sealed record TokenUsageDataPointDto(
    string Bucket,
    string AgentSlug,
    long TotalTokens);

public sealed record TokenUsageHistoryDto(
    string Period,
    IReadOnlyList<TokenUsageDataPointDto> DataPoints) : IApiPayload;

public sealed record WorkspaceEntryDto(
    string Name,
    string Type,
    long? Size,
    DateTimeOffset? LastModified);

public sealed record WorkspaceBrowseResponse(
    string Path,
    IReadOnlyList<WorkspaceEntryDto> Entries) : IApiPayload;