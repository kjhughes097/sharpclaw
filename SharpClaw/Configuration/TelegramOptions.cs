namespace SharpClaw.Configuration;

public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    public string BotToken { get; set; } = string.Empty;
    public List<string> AllowedUsers { get; set; } = [];
    public string DefaultAgent { get; set; } = string.Empty;
}
