# Telemetry notes (task T1)

This file records the real behaviour of the telemetry backends, discovered by running
`scripts/telemetry-smoke-test` against the live VictoriaMetrics and Loki endpoints.

## Status: pending live verification

The smoke test project and the gateway telemetry wiring are implemented, but the end-to-end
verification (metric lands in VictoriaMetrics, log accepted by Loki as structured metadata, both
visible in Grafana Explore) **requires live endpoints that must be supplied by the human**
(spec section 19):

- The OTLP endpoints for VictoriaMetrics and Loki (hosts, ports, any auth headers or tenant id),
  and confirmation that the Docker host can reach them.
- The VictoriaMetrics metric-naming flags in use.
- The Loki version and whether structured metadata is enabled (needs a recent schema — v13 with
  TSDB).

Until those are provided, this environment cannot reach the backends, so the landed metric name
and Loki behaviour below are **not yet filled in**.

## How to run the smoke test

```bash
export OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
export OTEL_EXPORTER_OTLP_METRICS_ENDPOINT=http://<host>:8428/opentelemetry/v1/metrics
export OTEL_EXPORTER_OTLP_LOGS_ENDPOINT=http://<host>:3100/otlp/v1/logs
export OTEL_EXPORTER_OTLP_METRICS_TEMPORALITY_PREFERENCE=cumulative
export OTEL_SERVICE_NAME=sharpclaw-smoke
export OTEL_RESOURCE_ATTRIBUTES=deployment.environment=home

dotnet run --project scripts/telemetry-smoke-test
```

Then, in Grafana Explore:

1. VictoriaMetrics: search for the smoke counter and record the **exact** name it landed under
   (dots vs underscores, any `_total`/unit suffix). Fill this in below.
2. Loki: query `{service_name="sharpclaw-smoke"}` and confirm `correlation_id` is queryable as
   structured metadata (`| correlation_id="…"`), not rejected and not promoted to a label.

## Findings (fill in after a live run)

- Landed metric name in VictoriaMetrics: `TODO` (e.g. `sharpclaw_smoke_counter_total`).
- Loki accepted `correlation_id` as structured metadata: `TODO (yes/no)`.
- Loki version / schema: `TODO`.
- Notes / anomalies: `TODO`.

> If Loki rejects the logs, the usual cause is structured metadata not being enabled (needs a
> recent schema, v13 with TSDB). Report that to the human rather than working around it.
