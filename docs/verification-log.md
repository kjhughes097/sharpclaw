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

Not done: importing `docker/grafana/sharpclaw-dashboard.json` into Grafana (its API needs
credentials; tech-debt #8).

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

**Not done here (needs the human):**

- The real OpenRouter run covering tool history, the approval queue and reconnect (needs the
  OpenRouter key and the Docker stack).
- Killing and restoring an *interactive CLI* stream mid-reply: covered by the CLI reconnect tests
  (`ChatRunner` resumes with the last id and prints each character once) and by the curl replay
  check above, but not by hand.
- `scripts/verify-isolation.sh` (needs the Docker stack running) and importing the dashboard into
  Grafana (tech-debt #8). The dashboard JSON parses and its panel ids are unique.
- Live confirmation of the new metric names in VictoriaMetrics ([telemetry-notes.md](telemetry-notes.md)).

### Acceptance checklist (pre-3.3 spec section 16)

- [x] Browser-style login, CSRF header, same-origin `Origin`, cookie SSE, logout; 403 without the token or with a foreign `Origin`.
- [x] Admin-key bearer still works for the CLI; user management rejects cookies.
- [x] Wrong-password logins are rate limited; no log line contains a submitted username or password.
- [x] A later turn sees the earlier turn's trusted tool results (fake provider; real model pending).
- [x] An earlier `workspace_read` result is not replayed (stub sent); taint starts fresh each turn.
- [x] `includeTools=true` shows the full exchange, truncated as configured; the default listing shows only user and final messages.
- [x] A thread beyond its window keeps working, drops the oldest turns and reports `droppedTurns`; an oversize turn fails with `context_too_large`.
- [x] A cut stream resumes with no loss or duplicates (curl and CLI tests); a stale last id yields `reset` (automated).
- [x] With nobody watching, an approval stays pending, is listed and can be approved from another terminal; an unanswered one expires and the model is told.
- [x] An `--unattended` message has its approvals denied immediately.
- [x] No prompt, reply, tool argument or result, password, cookie, CSRF token or key in logs or metric labels (tests).
- [ ] All automated tests pass ✔, the docs are updated ✔; the isolation script and dashboard import still need the Docker stack and Grafana credentials.
- [x] No out-of-scope features were added.
