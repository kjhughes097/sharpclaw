# Verification log

Dated records of manual and live verification. The README links here instead of carrying them.

## Phase 3.1 (2026-10-25)

Verified with the **fake provider** (no secrets or external network):

- All automated tests passed: gateway 37, agent 10, CLI 3.
- End-to-end locally: `sharpclaw ask assistant "…"` streamed `Fake reply to: …`, and the call
  appeared in `sharpclaw audit` with model, tokens, cost, latency and status `ok`.
- End-to-end in Docker Compose: gateway healthy via `--healthcheck`, agent container running,
  `sharpclaw ask` streamed a reply through the containerised agent.
- `scripts/verify-isolation.sh` passed all checks against the running stack.

Verified against the live environment:

- **Telemetry:** the smoke test and gateway metrics land in VictoriaMetrics, and logs land in Loki
  with `correlation_id` as structured metadata. One message was traced end to end in Loki by
  `correlation_id`. Landed metric names are in [telemetry-notes.md](telemetry-notes.md).
- **Real OpenRouter** (`openai/gpt-4o-mini`, Docker Compose stack): `sharpclaw ask` streamed real
  replies through the isolated agent. Each call was audited with tokens, cost (e.g. 68/28 tokens,
  $0.000027, 1235 ms) and status `ok`.
- **Budget, live:** lowering the cap below today's spend gave `429` → `fail(budget_exceeded)` →
  `Error: budget_exceeded` in the CLI, plus a `rejected` audit row.
- `scripts/verify-isolation.sh` passed all checks on the OpenRouter-backed stack.
- No prompt or reply text found in the gateway logs in Loki.

The dashboard was imported later (see the pre-3.3 section).

## Phase 3.2 (2026-10-25)

**Fake provider (automated):** `AgentToolEndToEndTests` runs the real agent (HTTP client, loop,
dispatcher, workspace tools) against the real gateway with a scripted FakeProvider: multi-step
write → read → reply; high-risk delete approved and denied by a watching user; unattended delete
denied immediately; `../../etc/passwd` blocked by the hardline rules. All automated tests passed
(gateway 101, agent 72, CLI 11).

**Real OpenRouter** (`openai/gpt-4o-mini`, Docker Compose stack, `allowedRiskLevel: low`,
smart mode on):

| Scenario | Result |
|---|---|
| Multi-step: create `notes/shopping.md`, read it back, report | `[tool] workspace_write`, `[tool] workspace_read`, correct reply; file on the host |
| Delete `notes/old.md`, user answers `n` | Prompt shown (`high risk: delete notes/old.md`); denied; file kept; model explained |
| Same, user answers `y` | Approved; file deleted |
| Delete with nobody watching the thread | Denied immediately by the gateway; file kept; model said no approver was available |
| Write `scripts/hello.sh` (uncertain) | Smart mode classified it `low` via one `risk_assessment` LLM call; allowed |
| Read a file, then write a new file in the same turn | Taint raised the write from low to **medium**; approval prompt; approved |
| Audit | `sharpclaw audit tools` showed every call with status, decision, risk and taint |
| Telemetry | `sharpclaw.tool.calls` and `sharpclaw.approvals` landed in VictoriaMetrics |
| Isolation | `scripts/verify-isolation.sh` passed all checks; agent writes `/workspace`, `/config` read-only |

(The "nobody watching → denied" row is superseded by the pre-3.3 approval queue.)

## Pre-3.3 foundations (2026-10-27)

**Automated:** all tests pass in Release (gateway 169, agent 77, CLI 25), 0 build warnings.

**Fake-provider walk-through** (local gateway and agent processes, Release build, curl as the
browser). Every check passed:

| Area | Checks |
|---|---|
| Browser auth | `users create --password-stdin`; login sets `sharpclaw_session` with `path=/; samesite=strict; httponly` (no `secure` over plain HTTP); cookie `GET` works; cookie `POST` with `Origin` + `X-CSRF-Token` → 201, without the token → 403, with a foreign `Origin` → 403; cookie-only SSE gets `retry: 3000` and `ready`; `/api/users` → 401 with the cookie, 200 with the admin key, no hashes in the body; logout → 204, then 401; five wrong passwords → 401, the sixth → 429 with `Retry-After: 899`; the gateway log contains no submitted username, password or hash, and does contain `login succeeded` |
| Tool history | `ask … "make a note"` printed both tool calls and the reply; default listing 2 rows, `includeTools=true` 6 rows in `seq` order with the read result `untrusted`; with the agent stopped, the context endpoint for the next turn replayed the trusted write result in OpenAI shape, replaced the `workspace_read` result with the stub, and ended with the new user message; the restarted agent answered it |
| Context | Window 4000 tokens, eight 600-character turns: every turn answered (no preflight rejections), thread reports `droppedTurns: 3`, `windowTokens: 4000`; a 12,000-character message failed with `context_too_large` |
| SSE replay | A stream cut mid-reply (45 characters received) and resumed with `Last-Event-ID` received a `snapshot` and the remaining deltas; the reassembled text equalled the final reply, nothing lost or duplicated |
| Approvals | With nobody watching, the delete approval stayed `pending`, appeared in `sharpclaw approvals list`, was approved from the CLI and the agent completed the delete; `ask --unattended` was denied at once and the file kept; an unanswered approval expired (40 s timeout) and the model said so |
| Logs | No error-level lines in the gateway log |

Two defects were found and fixed during the walk-through: deltas already contained in a
reconnect `snapshot` could be sent again (deltas now carry an internal buffer offset and are
dropped when the snapshot covers them), and a context filled to its budget could still fail the
chat preflight (the agent's reserve now uses the gateway's exact estimate of the soul message and
tool schemas, and the dropped-turns note is budgeted).

**Migration on a copy of the real database:** the Docker volume `sharpclaw_gateway-data` was copied
read-only; starting the new gateway on the copy applied the five new migrations. 29 messages in 13
threads kept their `(created_at, id)` order, `seq` runs 1..n per thread with no duplicates, every
row is `kind = message`, and the API listed all threads with their `context`.

**Real OpenRouter** (`openai/gpt-4o-mini`, Docker Compose stack rebuilt from this branch, which
migrated the live database; a backup was taken first; `allowedRiskLevel: low`, smart mode on).
The CLI was driven by hand in an interactive `sharpclaw chat`:

| Scenario | Result |
|---|---|
| Create `notes/shopping.md`, read it back | `[tool] workspace_write`, `[tool] workspace_read`, correct reply |
| Next turn, no tools: "which tools did you call, what did write return?" | The model listed write then read, said the write succeeded, and that the read content was not available (the untrusted result was stubbed) |
| Delete the file, answer `n` at `Allow? [y/N]` | Denied; the model explained |
| Ask again, then close the CLI at the prompt | `sharpclaw approvals list --status pending` showed the approval; `approvals approve <id>` from another terminal let the agent finish; file deleted; reply stored |
| `ask … "Delete old.md" --unattended` | Denied by the gateway at once (3.8 s total); file kept |
| Stream cut mid-reply: the CLI talked through a TCP proxy that was killed after 640 bytes of a 400-word story and restored 3 s later | The CLI reconnected and finished; printed text (2,375 chars) identical to the stored reply |
| Context: window 2,900 tokens on the 16-row thread | Reply worked; thread reported `droppedTurns: 1`. A 4,800-character message with a 3,000 window failed with `Error: context_too_large`. Window restored to 32,000 (set explicitly) |
| Browser auth against the stack | Login 200 with `path=/; samesite=strict; httponly`; cookie GET 200; cookie POST with CSRF 201, without 403, foreign `Origin` 403; `/api/users` 401 with cookie, 200 with admin key; cookie SSE on `/api/events` framed; logout 204 then 401; no password or hash in the gateway log |
| `includeTools=true` on the chat thread | 16 rows in `seq` order, the `workspace_read` result flagged `untrusted` |
| Isolation | `scripts/verify-isolation.sh`: all checks passed |
| VictoriaMetrics | All new names landed as documented in [telemetry-notes.md](telemetry-notes.md): `sharpclaw.auth.logins`, `sharpclaw.auth.failures` (with `method`), `sharpclaw.context.truncations`, `sharpclaw.context.rejections`, `sharpclaw.context.estimated_tokens_*`, `sharpclaw.sse.subscribers`, `sharpclaw.approvals.pending`, `sharpclaw.approval.decision_time_*`, `sharpclaw.tool.unreported`; every new dashboard panel query returns data |

**Grafana:** `docker/grafana/sharpclaw-dashboard.json` was imported into the home Grafana (13.2.2)
through its API (uid `sharpclaw-gateway`, 19 panels; the datasource variables resolve to the
VictoriaMetrics and Loki datasources). Every metrics panel query, run through Grafana's datasource
proxy over the last 6 hours, returned series; only *Inbox depth* and *budget rejections* were empty,
as expected with no pending messages or rejections in that window.

### Acceptance checklist (pre-3.3 spec section 16)

- [x] Browser-style login, CSRF header, same-origin `Origin`, cookie SSE, logout; 403 without the token or with a foreign `Origin`.
- [x] Admin-key bearer still works for the CLI; user management rejects cookies.
- [x] Wrong-password logins are rate limited; no log line contains a submitted username or password.
- [x] A later turn sees the earlier turn's trusted tool results (fake provider and real model).
- [x] An earlier `workspace_read` result is not replayed (stub sent); taint starts fresh each turn.
- [x] `includeTools=true` shows the full exchange, truncated as configured; the default listing shows only user and final messages.
- [x] A thread beyond its window keeps working, drops the oldest turns and reports `droppedTurns`; an oversize turn fails with `context_too_large`.
- [x] A cut stream resumes with no loss or duplicates (curl, CLI tests, and the real CLI through a killed proxy); a stale last id yields `reset` (automated).
- [x] With nobody watching, an approval stays pending, is listed and can be approved from another terminal; an unanswered one expires and the model is told.
- [x] An `--unattended` message has its approvals denied immediately.
- [x] No prompt, reply, tool argument or result, password, cookie, CSRF token or key in logs or metric labels (tests).
- [x] All automated tests pass, the isolation script passes, the dashboard imports, and the docs are updated.
- [x] No out-of-scope features were added.

## Phase 3.3 (2026-10-28)

**Automated:** gateway 202, agent 77, CLI 25 tests pass; Release build 0 warnings. UI: 155 Vitest
tests (unit, integration, jest-axe on each main view, run with `navigator.clipboard` and
`crypto.randomUUID` undefined), `lint`, `typecheck`, `check:api` and `build` pass; initial JS
193.7 KB gzipped, `index.html` has no inline script or style.

**End-to-end (Playwright, isolated fake-provider stack from `ui/e2e/stack.sh`):** 17 tests pass on
Chromium and the mobile viewport (Pixel 7, `@core` flows). Every test also asserts that all
requests are same-origin and the console shows no CSP violation.

| Area | Checks |
|---|---|
| Auth | Wrong password → generic error; login → `/chats`; sign-out → `/login`; disabling the user (revokes sessions) → next action lands on `/login?next=…` with nothing left on screen |
| Chat | Reply streams progressively (partial text seen before the end); reload mid-reply resumes from the snapshot with no duplicated text; offline 1.5 s then online, text complete and not duplicated; markdown: `<script>` inert, remote image shown as a text link and never fetched, `javascript:` link dropped |
| Banners | Disabled agent, budget used up, dropped-turns banner and `/ 1.0k tokens` meter with a 1,000-token window |
| Tools and approvals | Pending card in the thread and the Approvals page, tab title `(1) SharpClaw`; approve → turn continues and the tool card expands to plain-text arguments and result; deny → `denied`; approval decided in a second tab clears the first; unanswered approval expires (25 s) and the turn finishes |
| Agents, audit, trace | Create agent → token shown once, gone after "I've saved it"; revoke a token; trace panel shows the turn; audit tabs list the LLM and tool calls |

**Docker:** `docker/Dockerfile.gateway` builds with the Node 24 stage; the image serves the UI on
the client port with the exact CSP, reports the git sha from `GET /api/system`, and runs as `app`.

**Not yet done (needs the human):** the WebKit project (host libraries need root:
`sudo npx playwright install-deps webkit`); redeploying the running Compose stack and re-running
`scripts/verify-isolation.sh` against it; and the section 16 walk-through from a phone on the LAN
with a real OpenRouter session.

## Phase 3.3.1 — tabbed UI redesign (2026-10-28)

**Automated:** gateway 283, agent 80, CLI 25 tests pass; Release build 0 warnings. UI: 194 Vitest
tests (shell and tabs, Overview charts/credits/live pause, Prompts model picker and status bar,
agent tab, jest-axe on each tab), `lint`, `typecheck`, `check:api` and `build` pass; initial JS
199.7 KB gzipped, `index.html` has no inline script or style.

**End-to-end (Playwright, isolated fake-provider stack):** 22 tests pass on Chromium and the mobile
viewport (Pixel 7, `@core`), each also asserting same-origin requests and no CSP violation.

| Area | Checks |
|---|---|
| Shell | Login lands on the tabs; `/prompts` and agent-tab links; capitalised agent tabs don't clash with admin links |
| Overview | A message posted through the API shows on the agent card (prompt and reply) and changes today's total without a reload; a row opens the thread in Prompts |
| Prompts | Streaming, reload/offline recovery, markdown safety, banners and approvals as in 3.3; the context figure is in the status bar; switching the model to `fake/other` shows the toast and status bar, and the next reply's `llm_calls.model` is `fake/other` |
| Agents | Create an agent (token shown once) from the admin table, open its tab, revoke a token |

**Manual (screenshots, fake provider):** Overview, Prompts and agent tab at 1600×900 in dark and
light, and at 360 px: tab strip scrolls, toolbar and status bar wrap, sessions open as a drawer.

**Not yet done (needs the human):** the WebKit project (host libraries need root); the credits card
against a real OpenRouter management key; and a walk-through of the spec's section 15 checklist
with a real OpenRouter session.

## Agent tools (2026-10-28)

Verified with the fake provider and unit tests (no secrets or external network):

- All automated tests passed: gateway 283, agent 192, CLI 25. Release build has 0 warnings.
- **Time zones in the agent image:** `mcr.microsoft.com/dotnet/runtime:10.0` (Ubuntu 24.04) ships
  `tzdata`; a .NET program run as the `app` user in that image resolved `Europe/London` with
  `TimeZoneInfo.FindSystemTimeZoneById`, so `current_time` works in the container.
- `docker/Dockerfile.agent` builds with the new tools.

## External tools (2026-10-30)

Verified with the **fake provider** on the dev stack (`sharpclaw-dev`, profile `sandboxes`,
`sandbox-coder` enabled):

- Automated tests: gateway 500, agent 267, sandbox 72, CLI 25; UI lint, typecheck, 197 Vitest
  tests, `check:api` and build (Node 24).
- Broker calls made by hand as the `coder` agent inside a claimed turn: `http_fetch
  https://example.com` (HTTPS through the egress proxy's CONNECT tunnel, converted by pandoc),
  `download_to_workspace` (file landed owned by the host user), `git_clone
  https://github.com/octocat/Hello-World` (base commit recorded by the gateway), a clone from
  bitbucket.org refused by the gateway (`host_not_allowed`), and `http_fetch` of
  `169.254.169.254` refused by the proxy (`403`).
- The runner's `/proc/1/environ` (which holds its service secret) is unreadable from code in the
  sandbox (`PR_SET_DUMPABLE`).
- `scripts/verify-isolation.sh`: **all checks passed**, including agents getting `407` from
  `:8082`, `:8082` not published, `sandbox-coder` only on `sbx-coder` with no direct route out, the
  proxy refusing metadata, LAN and gateway addresses, `401` from the client and agent listeners,
  mounts limited to its workspace and cache, and a read-only root filesystem.

Not yet verified live: `web_search` against OpenRouter, the GitHub tools against api.github.com
(covered by a fake API), and a sandbox-to-sandbox check (the dev stack has one sandbox).
