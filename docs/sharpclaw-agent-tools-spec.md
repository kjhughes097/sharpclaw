# SharpClaw V3 — Agent Tools Spec

**Audience:** an implementing coding agent (and the human reviewing its work).
**Status:** implemented. Open questions resolved in section 0.1.
**Scope:** only tools that run entirely **inside the agent**, against its own workspace or local
state. No new gateway endpoints, no network access. Tools that need the gateway or an external
service (fetch, web search, GitHub, budget status, memory, agent-to-agent, scheduling) are out of
scope here and belong in their own phase specs. The
[3.2 spec](sharpclaw-3.2-implementation-spec.md) still applies wherever this document does not
change it, in particular the risk model (§3), agent configuration (§4) and the single dispatch path
(§7).

## 0. How to use this document

- Implement in the task order in section 9. Each task is one branch and one pull request, with
  passing tests, before the next task starts.
- "MUST" / "MUST NOT" are hard requirements.
- If something is ambiguous or contradictory, stop and ask the human rather than guessing.
- Never log or emit as a metric label any prompt, reply, tool argument, tool result, soul contents,
  token or key.

### 0.1 Decisions

- **Q1 — build `calculate`?** Yes. The grammar in section 5.2 is small and fixed, hand-parsed, and
  can't reach code, files or the network.
- **Search `glob`:** a pattern without `/` matches the file name (`*.md` finds Markdown files at any
  depth); a pattern with `/` matches the path relative to the search directory, as `workspace_find`
  does.
- **Overwrite risk:** move/copy are raised to the overwrite level only when `overwrite` is `true`
  **and** the destination exists, the same rule `workspace_write` uses for overwrites.
- **Result sizes:** `workspace_append` reports `bytes` as the file's size after the append.

## 1. Goal

Agents today have four tools: `workspace_list`, `workspace_read`, `workspace_write` and
`workspace_delete`. That is enough to prove the tool framework, but it makes common jobs clumsy:

- finding something means listing and reading files one at a time;
- changing one line means re-sending the whole file (up to 256 KB) through `workspace_write`;
- keeping a log or notes file means read-then-rewrite;
- the model does not know the current date or time;
- files larger than 64 KB can only be read in part.

This spec adds tools that close those gaps **without** changing the trust model or the gateway:
every tool runs in the agent process, goes through `ToolDispatcher` (hardline rules → risk →
approval → execute → report), and none executes code or touches the network.

## 2. Hard rules (unchanged, restated)

- **No tool may execute code or shell commands**, directly or indirectly. This includes
  interpreters, `Process.Start`, Roslyn/scripting, `DataTable.Compute`, regex engines driven by
  model-supplied patterns without a timeout, and `git` (hooks run code).
- Tools never read agent policy from the writable workspace.
- Every new tool implements `IAgentTool`; only `ToolDispatcher` calls `ExecuteUncheckedAsync`
  (the existing architecture test MUST cover the new tools).
- `Summarize` output (approval summaries) never includes file content or free text supplied by the
  model (search queries, replacement text). Paths, sizes and flags only.
- Tools are opt-in per agent via the `tools` list in `/config/agent.json`; unknown names still fail
  startup.

## 3. Shared workspace rules

All workspace tools in section 4 reuse `WorkspaceRoot.Resolve` and therefore the 3.2 §6 hardline
rules (→ `Blocked`): empty or absolute path, a path resolving outside the root, any existing path
component that is a symbolic link, or exceeding a cap.

- **Sensitive path:** the existing `workspace_write` definition (any dotfile segment, or extension
  `.sh .bash .ps1 .bat .cmd .exe .dll .so .py .js`). A tool that *creates or changes* a sensitive
  path returns `Uncertain` (smart mode may classify it; otherwise it needs approval).
- **Atomic writes:** any tool that changes a file writes a temporary file in the same directory and
  renames it, as `workspace_write` does today.
- **File size cap:** no tool may leave a file larger than `MaxWriteBytes` (256 KB).
- **Recursive walks** (search, find) skip symbolic links (they are never followed) and stop at the
  caps below, returning `truncated: true`.
- **Binary files:** a file whose first 8 KB contains a NUL byte is treated as binary and skipped by
  content tools.

## 4. New workspace tools (agent only, no gateway changes)

| Tool | Arguments | Result | Risk | Untrusted result |
|---|---|---|---|---|
| `workspace_search` | `query` (literal text, 1–200 chars), `path` (optional dir), `glob` (optional, e.g. `*.md`) | `{ matches: [{ path, line, text }], truncated }` | `low` | **yes** |
| `workspace_find` | `pattern` (glob: `*`, `?`, `**`), `path` (optional dir) | `{ paths: [...], truncated }` | `low` | no |
| `workspace_edit` | `path`, `oldText`, `newText` | `{ path, bytes }` | `medium`; sensitive `Uncertain` | no |
| `workspace_append` | `path`, `content` (max 64 KB) | `{ path, bytes, created }` | `low`; sensitive `Uncertain` | no |
| `workspace_move` | `from`, `to`, `overwrite` (default `false`) | `{ from, to, overwritten }` | `medium`; overwrite `high`; sensitive `Uncertain` | no |
| `workspace_copy` | `from`, `to`, `overwrite` (default `false`) | `{ from, to, bytes, overwritten }` | `low`; overwrite `medium`; sensitive `Uncertain` | no |

Where a call matches more than one row (e.g. an overwrite of a sensitive path), the **most
cautious** assessment wins: `Uncertain` over a score, and the higher score otherwise.

### 4.1 `workspace_search`

- Case-insensitive **literal** substring match, line by line. No regex (avoids ReDoS and keeps the
  "no code" line clear).
- Caps: at most 50 matches, 2,000 files visited, files larger than 1 MB skipped; `text` is the
  matching line trimmed to 200 characters.
- Results contain file content, so the tool is untrusted (taints the rest of the turn).
- Summary: `search notes/ (*.md)` — never the query.

### 4.2 `workspace_find`

- Matches paths relative to `path` (default root) against the glob; at most 200 results, sorted
  ordinally. Directories are not returned.
- Names are not treated as untrusted, matching `workspace_list`.
- Summary: `find **/*.md in notes/`.

### 4.3 `workspace_edit`

- `oldText` MUST occur **exactly once** in the file. Zero matches → error `no_match`; more than one
  → error `ambiguous_match` with the count, so the model can add context and retry.
- `oldText` must be non-empty; `newText` may be empty (deletion of that text).
- The file must exist (`not_found` otherwise) and be a text file within the size cap before and
  after the edit.
- Risk is `medium` because it changes existing content, the same as an overwrite.
- Summary: `edit notes/a.md (−120 B, +340 B)`.

### 4.4 `workspace_append`

- Creates the file (and parent directories) if missing, otherwise appends. No separator is added;
  the model controls newlines.
- Implemented as read + write-temp + rename so a crash never leaves a partial file.
- Summary: `append notes/log.md (1.2 KB)`.

### 4.5 `workspace_move` and `workspace_copy`

- Files only (directories → `Blocked` with reason `files only`), matching `workspace_delete`.
- Both `from` and `to` go through the hardline rules; `from` must exist.
- If `to` exists and `overwrite` is `false` → error `exists`. If `overwrite` is `true`, the risk is
  raised as in the table. Sensitive applies if **either** path is sensitive.
- Copy enforces the 256 KB size cap on the source.
- Summary: `move a.md → archive/a.md (overwrite)`.

### 4.6 `workspace_read` paging (change to an existing tool)

- New optional argument `offset` (bytes, default `0`). Reading starts at the first UTF-8 character
  boundary at or after `offset`.
- Result gains `offset` and, when truncated, `nextOffset`, so the model can page through a large
  file with repeated calls. The 64 KB per-call cap is unchanged.
- No change to risk (`low`) or taint (untrusted).

## 5. Built-in utility tools (no file or network access)

| Tool | Arguments | Result | Risk | Untrusted result |
|---|---|---|---|---|
| `current_time` | `timezone` (optional IANA id, e.g. `Europe/London`) | `{ utc, local, timezone, dayOfWeek }` | `low` | no |
| `calculate` | `expression` (max 200 chars) | `{ expression, value }` | `low` | no |

### 5.1 `current_time`

- Uses the injected `TimeProvider` (never `DateTime.UtcNow`), so tests use `FakeTimeProvider`.
- No `timezone` → `local` equals `utc` and `timezone` is `"UTC"`. Unknown id → error
  `invalid_timezone`. The agent image (`dotnet/runtime`, Debian) ships `tzdata`; the Docker task
  MUST verify a named zone resolves in the container.
- Summary: `current time`.

### 5.2 `calculate`

- A hand-written recursive-descent parser over a fixed grammar: decimal numbers, `+ - * / % ^`,
  unary minus, parentheses, and the functions `abs`, `round`, `floor`, `ceil`, `sqrt`, `min`, `max`.
  No identifiers, variables or any other function. Evaluated as `double`.
- MUST NOT use `DataTable.Compute`, Roslyn, any expression/scripting library or `eval`-like API.
- Depth limit of 32 nested parentheses/calls; division by zero or non-finite result → error
  `math_error`.
- Summary: `calculate (42 chars)`.

## 6. Ruled out

Not to be built as tools that run **inside the agent container**, now or later. The
[external tools spec](sharpclaw-external-tools-spec.md) changes the trust model so that network
access, `git clone`, running code and GitHub calls are possible, but only through the gateway,
in a per-agent sandbox or in the gateway itself. Nothing below may ever run in the agent:

- shell or command execution, running scripts, code, tests or build tools;
- `git` operations (hooks and config can execute code), including cloning repositories;
- the `gh` CLI or any other external-service client inside the agent;
- Docker or container management, the Docker socket;
- reading or changing agent policy (`agent.json`, `soul.md`) from the workspace;
- direct network access from the agent container (any network capability goes through the
  gateway, specified elsewhere).

## 7. Agent configuration and rollout

- New tool names are added to `WorkspaceTools.Names` (or a new `UtilityTools.Names`) so
  `AgentToolConfig.Load` accepts them; the registry is built from the union.
- Nothing is enabled by default. Each agent's `docker/agents/<name>/config/agent.json` lists the
  tools it gets; updating the shipped `ade` and `cody` configs is part of task 7.

## 8. Tests

- **Unit, per tool:** schema parses; `Assess` returns the expected `Scored` / `Uncertain` /
  `Blocked` for normal, overwrite, sensitive and every hardline case (absolute, `..`, symlink
  component, caps); `Summarize` contains no content or query text; execute results and error codes.
- **Search / find:** symlinks are not followed; caps produce `truncated: true`; binary and >1 MB
  files are skipped.
- **Edit:** zero / one / many matches; size cap after edit; atomic write (no temp files left).
- **Read paging:** `nextOffset` round-trips through a multi-byte UTF-8 file without corrupting
  characters.
- **`current_time`:** `FakeTimeProvider`; named zone; unknown zone error.
- **`calculate`:** precedence, unary minus, functions, depth limit, division by zero, rejection of
  identifiers and anything outside the grammar.
- **Config:** new names accepted; unknown names still fail startup.
- **Architecture test:** still passes with the new tools (only `ToolDispatcher` calls
  `ExecuteUncheckedAsync`).
- **Dispatcher integration:** taint from `workspace_search` raises later calls in the same turn.

## 9. Task order

| # | Task |
|---|---|
| 1 | This spec; answers to the open questions (section 0.1); roadmap link |
| 2 | `current_time` (+ `UtilityTools` registration) |
| 3 | `workspace_search` and `workspace_find` |
| 4 | `workspace_edit` and `workspace_append` |
| 5 | `workspace_move` and `workspace_copy` |
| 6 | `workspace_read` paging |
| 7 | `calculate`; update `ade` / `cody` configs; README tool table |

## 10. Out of scope

Everything in section 6; any tool needing a gateway endpoint or network access (`http_fetch`,
`download_to_workspace`, web search, GitHub, budget status, memory beyond workspace files,
agent-to-agent, scheduling, MCP); regex search; directory move/delete; per-tool approval memory
("always allow"); policy hot reload.
