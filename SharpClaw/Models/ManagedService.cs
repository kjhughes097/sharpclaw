using System.Diagnostics;

namespace SharpClaw.Models;

public sealed class ManagedService(ServiceDefinition definition, Process? process)
{
    public ServiceDefinition Definition { get; } = definition;
    public Process? Process { get; } = process;
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public ServiceStatus Status { get; set; } = ServiceStatus.Starting;

    /// <summary>
    /// True for docker-compose runtime services that have no spawned process.
    /// </summary>
    public bool IsComposeOnly => Process is null;
}

public enum ServiceStatus
{
    Starting,
    Healthy,
    Unhealthy,
    Stopped,
    Failed
}
