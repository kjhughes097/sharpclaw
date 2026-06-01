namespace SharpClaw.Models;

public enum ScheduleChannelType { Telegram, Web }

public enum ScheduledTaskType { Agent, Command }

public sealed record ScheduledTask
{
    public required string Id { get; init; }
    public required string AgentId { get; init; }
    public required string Prompt { get; init; }
    public required string CronExpression { get; init; }
    public string Description { get; init; } = string.Empty;
    public bool IsOneOff { get; init; }
    public required string ChannelKey { get; init; }
    public ScheduleChannelType ChannelType { get; init; }
    public ScheduledTaskType TaskType { get; init; } = ScheduledTaskType.Agent;
    public string? Command { get; init; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset NextRunUtc { get; set; }
    public DateTimeOffset? LastRunUtc { get; set; }
    public bool Enabled { get; set; } = true;
}
