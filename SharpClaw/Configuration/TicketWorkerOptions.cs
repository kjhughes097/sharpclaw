namespace SharpClaw.Configuration;

public sealed class TicketWorkerOptions
{
    public const string SectionName = "TicketWorker";

    public bool Enabled { get; set; } = true;

    public int PollingIntervalSeconds { get; set; } = 60;
}
