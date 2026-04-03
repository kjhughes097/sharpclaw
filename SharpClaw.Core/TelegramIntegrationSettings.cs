namespace SharpClaw.Core;

public sealed record TelegramIntegrationSettings(
    bool IsEnabled,
    string? BotToken,
    IReadOnlyList<long> AllowedUserIds,
    IReadOnlyList<string> AllowedUsernames,
    string? MappingStorePath);
