# Tech Debt Register

Known and accepted tech debt. Each item says **why it was accepted** and **what would trigger
fixing it**, so it can be paid down deliberately rather than forgotten.

- Add an entry when you knowingly take a shortcut, or spot complexity creeping in that isn't worth
  fixing right now.
- Remove the entry (in the same PR) when it is paid down.
- Severity: **High** = likely to cause bugs or block upcoming work; **Medium** = slows work or
  hurts readability; **Low** = cosmetic or only matters at larger scale.

## Accepted by design (Phase 3.1 spec, section 20)

These are deliberate scope limits, not oversights. Revisit when the phase that needs them starts.

| Item | Notes |
|---|---|
| No provider retries | A transient provider failure fails the message. |
| Budget overshoot possible | Concurrent or in-flight calls aren't counted; cancelled calls may incur unrecorded upstream cost. |
| Single gateway process, single SQLite file | No high availability. |
| Streaming-only chat completions | No non-streaming `/v1/chat/completions`; every client and agent streams. |

## Accepted by design (Phase 3.2 spec)

| Item | Notes |
|---|---|
| Agent-reported tool outcomes are self-reported | The gateway independently records every tool call the model requested, so a missing report is visible (`status = requested`), but what a local tool actually did is only what the agent says. Docker isolation limits the damage. |

## Accepted by design (pre-3.3 foundations, section 15)

| Item | Notes |
|---|---|
| No summarisation | Long threads lose their oldest turns from the model's context (the thread's `context.droppedTurns` shows how many). `IContextSummariser` is the seam. |
| A failed turn stores no tool exchanges | After a failure the model doesn't know which tools it already ran in that turn; the `tool_calls` audit still records them. |
| Token estimates are a heuristic | Characters / 4. `llm_calls.estimated_prompt_tokens` sits beside the provider's count so it can be tuned. |
| Login rate-limit counters are in memory | They reset on restart. |
| Durable events duplicate reply text | `message.completed` carries the reply, bounded by `Events:RetentionDays`. |
| A pending approval blocks the agent | It processes one message at a time, so it waits up to the approval timeout. |
| No session list | Sessions are revoked only by logout, password change, an admin reset or disabling the user. |

## Accepted by design (Phase 3.3 spec, section 15)

| Item | Notes |
|---|---|
| Plain HTTP on the LAN | The password and session cookie are visible to anyone who can sniff the network. Fix: a local TLS reverse proxy, which also enables HTTP/2 and secure-context features (clipboard API, notifications, PWA). |
| HTTP/1.1 connection limit | One SSE stream per tab, so about four tabs per browser before the six-connection limit bites. A cross-tab shared stream is a possible later improvement. |
| No stop button, no thread deletion, drafts lost on reload | Out of scope for 3.3. |
| Half-open streams heal only on visibility or online/offline changes | No server keep-alive detection in the UI. |
| The context meter is the last computed value | From the gateway's last context build, not a live figure. |
| Tool-card enrichment depends on the trace | Risk, decision and duration come from `tool_calls`, joined by the provider tool-call id. |
| Every user sees every agent, thread and approval | Shared visibility suits a household system; there is no per-user ownership. Approvals record *which* user decided (`approvals.decided_by_user`). Revisit if the gateway is ever shared beyond a household. |
| Thread previews duplicate content | `threads.last_preview` holds a 120-character slice of the last message. |
| No list virtualisation | Very long threads rely on paging. |
| No frontend error reporting | UI crashes are visible only in the browser console. |

## Accepted by design (Phase 3.3.1 spec, section 14)

| Item | Notes |
|---|---|
| Usage is aggregated in memory per request | `GET /api/usage` loads the window's `llm_calls` rows and sums them in C#. Fine at household volume. Fix: SQL `GROUP BY` or rollup tables if it slows down. |
| Credits are cached in memory | After a restart the card is empty until the first fetch succeeds; no history. |
| A model override doesn't change the context window | The window is per agent; a much smaller model may fail with `context_window_exceeded`. |
| A model override can be silently ignored | When an id leaves `Models:Allowed`, the thread uses the agent's model (a warning is logged) but the UI still shows the stored override until the thread is updated. |
| Activity previews duplicate content | The same 120-character slice already shown in the thread list. |
| The Overview refetches on events | A few queries per completed turn (debounced to 1 s). Revisit with richer event payloads if it becomes chatty. |
| Chart bars are hover-only for sighted users | Keyboard and screen-reader users get the same figures from a visually hidden table per chart, not a focusable tooltip. |
| Live tool rows need the live stream | After a page load mid-turn, rows for tools requested earlier in that turn appear only once the turn finishes (a fresh stream doesn't replay `tool.requested`); the approval card still shows. Unchanged from 3.3. |

## Accepted by design (external tools spec)

| Item | Notes |
|---|---|
| Credentialed integrations live in the gateway | The GitHub token sits next to the OpenRouter keys, and the gateway makes fixed, typed calls to `api.github.com`. A separate credentials container (`toolhost`) was considered and dropped: it would only protect the token from a compromised gateway, which already holds more valuable keys. Fix: split them out into their own container when several credentialed integrations (Jira, Slack, email…) start crowding the gateway, or when one needs more than typed JSON calls. |
| Sandboxed code can send its agent's workspace to the internet | Sandbox egress is open (private and LAN addresses blocked). Mitigated by one workspace per sandbox and by keeping secrets out of workspaces; since 3.6 `sandbox_run` is medium risk and no longer always-approve by default (operators can set `ToolBroker:AlwaysApprove` back). Fix: an egress allow-list per agent if workspaces start holding sensitive data. |
| Spilled tool output is never cleaned up | Pages and run logs saved under `.sandbox/fetch/` and `.sandbox/logs/` (external tools spec 7.0) stay in the workspace until someone deletes them. Fix: prune files older than N days at sandbox start if workspaces grow noticeably. |
| The agent reads the broker policy once at startup | `GET /agent/tools` (always-approve list, fetch and clone hosts) is read when the agent starts; changing `ToolBroker:*` needs an agent restart to change its risk scoring. The gateway's own checks apply immediately either way. |
| `github_create_pr` is tested against a fake GitHub only | The fork, Git Data and PR calls follow GitHub's REST docs and are covered by a fake api.github.com; no live PR has been opened yet. Fix: a manual run on a scratch repository, recorded in [verification-log.md](verification-log.md). |
| The egress proxy runs inside the gateway | Sandbox traffic (package restores, downloads) shares the process that streams chats, and a proxy bug would sit next to the keys. Kept small: a raw connection handler, TLS tunnels are never parsed, plain HTTP is one rewritten request per connection, with per-sandbox limits. Fix: move `EgressConnectionHandler` into its own container if heavy builds make chat slow, or if the proxy needs to grow (caching, TLS inspection, other protocols). |

## Accepted by design (skills spec)

| Item | Notes |
|---|---|
| Skill contents are trusted | `skill_read` results don't taint the turn: skills are operator-curated and mounted read-only, like `soul.md`. A third-party skill could carry prompt injection; the README warns operators to review skills before adding them. Fix: mark skills as untrusted (or per-skill trust) if operators start importing skills they don't review. |
| One shared skill library for every agent | No per-agent selection; every agent with `skill_read` sees every skill. Fix: an optional `skills` list in `agent.json` if agents need different sets. |
| The skill index is built only at startup | New, removed or re-described skills need an agent restart (file contents are read live). Fix: re-scan per turn if restarts become a nuisance. |

## Accepted by design (Phase 3.6 spec)

| Item | Notes |
|---|---|
| Workspace reads don't taint by default | `workspace_read` / `workspace_search` results are still stubbed in thread history, but no longer raise the risk of later calls. Content that reached the workspace through a download, clone or sandbox run, read in a later call, is treated as trusted. Accepted because agents hold no secrets and act only inside their sandbox. Fix: provenance tracking per file, or set `taintWorkspaceReads: true` for agents that handle sensitive data. |

## Accepted by design (Phase 3.5 spec)

| Item | Notes |
|---|---|
| No `IMessageBus` interface | The plan named one, but there is a single DB-backed implementation (`AgentAsks` over the `messages` inbox) and none other planned. Fix: introduce it with the second implementation (e.g. NATS JetStream). |
| Recipients are read at agent startup | With any agent free to ask any other, an agent created later isn't offered to existing agents until they restart (the tool's recipient list comes from `GET /agent/tools` at startup). Fix: refresh the policy per turn if agents are added often. |
| A claimed ask runs on after its asker gives up | After a timeout, an ask the recipient has already claimed finishes (and is paid for) even though nobody reads the reply, apart from the linked thread. Fix: a cancel-turn signal to the agent, if wasted turns show up in spend. |
| Agent-to-agent policy is config-only | `AllowAll`, restricting lists and descriptions live in gateway config and need a restart; agents read them at startup. Fix: store them in the DB with an admin API if they change often. |
| Agents' descriptions of each other live in config | `AgentMessaging:Descriptions` rather than on the agent record. Fix: an agent `description` field if the UI needs it too. |

## Code-level debt

| # | Item | Severity | Why accepted | Fix when / how |
|---|---|---|---|---|
| 5 | **Observable gauges query the database on every collection.** `budget.remaining_usd` loads all agents and runs one spend query per agent; `approvals.pending` and `tool.unreported` run a grouped query each. | Low | Cheap at personal scale (a handful of agents, 60 s export interval). | If agent count grows, replace with a single grouped query or an in-memory cache. |
| 7 | **Hand-rolled CLI argument parser** (`ParsedArgs`) instead of `System.CommandLine`. | Low | `System.CommandLine` was only available as a pre-release; the parser is about 60 lines and tested. | Switch if the CLI grows (subcommand help, validation) or a stable release lands. |
| 9 | **Smart-mode classifier can be influenced by the call summary for workspace tools.** Their summary includes the path, which the model chose and could word to look harmless. | Low | Only *uncertain* workspace calls are classified, the taint bump still applies, and a misjudged call only touches the agent's own workspace. External tools give the classifier structured fields instead (`IAgentTool.ClassifierFields`), and always-approve tools are never classified. | If a workspace tool becomes riskier, give it `ClassifierFields` too. |
| 12 | **Every event-producing write runs under one global lock** (`EventPublisher.WriteAsync`), from `BEGIN` through commit to publish, so events commit atomically with their state change and reach streams in id order. | Low | Simple and correct; event volume is tiny, and SQLite serialises writers anyway. | If event throughput ever matters, assign ids from a sequence and let streams reorder a small window instead. |
| 13 | **Login rate-limit keys are only trimmed when touched.** Spraying many distinct usernames grows the in-memory map until restart. | Low | Single-user deployment behind a proxy; each entry is tiny. | Add a periodic sweep or a size cap when exposing the gateway more widely. |
| 14 | **`seq` is assigned as `max(seq)+1` inside an immediate SQLite transaction.** Concurrent writers to one thread serialise on the database write lock. | Low | SQLite serialises writers anyway; threads have one agent. | Revisit with Postgres (use a per-thread counter row or a sequence). |
| 15 | **`react-remove-scroll` is aliased to a local shim** (`ui/src/lib/removeScroll.tsx`) because the original injects a `<style>` element the CSP blocks. | Low | Keeps the CSP strict; the shim only locks body scroll (no scrollbar-gap compensation or touch-scroll isolation). | Revisit if Radix changes how it imports scroll locking, or if scrolling behind dialogs misbehaves on iOS. |
| 17 | **The WebKit Playwright project was not run** on the development host (missing system libraries). | Low | Chromium and the mobile viewport pass; installing host packages needs root. | Run `sudo npx playwright install-deps webkit` and the suite on a host with them, and check on a real iPhone. |
| 18 | **Template changes don't reach existing installs.** `scripts/install.sh` never overwrites, so a changed `docker/template/compose.yml` or `.env.example` only shows up as a `diff -u` hint. | Low | The install is the user's to edit; overwriting would lose agents and settings. | If template changes become frequent or breaking, add a versioned migration step or move user edits into `compose.override.yml`. |
