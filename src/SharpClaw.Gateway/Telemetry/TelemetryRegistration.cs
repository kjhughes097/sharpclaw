using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using SharpClaw.Gateway.Telemetry;

namespace SharpClaw.Gateway;

public static class TelemetryRegistration
{
    public static WebApplicationBuilder AddSharpClawTelemetry(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<GatewayMetrics>();

        // Signal-specific endpoints are full URLs (VictoriaMetrics and Loki use different paths).
        // They are applied explicitly rather than relying on SDK env-var handling.
        var metricsEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_METRICS_ENDPOINT");
        var logsEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_LOGS_ENDPOINT");
        var serviceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") ?? "sharpclaw-gateway";

        // Structured JSON logs to stdout are always on, independent of any exporter.
        builder.Logging.AddJsonConsole(o => o.IncludeScopes = true);

        var otel = builder.Services.AddOpenTelemetry();
        otel.ConfigureResource(r => r.AddService(serviceName));

        otel.WithMetrics(m =>
        {
            m.AddMeter(GatewayMetrics.MeterName);
            if (!string.IsNullOrWhiteSpace(metricsEndpoint))
                m.AddOtlpExporter((o, reader) =>
                {
                    o.Endpoint = new Uri(metricsEndpoint);
                    o.Protocol = OtlpExportProtocol.HttpProtobuf;
                    reader.TemporalityPreference = MetricReaderTemporalityPreference.Cumulative;
                });
        });

        builder.Logging.AddOpenTelemetry(o =>
        {
            o.IncludeScopes = true;
            o.IncludeFormattedMessage = true;
            o.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(serviceName));
            if (!string.IsNullOrWhiteSpace(logsEndpoint))
                o.AddOtlpExporter(e =>
                {
                    e.Endpoint = new Uri(logsEndpoint);
                    e.Protocol = OtlpExportProtocol.HttpProtobuf;
                });
        });
        return builder;
    }
}
