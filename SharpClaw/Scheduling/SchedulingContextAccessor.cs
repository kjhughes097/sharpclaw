using SharpClaw.Models;

namespace SharpClaw.Scheduling;

/// <summary>
/// Provides ambient channel context (chat ID + type) to tools during agent execution.
/// </summary>
public sealed class SchedulingContextAccessor
{
    private static readonly AsyncLocal<SchedulingContext?> _current = new();

    public SchedulingContext? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}

public sealed record SchedulingContext(string ChannelKey, ScheduleChannelType ChannelType, string AgentId);
