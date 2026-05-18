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

## MCP Trace Logging

SharpClaw emits `Information`-level logs that trace MCP wiring for each agent session. This is useful when an agent reports that an MCP server is unavailable.

Key log points:

- `RegistryWorker`: MCP registrations at startup (`Registered MCP server {Name}`)
- `AgentInvoker`: selected agent and first-session creation/reuse
- `AgentRunner`: requested vs resolved tool and MCP names
- `CopilotProvider`: MCP transport details passed into `CreateSessionAsync` / `ResumeSessionAsync`

Typical diagnosis flow:

1. Verify the MCP is registered at startup.
2. Verify the selected agent requested that MCP in its frontmatter.
3. Verify `AgentRunner` resolved the MCP (not missing from registry).
4. Verify `CopilotProvider` included the MCP in session setup.

## Workspace Transcripts

SharpClaw now writes timestamped request/response transcripts for every handled turn to workspace files.

- Path format: `{WorkspacePath}/{agent}/sessions/{sessionId}.transcript.jsonl`
- One JSON object per line (JSONL)
- Includes full request/response content for debugging token spikes and throttling

Each transcript line includes fields such as:

- `timestampUtc`
- `agentId`
- `sessionId`
- `turnType` (`request` or `response`)
- `content` (full prompt or response text)
- metadata when available: `source`, `llmProvider`, `model`, `toolCount`, `mcpCount`, `success`, `error`, `durationMs`, `isCommand`

Use this for deep diagnostics; `audit.md` remains available as a lightweight summary log.

You can also use the built-in helper script to inspect an agent's transcripts:

```bash
./sharpclaw.sh transcript fin
```

Optional explicit workspace path:

```bash
./sharpclaw.sh transcript fin /path/to/workspace
```

Single-session deep dive:

```bash
./sharpclaw.sh transcript fin --session <session-id>
```

Single-session deep dive with explicit workspace path:

```bash
./sharpclaw.sh transcript fin /path/to/workspace --session <session-id>
```

Browser MCP struggle diagnostics (heuristics):

```bash
./sharpclaw.sh transcript fin --browser
```

This adds:

- summary counts for browser-tool mentions, leaked `browser_*` invoke tags, and browser-related error text
- a latest-turn list of suspicious browser-related responses (errors or very slow responses)

## Transcript Query Examples

These examples assume:

- `WS` points to your workspace path (`SharpClaw:WorkspacePath` in appsettings)
- `AGENT` is the agent folder name (for example `fin`)

```bash
WS=/path/to/workspace
AGENT=fin
```

### 1) Largest turns (by content length)

```bash
find "$WS/$AGENT/sessions" -name '*.transcript.jsonl' -print0 \
  | xargs -0 cat \
  | jq -r '[.timestampUtc, .sessionId, .turnType, (.content|length)] | @tsv' \
  | sort -k4,4nr \
  | head -n 25
```

### 2) Session summary (request/response counts + max payload size)

```bash
find "$WS/$AGENT/sessions" -name '*.transcript.jsonl' -print0 \
  | xargs -0 cat \
  | jq -s '
      group_by(.sessionId)
      | map({
          sessionId: .[0].sessionId,
          requests: (map(select(.turnType == "request")) | length),
          responses: (map(select(.turnType == "response")) | length),
          maxContentLength: (map(.content | length) | max),
          avgDurationMs: ([.[] | select(.durationMs != null) | .durationMs] | if length == 0 then 0 else (add / length) end)
        })
      | sort_by(.maxContentLength)
      | reverse
    '
```

### 3) Find likely throttle-heavy requests

Heuristic: large request content and/or long model duration.

```bash
find "$WS/$AGENT/sessions" -name '*.transcript.jsonl' -print0 \
  | xargs -0 cat \
  | jq -r '
      select(
        .turnType == "request" and
        ((.content | length) > 8000)
      )
      | [.timestampUtc, .sessionId, (.content | length)]
      | @tsv
    ' | sort
```

```bash
find "$WS/$AGENT/sessions" -name '*.transcript.jsonl' -print0 \
  | xargs -0 cat \
  | jq -r '
      select(
        .turnType == "response" and
        .durationMs != null and
        .durationMs > 15000
      )
      | [.timestampUtc, .sessionId, .durationMs]
      | @tsv
    ' | sort -k3,3nr
```

### 4) Inspect one session as a clean timeline

```bash
SESSION_ID=<session-id>
jq -r '
  select(.sessionId == env.SESSION_ID)
  | "\(.timestampUtc) [\(.turnType)] len=\(.content|length) dur=\(.durationMs // 0)" \
    + "\n" + (.content | gsub("\\n"; " ") | .[0:220]) + "\n"
' "$WS/$AGENT/sessions/$SESSION_ID.transcript.jsonl"
```

If `jq` is not installed, use `sudo apt-get install jq` (Debian/Ubuntu) or equivalent for your distro.
