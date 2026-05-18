namespace SharpClaw.Configuration;

public sealed class AnthropicAdminMcpOptions
{
    public const string SectionName = "AnthropicAdminMcp";

    public string ApiBaseUrl { get; set; } = "https://api.anthropic.com";
    public string ApiKey { get; set; } = string.Empty;
    public decimal MonthlyBudgetUsd { get; set; }
    public int DefaultLookbackDays { get; set; } = 7;
    public bool EnableDashboardFallback { get; set; }
}
