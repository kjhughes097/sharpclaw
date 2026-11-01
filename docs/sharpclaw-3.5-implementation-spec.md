# SharpClaw V3 — Phase 3.5 Implementation Spec (agent-to-agent messaging)

**Audience:** an implementing coding agent (and the human reviewing its work).
**Status:** **implemented** (tasks 1–6 in section 13).
**Scope:** one agent can ask another agent a question and get its answer back within the same turn,
through the gateway, under operator policy. Earlier specs still apply wherever this one doesn't
change them, in particular the risk model and single dispatch path
([3.2](sharpclaw-3.2-implementation-spec.md) §3 and §7), the inbox and turn lifecycle
([3.1](sharpclaw-3.1-implementation-spec.md), [pre-3.3 foundations](sharpclaw-pre-3.3-foundations-spec.md))
and the tool broker ([external tools spec](sharpclaw-external-tools-spec.md) §4).

## 0. How to use this document

- Implement in the task order in section 13. Each task is one branch and one pull request with
  passing tests before the next starts.
- "MUST" / "MUST NOT" are hard requirements. If something is ambiguous, stop and ask.
- Never log, put in an event, or emit as a metric label any message content, reply, tool argument
  or result, soul contents, token or key. Agent **names** are fine in logs (never as anything but
  the existing `agent` metric label).

### 0.1 Decisions

| # | Question | Decision |
|---|---|---|
| Q1 | How does one agent talk to another? | **Synchronously, through a tool.** The asking agent calls `ask_agent(agent, message)`. The gateway queues the message in the recipient's inbox, holds the call open until the recipient's turn finishes (or a timeout), and returns the recipient's reply as the tool result. There is no fire-and-forget send and no reply that starts a new turn later. |
| Q2 | Where do the messages live? | **In the existing `messages` and `threads` tables**, as the 3.1 spec anticipated. An agent's question is a `user` row in a thread the **recipient** owns, with the sending agent recorded. There is no separate mailbox table: the inbox claim, requeue, failure and replay all work as they do today. |
| Q3 | Which thread on the recipient's side? | **One linked thread per (originating thread, sender, recipient)**, reused for follow-up asks from the same conversation, so the recipient keeps its context. |
| Q4 | Who may talk to whom? | **Any agent may ask any other by default** (`AgentMessaging:AllowAll`, revised after the first build, which allowed nothing by default). Gateway config only: `AgentMessaging:Allowed:<sender>` restricts a sender to the agents it lists, and `AllowAll=false` allows only listed pairs. An agent still needs `ask_agent` enabled to ask at all. Not editable through the API or UI. What stops asks going on forever is in section 5.3. |
| Q5 | Is another agent's reply trusted? | **No.** The `ask_agent` result is untrusted: it taints the asking agent's turn, like `web_search`. In the other direction, taint is **propagated**: the recipient's turn starts tainted if and only if the asking turn was tainted when it asked (section 6.3). Always tainting the recipient was rejected: with the default `allowedRiskLevel` of `low`, every tool call the recipient made would need approval. |
| Q6 | If the recipient needs an approval | **Inherit attendance.** The recipient's message copies `unattended` from the message that started the chain: attended chains ask the human in the UI as usual; unattended ones are denied at once. |
| Q7 | `IMessageBus` interface (from the plan) | **Not added.** There is one implementation and no second one planned; `InboxRepository` and the new `AgentAsks` service are the seam. Recorded in [tech-debt](tech-debt.md). |
| Q8 | Web UI scope | **Minimal**: the sender is shown on agent-sent messages, agent-to-agent rows appear in Activity, linked threads link to each other, and the trace lists the asks a turn made. No conversation graph and no policy screen. |

How the plan's outline maps onto this design:

| Plan item | Here |
|---|---|
| Mailbox table, `POST /messages`, delivery via long-poll/SSE | The recipient's inbox (`messages`, long-poll) and `POST /agent/tools/ask_agent` |
| Acks, idempotency keys | The recipient's claim/complete/fail is the ack; (asking turn, tool call id) is the idempotency key (section 5.2) |
| Allowed sender → recipient pairs | `AgentMessaging:Allowed` (section 4) |
| Correlation ids for request/reply | Each ask records the asking turn's correlation id and tool call id (section 3) |
| Hop limits and budgets | `AgentMessaging:MaxHops`, `AgentMessaging:MaxAsksPerChain`, deadlock refusal, and each agent's own daily budget (sections 5.3, 7) |
| TTL and dead-letter status | `AgentMessaging:AskTimeoutSeconds`; asks nobody is waiting for any more end as `failed` with `ask_timeout` or `ask_cancelled` (section 5.5) |
| `IMessageBus` | Not added (Q7) |

## 1. Goal

Today each agent works alone. A coordinator agent that wants a coding agent to look at a repository,
or a research agent to brief it, can't ask. The human has to copy text between threads.

After this phase an operator can let named agents ask other named agents for help. The asking agent
keeps working in its own turn and gets the answer back as a tool result. Every hop is visible to the
human, every model call is still paid for and audited by the agent that made it, and the gateway
stays the only path between agents.

## 2. Hard rules

- **Agents never talk directly.** Agents stay on the internal network, reachable only by the
  gateway. A message to another agent goes only through `POST /agent/tools/ask_agent`.
- **The gateway enforces the policy.** Recipients come only from `AgentMessaging` in gateway
  config, never from the agent, its workspace or a skill. A recipient the sender may not
  ask, or that doesn't exist, is reported as **not found**, with the same error for both.
- `ask_agent` goes through `ToolDispatcher` like every other tool (hardline → risk → taint →
  approval → execute → report). Only `ToolDispatcher` calls `ExecuteUncheckedAsync` (the
  architecture test MUST still pass).
- The reply is **untrusted** (`ReturnsUntrustedContent = true`).
- **No endless asking:** an agent can never be asked by a chain it is already part of or by an
  agent it is waiting on, a chain is never longer than `MaxHops`, and one human message leads to at
  most `MaxAsksPerChain` asks in total (section 5.3).
- Each agent pays for its **own** model calls from its own daily budget. Asking costs the asker
  nothing at the gateway; answering is a normal turn for the recipient.
- No message content, reply or preview in logs, metric labels or events. `message.created`,
  `message.completed` and `error` on the recipient's thread are the existing events, unchanged.

## 3. Data model

One migration (`AddAgentMessaging`). All columns are nullable or defaulted, so existing rows are
unchanged.

**`messages`** (on `user` rows sent by an agent; null or default on everything else):

| Column | Type | Meaning |
|---|---|---|
| `sender_agent_id` | Guid? | The agent that asked. Null for human-sent messages (which keep `sent_by_user`). |
| `origin_message_id` | Guid? | The correlation id of the asking turn (the sender's claimed user message). |
| `origin_tool_call_id` | string? (≤200) | The `ask_agent` tool call id in that turn. |
| `hop` | int, default 0 | 0 for human-sent messages; the origin message's `hop` + 1 for asks. |
| `origin_tainted` | bool, default false | The asking turn was tainted when it asked (section 6.3). |

Unique index on (`origin_message_id`, `origin_tool_call_id`) where `origin_message_id` is not null
(the idempotency key, section 5.2). Index on (`origin_message_id`) for the trace and for cancelling
asks when the asking turn ends.

**`threads`** (on linked threads only):

| Column | Type | Meaning |
|---|---|---|
| `origin_thread_id` | Guid? | The asking agent's thread. |
| `origin_agent_id` | Guid? | The asking agent. |

Unique index on (`agent_id`, `origin_agent_id`, `origin_thread_id`) where `origin_thread_id` is not
null: one linked thread per (originating thread, sender, recipient).

Deleting an agent: there is no agent deletion today. If one is added later it must handle these
references; nothing here cascades.

## 4. Configuration

New options class `AgentMessagingOptions`, section `AgentMessaging`, bound and validated at startup
like the others:

| Key | Default | Range | Meaning |
|---|---|---|---|
| `AgentMessaging:AllowAll` | `true` | — | Every agent may ask every other agent (never itself), except senders with their own `Allowed` list. `false`: only the listed pairs. |
| `AgentMessaging:Allowed:<sender>` | empty | — | Array of the only agent names `<sender>` may ask. Names are matched ignoring case, like `ToolBroker:Agents`. An agent may not list itself (startup error). |
| `AgentMessaging:Descriptions:<name>` | none | ≤ 300 chars | One line telling other agents what `<name>` is for. Shown to the model in the `ask_agent` tool description. Optional. |
| `AgentMessaging:MaxHops` | 3 | 1–10 | The longest chain: human → A is hop 0, A → B hop 1, B → C hop 2, and so on. |
| `AgentMessaging:MaxAsksPerChain` | 10 | 1–1000 | The most asks that may follow from one human message, at every depth together. |
| `AgentMessaging:AskTimeoutSeconds` | 600 | 10–3600 | How long an ask waits for the recipient's reply. The default is longer than the default approval timeout (300 s), so a recipient's approval can be answered before its asker gives up. |
| `AgentMessaging:MaxReplyChars` | 32,000 | 1,000–200,000 | The longest reply returned to the asker; a longer reply is cut and marked `truncated`. |

The message itself is limited by the existing `Messages:MaxContentChars`.

`ask_agent` is a broker tool, so it also has to be enabled in two existing places: in the
`tools` list of the agent's `agent.json`, and for the agent in `ToolBroker:Agents:<name>:Tools` (or
`AllTools`). It is **not** in `ToolBroker:AlwaysApprove` by default. The quick start enables it
for every agent (it adds every broker tool to `agent.json` and sets `AllTools`). With no possible
recipient the tool does nothing useful, so the agent doesn't offer it (section 6.1).

Example (`.env`): describe agents to each other, and restrict `ade` to asking `cody` only:

```bash
AgentMessaging__Descriptions__cody=Coding agent: reads, changes and tests code in its sandbox.
AgentMessaging__Allowed__ade__0=cody
```

## 5. Gateway

### 5.1 The tool: `POST /agent/tools/ask_agent`

`ask_agent` is added to `BrokerTools` (Contracts) and handled by a new `AskAgentToolHandler`
(`IBrokerToolHandler`), so it inherits the broker endpoint's checks (agent token, the tool enabled
for the agent, the turn is the agent's and is claimed) and its audit row in `broker_calls`
(`target` = recipient name, `target_host` = null).

Arguments: `{ "agent": string, "message": string }`. The broker request (`InvokeToolRequest`) gains
an optional `tainted` boolean (default `false`), which the agent fills from its turn (section 6.3).

The handler delegates to a new scoped service, `AgentAsks`, which owns the state changes (as
`MessageTurns` does for human turns). In order:

1. **Validate.** `message` is non-empty and within `Messages:MaxContentChars`; otherwise a
   validation error.
2. **Policy.** The sender must be allowed to ask the recipient (section 4), which must exist and be enabled.
   Not allowed or unknown → `404 not_found` ("Agent not found."). Allowed but disabled → tool
   result `{ ok: false, error: "agent_unavailable" }`.
3. **Chain checks** (section 5.3): hop limit, chain budget, then deadlocks, inside the same write
   transaction as the queueing. On failure → tool result
   `{ ok: false, error: "hop_limit" | "chain_limit" | "agent_cycle" }`. Nothing is queued.
4. **Queue** (one write transaction, section 5.2): find or create the linked thread, insert the
   recipient's `pending` user message, and stage `message.created` on that thread. After commit,
   signal the recipient's inbox.
5. **Wait** (section 5.4) for the message to become `done` or `failed`, or for the timeout.
6. **Answer.** Return a result the dispatcher marks as untrusted:
   - done → `{ ok: true, agent, threadId, reply, truncated }`, where `reply` is the recipient's
     final reply for that correlation id, cut to `MaxReplyChars`;
   - failed → `{ ok: false, error: "agent_failed", reason, message }`, where `reason` is the
     recipient's fail code and `message` is `FailReasons.Describe(reason)`;
   - timeout → `{ ok: false, error: "ask_timeout", threadId }`.

### 5.2 Queueing and idempotency

Inside one `IEventPublisher.WriteAsync` transaction:

- If a message already exists for (`origin_message_id`, `origin_tool_call_id`), queue nothing and
  wait on that message instead. A repeated request for the same tool call (for example, the agent
  retrying after its connection dropped) never asks twice.
- Find the linked thread for (recipient, sender, sender's thread). If there is none, create it,
  owned by the recipient, with `origin_thread_id` and `origin_agent_id` set and the title
  `From <sender>: ` followed by the normal auto-title from the first message. If the existing linked
  thread is archived, use it anyway (archiving only hides it from the UI).
- Insert the message as `MessageTurns.PostAsync` would: `role = user`, `kind = message`,
  `status = pending`, the next `seq`, `ThreadActivity.OnMessage`, and additionally:
  `sender_agent_id`, `origin_message_id`, `origin_tool_call_id`, `hop` = origin hop + 1,
  `origin_tainted` = the request's `tainted`, and `unattended` = the origin message's `unattended`
  (Q6). `sent_by_user` stays null.

Extract the shared insert code from `MessageTurns.PostAsync` rather than duplicating it.

### 5.3 Stopping endless asking: hops, chain budget, deadlocks

Asks are synchronous, so a chain is a call stack: it can only grow while every agent in it is
waiting. Three checks bound it, run inside the write transaction that queues the ask (so two asks
can't both pass and then deadlock). The sender's current turn is its claimed message M (the
request's correlation id).

- **Hop limit (depth):** refuse if `M.hop + 1 > MaxHops`.
- **Chain budget (breadth):** every ask records `chain_root_id`, the human message the chain started
  from (M's root, or M itself at hop 0). Refuse if that root already has `MaxAsksPerChain` asks.
  Without it, each agent could ask once per tool round at every level (10 rounds × 3 levels ≈ 1,000
  turns from one message).
- **Chain cycle:** walk M's chain upward through `origin_message_id` (at most `MaxHops` rows),
  collecting the agent that owns each message. Refuse if the recipient is the sender or any agent in
  the chain. This is what stops A → B → A from deadlocking, since A is still waiting for B.
- **Deadlock across chains:** refuse if the recipient is waiting, directly or through other agents,
  on the sender. Starting from the recipient, follow "agent X's claimed turn has an unanswered
  (`pending` or `claimed`) ask to agent Y" edges; if they reach the sender, the ask could never be
  answered, because each agent runs one turn at a time. This catches A ⇄ B and longer circles such
  as A → B, B → C, C → A built from separate chains.

The timeout (section 5.5) stays as the final backstop.

### 5.4 Waiting, heartbeat and cancellation

- The handler waits on the request: it checks the message's status, then waits about one second on
  an in-process signal raised when a turn finishes (or polls once a second; no new event type), and
  repeats.
- On every loop it **refreshes the sender's claim** (`InboxRepository.TouchClaimAsync`), so the
  sender's turn isn't requeued as stale while it waits (`Inbox:ClaimTimeoutSeconds` defaults to
  300 s, shorter than the ask timeout).
- If the request is cancelled (the agent disconnected or the gateway is stopping), stop waiting and
  cancel the ask (section 5.5).

### 5.5 When nobody is waiting any more (TTL and dead-letter)

An ask that has nobody waiting for it any more doesn't run, if it can be stopped in time:

- **Timeout:** if the ask is still `pending`, mark it `failed` with reason `ask_timeout`, with an
  `error` event on the recipient's thread. If the recipient has already claimed it, let the turn
  finish: its reply stays in the linked thread (the recipient keeps that context for the next ask),
  but it is not delivered to the asker.
- **The asking turn ends** (`MessageTurns.CompleteAsync`, `FailAsync` or `ReleaseAsync` on a stale
  requeue) **or the request is cancelled:** every still-`pending` ask whose `origin_message_id` is
  that turn is marked `failed` with reason `ask_cancelled`, in the same way.

`ask_timeout` and `ask_cancelled` are added to `FailReasons` (with `Describe` text). These failed
rows are the dead-letter record: they stay in the linked thread and are visible in the UI and the
trace.

### 5.6 Context for the recipient

`ContextBuilder` presents an agent-sent user message to the recipient's model as
`[Message from agent <sender>]\n\n<content>`. The stored content is unchanged. The prefix counts in
the token estimate.

### 5.7 Policy for the agent: `GET /agent/tools`

`AgentToolPolicyResponse` gains `askAgents: [{ name, description }]`: the sender's `Allowed`
recipients that exist, in config order, with their optional descriptions. Disabled agents are
included (their state can change while the asker is running; step 2 reports it at call time).

### 5.8 Inbox

`InboxMessageDto` gains `fromAgent` (sender name, or null) and `tainted` (`origin_tainted`). The
claim query is unchanged: asks and human messages share one queue, oldest first.

## 6. Agent

### 6.1 The `ask_agent` tool

A new `BrokerTool` in `Tools/Broker/AskAgentTool.cs`:

- **Description** (built once at startup from `askAgents`): what the tool does, that the other
  agent sees only the message (not this conversation), that the call waits for its answer, and one
  line per recipient: `- <name>: <description>`. Without any recipients the tool isn't registered
  and a warning is logged.
- **Schema:** `agent` (string, `enum` of the recipient names), `message` (string).
- **Assess:** unknown recipient, or an empty message → `Blocked`. Otherwise `Scored(Low, "asks
  another agent")`. The gateway's policy and the recipient's own tool policy are the real controls.
  Taint raises it to `medium`, so after one untrusted result (including another agent's reply) the
  next ask needs approval when `allowedRiskLevel` is `low`. This is intended; an operator who wants
  agents to chain several asks without a prompt raises `allowedRiskLevel` to `medium`.
- **Summarize:** `ask <agent> (<n> chars)`. Never the message itself.
- **ClassifierFields:** `{ tool, agent, chars }`.
- **ReturnsUntrustedContent:** `true`.

### 6.2 Waiting

The agent's `HttpClient` already has no timeout, so the call simply waits. The agent sends nothing
else during an ask (its tool calls are dispatched one at a time), and the gateway keeps its claim
fresh (section 5.4).

### 6.3 Taint in both directions

- `ToolCallContext` gains `Tainted`, filled by `ToolDispatcher` from the turn. `BrokerTool` passes it
  as `InvokeToolRequest.tainted`. Other broker handlers ignore it.
- When the agent claims a message with `tainted = true`, it starts the turn with
  `TurnContext.Tainted = true`.

The flag comes from the asking agent's own code, not from its model. A model that has been prompt
injected can't clear it.

## 7. Budgets and limits

Each agent's daily budget, `Chat:MaxLlmCallsPerMessage` and `MaxToolIterations` apply to its own
turn exactly as today. An ask counts as one tool iteration for the asker. A chain is bounded by
`MaxHops`, the cycle checks and `AskTimeoutSeconds`. The asker's own claim stays fresh while it
waits, so the claim timeout doesn't fire. Per-thread or per-chain spending caps are in the 3.7+
backlog.

## 8. Client API and contracts

- `MessageDto` gains `sentByAgent` (sender name, or null).
- `ThreadDto` and the thread-list row gain `originThreadId` and `originAgentName` (null on normal
  threads).
- `ActivityItemDto`: for an agent-sent message, `from` is the sender's name and `to` the recipient's;
  for the recipient's reply, `from` is the recipient and `to` the sender's name.
- `TraceTurnDto` gains `asks: [{ toolCallId, agentName, threadId, messageId, status, failReason }]`:
  the asks that turn made, from `messages` where `origin_message_id` is the turn's correlation id.
- No new routes and no new SSE events. Update the OpenAPI snapshot and the generated TypeScript
  client.

## 9. Web UI (minimal)

- In chat, an agent-sent message shows **from `<sender>`** (with the sender's avatar or name chip)
  instead of the user's name.
- A linked thread's header shows "Asked by `<sender>`" with a link to the originating thread.
- An `ask_agent` tool card shows the recipient and, when the stored result has a `threadId`, links to
  the linked thread.
- Linked threads appear in the recipient's thread list with a small "from `<sender>`" badge.
- Activity shows agent → agent rows using the DTO fields above.
- The trace view lists each turn's asks with status and a link to the linked thread.
- Approvals raised in linked threads already show up in the approvals inbox; no change.

## 10. Telemetry

- Existing broker metrics cover asks (`tool` = `ask_agent`, with status). No new metric names.
- Log on the gateway, in the turn's scope: `agent ask queued` (sender, recipient, hop),
  `agent ask finished` (sender, recipient, outcome `done`/`failed`/`ask_timeout`/`ask_cancelled`,
  duration), and `agent ask refused` (sender, reason `not_found`/`agent_unavailable`/`hop_limit`/`agent_cycle`).
  Never the message or reply.
- Update [telemetry-notes.md](telemetry-notes.md) with the new log lines.

## 11. Tests

- **Config:** `Allowed`, `Descriptions`, ranges; an agent listing itself fails startup.
- **Policy:** recipient not allowed and recipient unknown both return `404` with the same body; a
  disabled recipient → `agent_unavailable`; `ask_agent` not enabled in `ToolBroker` → `404`; the
  turn not claimed by the sender → `404`; the client listener → `404`.
- **Queueing:** creates a linked thread owned by the recipient with origin fields; a second ask from
  the same originating thread reuses it; a different originating thread gets a new one; an archived
  linked thread is reused; `hop`, `unattended`, `origin_tainted` and `sender_agent_id` are set;
  `message.created` on the recipient's thread; the same (turn, tool call id) twice queues one
  message.
- **Chain:** `MaxHops` boundary; `MaxAsksPerChain` across levels, and a new human message gets a
  fresh budget; A → B → A refused; A → A refused; two agents asking each other and a three-agent
  circle across separate chains refused; a refusal queues nothing.
- **Policy:** by default any agent may ask any other; an `Allowed` list restricts its sender;
  `AllowAll=false` allows only listed pairs.
- **Waiting:** reply returned (and truncated at `MaxReplyChars`); recipient failure returned with its
  reason; timeout while pending → recipient message `failed`/`ask_timeout` with an `error` event;
  timeout while claimed → recipient finishes, reply stays in the linked thread; the sender's claim
  is refreshed while waiting (`FakeTimeProvider` past `ClaimTimeoutSeconds`, no requeue); request
  cancelled → `ask_cancelled`; the asking turn completing, failing or being requeued cancels its
  pending asks.
- **Approvals:** an attended chain raises `approval.requested` on the linked thread; an unattended
  chain denies at once.
- **Context:** an agent-sent message is replayed with the prefix; the estimate includes it.
- **Agent:** tool not registered without recipients; description and enum built from `askAgents`;
  Assess (blocked, low, taint → medium); Summarize has no content; the result taints the turn;
  `tainted` passed to the gateway; a claimed message with `tainted` starts a tainted turn.
- **Architecture and privacy tests:** still pass; no message or reply content in logs or events
  (extend `PrivacyTests` with an ask).
- **Integration (`GatewayFixture`):** two agents, FakeProvider scripts for both. A asks B; B
  replies; A's turn completes with B's answer in its tool result; both threads, the trace, activity
  and `broker_calls` are correct.
- **UI:** Vitest + MSW: sender chip, linked-thread header link, ask tool card link, trace asks.
  Playwright: one e2e path on the fake-provider stack where agent `ade` asks `cody`.
- **Snapshots:** OpenAPI snapshot and `npm run check:api` updated deliberately.

## 12. Documentation

- README: an "Agent-to-agent" section (configuration, the three gates, the example above, what the
  human sees, the taint and approval behaviour).
- [architecture.md](architecture.md): the ask flow (a sequence diagram), the new columns, the
  invariants (no direct agent traffic, gateway policy, no cycles).
- [ui.md](ui.md): the UI additions.
- [tech-debt.md](tech-debt.md): Q7 (no `IMessageBus`), a claimed ask runs to completion after a
  timeout, the policy is config-only, recipients are read at agent startup.
- [sharpclaw-v3-plan.md](sharpclaw-v3-plan.md): mark 3.5 built when task 6 lands.
- Template and quickstart: a commented example in the template's `.env.example`. The quickstart
  doesn't set up pairs (it can't know the operator's intent).

## 13. Task order

| # | Task |
|---|---|
| 1 | This spec; roadmap and architecture links; tech-debt entries |
| 2 | Gateway: migration, `AgentMessagingOptions` with validation, `BrokerTools.AskAgent`, `FailReasons` additions, `askAgents` on `GET /agent/tools`, `fromAgent`/`tainted` on the inbox |
| 3 | Gateway: `AgentAsks` and `AskAgentToolHandler` (policy, chain checks, queueing, waiting and heartbeat, timeout and cancellation, cancel-on-turn-end in `MessageTurns`), context prefix, telemetry; integration tests |
| 4 | Agent: `AskAgentTool`, `ToolCallContext.Tainted` and `InvokeToolRequest.tainted`, starting tainted from the inbox; tests |
| 5 | Client API: `sentByAgent`, thread origin fields, activity, trace `asks`; OpenAPI snapshot and TS client |
| 6 | UI (section 9) with Vitest and the Playwright path; docs (README, architecture, ui.md, telemetry-notes, template `.env.example`); plan marked built |

## 14. Out of scope

Fire-and-forget messages and replies that start a new turn later (async mailbox); broadcast or
several recipients in one call; an `IMessageBus` abstraction or an external broker (NATS); agents
reaching each other other than through the gateway; editing the policy through the API, CLI or UI;
per-thread or per-chain budgets (3.7+ backlog); detecting cross-chain cycles longer than two agents;
streaming the recipient's reply into the asker's thread; a conversation-graph view; new CLI
commands; scheduled or autonomous agents (3.6); Google A2A compatibility (3.7+).
