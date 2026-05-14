---
sidebar_position: 12
---

# Logging & Observability

SharpClaw uses OpenTelemetry for structured observability — traces, metrics, and logs exported via OTLP.

## Console Logging

```csharp
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(opts =>
{
    opts.TimestampFormat = "yyyy-MM-dd HH:mm:ss : ";
    opts.SingleLine = true;
});
```

## OpenTelemetry Setup

```csharp
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("SharpClaw"))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter(o => o.Endpoint = new Uri(endpoint)))
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter(o => o.Endpoint = new Uri(endpoint)));

builder.Logging.AddOpenTelemetry(logging =>
{
    logging.IncludeFormattedMessage = true;
    logging.AddOtlpExporter(o => o.Endpoint = new Uri(endpoint));
});
```

## Configuration

```json
{
  "OpenTelemetry": {
    "Endpoint": "http://localhost:4318"
  }
}
```

## LGTM Stack

For local development, a docker-compose LGTM stack (Loki + Grafana + Tempo + Mimir) provides:

- **Grafana** — dashboards and exploration (port 3000)
- **Tempo** — distributed tracing
- **Loki** — log aggregation
- **Mimir** — metrics storage

## Exported Signals

| Signal  | Instrumentation                              |
| ------- | -------------------------------------------- |
| Traces  | ASP.NET Core requests, HTTP client calls     |
| Metrics | Request count, duration, HTTP client metrics |
| Logs    | All ILogger output with formatted messages   |

## Service Identity

The service identifies itself as `"SharpClaw"` in all telemetry via `ConfigureResource`.
