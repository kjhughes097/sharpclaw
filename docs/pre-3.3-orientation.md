# Pre-3.3 Foundations — Orientation (task T0)

Maps [the pre-3.3 spec](sharpclaw-3.2-post-implementation-work.md) onto the code as it stood at
the start of the work (`main` after Phase 3.2, plus the CLI install docs). Open questions were
resolved by the human's instruction to implement the whole spec without pausing; the decisions
taken are listed in section 4 so they can be reviewed.

## 1. Real names the spec maps to

| Spec term | In the repo |
|---|---|
| Client API auth (admin key) | `Auth/AdminAuthFilter.cs` (endpoint filter on the `/api` group, `GatewayEndpoints.Map`) |
| Agent auth | `Auth/AgentAuthFilter.cs`, `AuthContext.Agent(http)` → `AuthenticatedAgent` |
| Listener separation | `Infrastructure/ListenerFilter.cs` (`RequireListener`) |
| Endpoint registration | `Endpoints/EndpointMapping.cs` (partial `GatewayEndpoints`); one static class per area |
| Service registration | `GatewayServices` partials: `Auth/AuthRegistration.cs`, `Services/ServicesRegistration.cs`, `Telemetry/TelemetryRegistration.cs`, `Providers/ProviderRegistration.cs` |
| Options | `Configuration/Options.cs`, bound in `GatewayBuilder.Build` |
| In-memory event bus | `Services/ThreadEventBus.cs` (`IThreadEventBus`, per-thread channels, `SubscriberCount`) |
| SSE stream | `Endpoints/ThreadStreamResult.cs` (`GET /api/threads/{id}/stream`) |
| Agent reply / fail / history | `Endpoints/AgentEndpoints.cs` (`Reply`, `Fail`, `ThreadHistory`) |
| Message listing | `Services/MessageQueries.ListAsync` (orders by `created_at, id`; `after` anchor) |
| Claim / requeue | `Services/InboxRepository` (`ClaimNextAsync`, `RequeueStaleAsync` raw SQL, `TouchClaimAsync`) |
| Approvals | `Endpoints/ApprovalEndpoints.cs` (`Create`, `Poll`, `Decide`, lazy `ExpireIfDueAsync`), `ApprovalSignal` |
| Chat completions | `Endpoints/ChatEndpoints.cs` (`ChatStream`), `LlmCallRecorder`, `ChatRequestParser` |
| Metrics | `Telemetry/GatewayMetrics.cs` |
| Agent history fetch | `IGatewayClient.GetThreadHistoryAsync` → `AgentLoop.ProcessOnceAsync` |
| Agent tool loop | `AgentLoop.RunTurnAsync`; `Tools/ToolDispatcher.DispatchAsync` returns the tool message JSON |
| CLI chat | `Cli/ChatRunner.RunOnceAsync`, `CliGateway.HttpThreadStream` (SSE parser), `ICliGateway` |
| CLI commands | `Cli/Commands.cs` (hand-rolled `ParsedArgs`) |
| Shared contracts | `Contracts/Dtos.cs`, `Constants.cs` (`ErrorCodes`, …), `SseEvents.cs`, `ChatCompletion.cs` |

## 2. Where the repo already deviates from the 3.1/3.2 specs

- `ChatToolCall` in Contracts is flat (`id`, `name`, `arguments`); the OpenAI nested
  `function: { name, arguments }` shape is produced only when the agent serialises its request
  (`GatewayHttpClient.ToWire`).
- `LlmCallRecorder` already truncates `tool_calls.arguments` to 1,000 characters.
- `Chat:MaxLlmCallsPerMessage` (loop backstop) and the claim heartbeat (`TouchClaimAsync`) exist.
- `fail` publishes the reason code as both `code` and `message` (tech-debt #6).
- Approval expiry is lazy (tech-debt #11).

## 3. Conflicts between this spec and the code (and how they were resolved)

| # | Conflict | Resolution |
|---|---|---|
| 1 | §9 (task T2) needs `messages.unattended`, but the column is listed with the §5 message changes (task T3). | T2 adds the `unattended` column in its own migration; T3 adds the rest. |
| 2 | §5.2 groups turns as "a user row up to the next user row" by `seq`. A user can post a second message while the first turn is running, so its `seq` lands *before* the first turn's exchanges and reply. | Turns are grouped by **correlation id** (a user message's id; every exchange and reply carries it) and ordered by the user message's `seq`. In the normal case this is identical to the spec. Turns after the current one (queued, not yet claimed) are not sent to the model. |
| 3 | §6.4: the context endpoint takes no correlation id, but needs "the current turn". | The current turn is the agent's claimed user message in that thread (latest if several); if none is claimed, the latest user message. |
| 4 | §9.4 needs to know which messages a claim timeout requeued; `RequeueStaleAsync` is a bulk `UPDATE`. | It now uses `RETURNING id` and expires pending approvals for those ids. |
| 5 | §6.7 asks for the `CharsPerToken` heuristic on the agent side; agents can't read gateway config. | A shared constant `ContextDefaults.CharsPerToken` (4) in Contracts; the gateway's `Context:CharsPerToken` defaults to it. Similarly the agent uses the default truncation limits from Contracts and the gateway re-applies its configured ones. |
| 6 | §9.2's approval shape is much larger than the agent-facing `ApprovalDto`. | New `ApprovalDetailDto` for the client queue API; the agent contract is unchanged. |
| 7 | §5.4: model-facing `tool_calls` need the OpenAI nested shape; stored rows use the flat shape. | A `ContextMessage` / `ContextToolCall` contract with explicit snake_case wire names, used by the context endpoint and by the agent. |
| 8 | §6.6: OpenRouter non-2xx errors are not inspected (`"upstream non-2xx"`). | The provider reads at most 4 KB of the error body, only to detect a context-length error (never logged or stored), and raises `ProviderErrorKind.ContextLength`. |
| 9 | §8.6 / §16: the CLI needs `id:` values from the stream; `CliSseEvent` has none. | `CliSseEvent` gains an optional `Id`. |

No conflict required changing the spec's behaviour; the items above fill gaps.

## 4. Inputs from section 17

The human asked for the whole spec to be implemented without pausing, so the section 2 defaults
are used as written: gateway-native login with a session cookie, same-origin UI, TLS at a reverse
proxy (forwarded headers off by default), a 32,000-token default context window, a 300 s approval
timeout and 14-day event retention. No user is created automatically; the first user is created
with `sharpclaw users create <name>`.
