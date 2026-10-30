# Telemetry notes (task T1)

Findings from running `scripts/telemetry-smoke-test` and the gateway against the live backends on
2026-10-25.

## Backends

| Backend | Endpoint | Version |
|---|---|---|
| VictoriaMetrics | `http://192.168.1.10:8428/opentelemetry/v1/metrics` | 2.24.0 |
| Loki | `http://192.168.1.10:3100/otlp/v1/logs` | 3.7.8 |
| Grafana | `http://192.168.1.10:3000` | 13.2.2 (API requires auth) |

No auth headers or tenant id are needed for the OTLP endpoints.

## Exporter configuration gotcha

With OpenTelemetry .NET 1.20.1 the signal-specific `OTEL_EXPORTER_OTLP_METRICS_ENDPOINT` /
`OTEL_EXPORTER_OTLP_LOGS_ENDPOINT` variables were **not** applied by `AddOtlpExporter()`; the
exporter silently fell back to `http://localhost:4318/v1/metrics` (found via OTel
self-diagnostics, `OTEL_DIAGNOSTICS.json`). Both the gateway and the smoke test now read these
variables and set `Endpoint` (used as the full URL) and `Protocol = HttpProtobuf` explicitly, with
cumulative temporality for metrics.

## VictoriaMetrics: landed metric names

OTel names are stored **verbatim**: dots are kept, counters get **no** `_total` suffix, and no
unit suffix is added. Histograms only gain `_bucket`, `_sum` and `_count`.

| OTel instrument | Landed name(s) |
|---|---|
| `sharpclaw.smoke.counter` | `sharpclaw.smoke.counter` |
| `sharpclaw.llm.calls` | `sharpclaw.llm.calls` |
| `sharpclaw.llm.tokens` | `sharpclaw.llm.tokens` |
| `sharpclaw.llm.cost_usd` | `sharpclaw.llm.cost_usd` |
| `sharpclaw.llm.duration` | `sharpclaw.llm.duration_bucket` / `_sum` / `_count` |
| `sharpclaw.llm.time_to_first_token` | `sharpclaw.llm.time_to_first_token_bucket` / `_sum` / `_count` |
| `sharpclaw.message.duration` | `sharpclaw.message.duration_bucket` / `_sum` / `_count` |
| `sharpclaw.budget.remaining_usd` | `sharpclaw.budget.remaining_usd` |
| `sharpclaw.budget.exceeded` | `sharpclaw.budget.exceeded` |
| `sharpclaw.agent.last_seen` | `sharpclaw.agent.last_seen` |
| `sharpclaw.inbox.depth` | `sharpclaw.inbox.depth` (only present while messages are pending) |
| `sharpclaw.auth.failures` | `sharpclaw.auth.failures` (only present after a failure) |
| `sharpclaw.tool.calls` (3.2) | `sharpclaw.tool.calls` (labels agent, tool, status) |
| `sharpclaw.approvals` (3.2) | `sharpclaw.approvals` (labels agent, status) |

### Added by the pre-3.3 foundations

Confirmed live in VictoriaMetrics on 2026-10-27 (the dashboard panels use these names):

| OTel instrument | Landed name(s) | Labels |
|---|---|---|
| `sharpclaw.auth.failures` (extended) | `sharpclaw.auth.failures` | listener, method (`admin_key`, `session`, `csrf`, `origin`, `password`, `agent_token`) |
| `sharpclaw.auth.logins` | `sharpclaw.auth.logins` | result (`success`, `failure`, `rate_limited`) |
| `sharpclaw.context.truncations` | `sharpclaw.context.truncations` | agent |
| `sharpclaw.context.rejections` | `sharpclaw.context.rejections` | agent |
| `sharpclaw.context.estimated_tokens` | `sharpclaw.context.estimated_tokens_bucket` / `_sum` / `_count` | agent |
| `sharpclaw.sse.subscribers` | `sharpclaw.sse.subscribers` | scope (`thread`, `global`) |
| `sharpclaw.approvals.pending` | `sharpclaw.approvals.pending` | agent |
| `sharpclaw.approval.decision_time` | `sharpclaw.approval.decision_time_bucket` / `_sum` / `_count` | agent, status |
| `sharpclaw.tool.unreported` | `sharpclaw.tool.unreported` | agent (tool calls older than 5 min never reported) |

### Added by the external tools spec

Expected names (dots kept, as above); not yet confirmed live.

| OTel instrument | Landed name(s) | Labels |
|---|---|---|
| `sharpclaw.broker.calls` | `sharpclaw.broker.calls` | agent, tool, status (`succeeded`, `failed`, `refused`, `error`) |
| `sharpclaw.broker.duration` | `sharpclaw.broker.duration_bucket` / `_sum` / `_count` | agent, tool |
| `sharpclaw.egress.connections` | `sharpclaw.egress.connections` | agent, type (`connect`, `http`), status (`allowed`, `bad_request`, `auth_failed`, `blocked`, `limited`, `timeout`, `upstream_error`, `closed`) |
| `sharpclaw.egress.bytes` | `sharpclaw.egress.bytes` | agent |
| `sharpclaw.auth.failures` (extended) | `sharpclaw.auth.failures` | listener `egress`, method `proxy_credential` |

Because the names contain dots, PromQL/MetricsQL must select them as
`{__name__="sharpclaw.llm.calls", agent="assistant"}`.

Resource attributes also become labels (`service.name`, `service.instance.id`,
`deployment.environment`, `scope.name`, `telemetry.sdk.*`). `service.instance.id` changes on every
gateway restart, so dashboard queries aggregate with `sum by (...)` / `max by (...)`.

Histogram buckets: the SDK default boundaries (0, 5, 10, 25, … 10000) are designed for
milliseconds; the gateway sets second-scale boundaries via `InstrumentAdvice`
(0.05 … 120 s for LLM duration/TTFT, 0.1 … 300 s for message duration). Series recorded before
this change use the old boundaries and age out naturally.

## Loki

- Logs are accepted over OTLP (structured metadata is enabled).
- Index labels are only `service_name`, `service_instance_id` and `deployment_environment`.
- Log attributes and scope values (`correlation_id`, `thread_id`, `agent`, `agent_id`) arrive as
  **structured metadata**, not labels, and are queryable:
  ```
  {service_name="sharpclaw-gateway"} | correlation_id="<user message id>"
  ```
- A single message is traceable end to end: `message queued` → `message claimed` →
  `llm call finished` (status, tokens, cost, latency, ttft) → `message completed` (or
  `message failed`).
- Verified that prompt and reply text never appear in Loki.
