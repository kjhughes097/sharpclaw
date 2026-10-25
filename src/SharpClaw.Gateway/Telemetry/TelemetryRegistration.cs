using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using SharpClaw.Gateway.Telemetry;

namespace SharpClaw.Gateway;

public static partial class GatewayServices
{
    static partial void RegisterTelemetry(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<GatewayMetrics>();

        var metricsEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_METRICS_ENDPOINT");
        var logsEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_LOGS_ENDPOINT");

        // Structured JSON logs to stdout are always on, independent of any exporter.
        builder.Logging.AddJsonConsole(o =>
        {
            o.IncludeScopes = true;
        });

        var otel = builder.Services.AddOpenTelemetry();

        otel.ConfigureResource(r => r.AddService(serviceName: "sharpclaw-gateway"));

        otel.WithMetrics(m =>
        {
            m.AddMeter(GatewayMetrics.MeterName);
            if (!string.IsNullOrWhiteSpace(metricsEndpoint))
                m.AddOtlpExporter();
        });

        builder.Logging.AddOpenTelemetry(o =>
        {
            o.IncludeScopes = true;
            o.IncludeFormattedMessage = true;
            o.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("sharpclaw-gateway"));
            if (!string.IsNullOrWhiteSpace(logsEndpoint))
                o.AddOtlpExporter();
        });
    }
}
