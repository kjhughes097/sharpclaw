namespace SharpClaw.Telegram;

/// <summary>
/// Configuration options for the Telegram integration.
/// </summary>
public sealed class TelegramOptions
{
    /// <summary>Telegram Bot API token.</summary>
    public string BotToken { get; init; } = string.Empty;

    /// <summary>Base URL for the SharpClaw API (e.g. http://127.0.0.1:5100).</summary>
    public string ApiBaseUrl { get; init; } = "http://127.0.0.1:5100";

    /// <summary>Optional API key for authenticating with the SharpClaw API.</summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>
    /// Comma-separated list of allowed Telegram usernames (without the @ prefix).
    /// If empty, all users are allowed.
    /// </summary>
    public string AllowedUsers { get; init; } = string.Empty;

    /// <summary>Default SharpClaw project slug for Telegram conversations.</summary>
    public string DefaultProject { get; init; } = "telegram";
}
