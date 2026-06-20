namespace SharpClaw.Configuration;

public sealed class BackupOptions
{
    public const string SectionName = "Backup";

    public bool Enabled { get; set; } = false;

    public string RootPath { get; set; } = "/srv/backups/sharpclaw";

    public string CronExpression { get; set; } = "0 3 * * *";

    public int RetentionDays { get; set; } = 7;

    public long? NotifyTelegramChatId { get; set; }

    public IList<string> ExcludeRelativePaths { get; set; } = new List<string> { "coding" };

    public IList<string> SqliteRelativePaths { get; set; } = new List<string>
    {
        "token-usage.db",
        "data/semantic-memory.db",
    };
}
