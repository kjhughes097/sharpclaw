using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

// Throwaway telemetry smoke test (spec task T1).
//
// Emits one counter (sharpclaw.smoke.counter) and one log line carrying a correlation_id
// attribute, using the SAME OTEL_* environment configuration as the gateway. Use it to
// discover: (1) the exact metric name VictoriaMetrics lands it under, (2) whether Loki
// accepts correlation_id as structured metadata, (3) that both show up in Grafana Explore.
//
// Required env (same as the gateway):
//   OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
//   OTEL_EXPORTER_OTLP_METRICS_ENDPOINT=http://<host>:8428/opentelemetry/v1/metrics
//   OTEL_EXPORTER_OTLP_LOGS_ENDPOINT=http://<host>:3100/otlp/v1/logs
//   OTEL_EXPORTER_OTLP_METRICS_TEMPORALITY_PREFERENCE=cumulative
//   OTEL_SERVICE_NAME=sharpclaw-smoke
//   OTEL_RESOURCE_ATTRIBUTES=deployment.environment=home

const string meterName = "SharpClaw.Smoke";

var metricsEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_METRICS_ENDPOINT");
var logsEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_LOGS_ENDPOINT");

if (string.IsNullOrWhiteSpace(metricsEndpoint) && string.IsNullOrWhiteSpace(logsEndpoint))
{
    Console.Error.WriteLine("Set OTEL_EXPORTER_OTLP_METRICS_ENDPOINT and/or OTEL_EXPORTER_OTLP_LOGS_ENDPOINT first.");
    return 1;
}

var resource = ResourceBuilder.CreateDefault().AddService("sharpclaw-smoke");

using var meterProvider = Sdk.CreateMeterProviderBuilder()
    .SetResourceBuilder(resource)
    .AddMeter(meterName)
    .AddOtlpExporter()
    .Build();

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddOpenTelemetry(o =>
    {
        o.IncludeScopes = true;
        o.IncludeFormattedMessage = true;
        o.SetResourceBuilder(resource);
        o.AddOtlpExporter();
    });
});

using var meter = new Meter(meterName);
var counter = meter.CreateCounter<long>("sharpclaw.smoke.counter", "{tick}");
counter.Add(1, new KeyValuePair<string, object?>("source", "smoke-test"));

var logger = loggerFactory.CreateLogger("SharpClaw.Smoke");
var correlationId = Guid.NewGuid().ToString();
using (logger.BeginScope(new Dictionary<string, object> { ["correlation_id"] = correlationId }))
{
    logger.LogInformation("smoke test log, correlation_id={CorrelationId}", correlationId);
}

Console.WriteLine($"Emitted sharpclaw.smoke.counter and a log with correlation_id={correlationId}.");
Console.WriteLine("Flushing exporters...");

// Give the exporters time to flush before exit.
meterProvider.ForceFlush(5000);
await Task.Delay(2000);

Console.WriteLine("Done. Check VictoriaMetrics for the metric and Loki/Grafana for the log.");
return 0;
