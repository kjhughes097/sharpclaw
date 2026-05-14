namespace SharpClaw.Configuration;

public sealed class OpenTelemetryOptions
{
    public const string SectionName = "OpenTelemetry";

    public string Endpoint { get; set; } = "http://localhost:4317";
}
