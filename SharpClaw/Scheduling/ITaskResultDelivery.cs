using SharpClaw.Models;

namespace SharpClaw.Scheduling;

public interface ITaskResultDelivery
{
    ScheduleChannelType ChannelType { get; }
    Task DeliverAsync(ScheduledTask task, string result, CancellationToken ct = default);
}
