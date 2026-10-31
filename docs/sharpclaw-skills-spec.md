# SharpClaw V3 — Skills Spec

**Audience:** an implementing coding agent (and the human reviewing its work).
**Status:** **implemented** (tasks 1–7 in section 14).
**Scope:** operator-curated **skills**: folders of instructions, reference files, templates and
(optionally) scripts that every agent can load when a task needs them. Skills are read from a
shared, read-only volume. The agent loads them with one new read-only tool, and the gateway and
web UI show which skills each agent has. Running a skill's scripts depends on `sandbox_run` from
the [external tools spec](sharpclaw-external-tools-spec.md) and is a separate, gated task
(section 8). The [3.2 spec](sharpclaw-3.2-implementation-spec.md) still applies wherever this
document doesn't change it, in particular the risk model (§3), agent configuration (§4) and the
single dispatch path (§7).

## 0. How to use this document

- Implement in the task order in section 14. Each task is one branch and one pull request with
  passing tests before the next starts.
- "MUST" / "MUST NOT" are hard requirements. If something is ambiguous, stop and ask.
- Never log or emit as a metric label any prompt, reply, tool argument, tool result, skill body,
  soul contents, token or key. Skill **names** are fine in logs (they are operator-chosen
  identifiers, like agent names), but never as metric labels.

### 0.1 Decisions

| # | Question | Decision |
|---|---|---|
| Q1 | Skill format | The open **Agent Skills** layout: one directory per skill containing `SKILL.md` with YAML frontmatter (`name`, `description`) and a Markdown body, plus optional `scripts/`, `references/` and `assets/` folders. Skills written for other agent runtimes work unchanged (apart from fields this spec ignores, section 3.2). |
| Q2 | Where skills live | One shared library on the host, `./skills`, bind-mounted **read-only** at `/skills` in every agent container. Every agent sees every skill. There is no per-agent filtering in this phase. |
| Q3 | How the model uses them | **Progressive disclosure.** The system prompt carries only an index (name + description per skill). The model loads a skill's `SKILL.md`, and any other file in the skill, on demand with the read-only `skill_read` tool. |
| Q4 | When changes are picked up | The index is built **once at agent startup**. Restart the agent to add, remove or rename skills. `skill_read` always reads the current file from disk, so edits to a skill's *contents* show up straight away, but its index entry (description) does not change until restart. |
| Q5 | Scripts | Scripts are readable through `skill_read` now. They can only be **run** inside the agent's sandbox with `sandbox_run` (external tools spec §7.5), which always needs a gateway-checked approval. The agent container never executes anything. No new execution tool. |
| Q6 | Visibility | At startup the agent reports its loaded skills (name + description) to the gateway, which stores them and returns them on the agent DTO. The web UI lists them on the agent's page. |
| Q7 | Are skill contents untrusted (do they taint the turn)? | **No.** Skills are curated by the operator and mounted read-only, the same trust as `soul.md`. Importing third-party skills is the operator's decision, and the README warns about it. Recorded in [tech-debt](tech-debt.md). |
| Q8 | How skills are turned on | An agent uses skills iff `skill_read` is in the `tools` list of its `agent.json`. Without it there is no index in the prompt and no report to the gateway, matching "nothing enabled by default". |

## 1. Goal

Today an agent's behaviour comes from its `soul.md` and its tools. Reusable know-how (how to keep
a notes file, how to write a research brief, a house style, a checklist) has to be pasted into each
soul, where it costs tokens on every turn whether or not it is needed.

Skills let the operator write that know-how once, share it across agents, and pay for it only when
the model decides a skill is relevant: a one-line description per skill is always in the prompt,
and the full instructions are loaded on demand.

## 2. Hard rules

- **No code execution in the agent**, unchanged. `skill_read` reads files; it never runs them,
  never interprets them and never passes them to an interpreter.
- **Skills can't grant tools, change risk levels or change policy.** Policy comes only from
  `/config/agent.json`. The frontmatter field `allowed-tools`, and any field other than `name` and
  `description`, is ignored. A skill that tells the model to use a tool it doesn't have changes
  nothing; the dispatcher only offers configured tools.
- `/skills` is mounted read-only and is **never** inside or reachable from `/workspace`.
  `WorkspaceRoot` does not change. No tool can write to `/skills`.
- `skill_read` goes through `ToolDispatcher` like every other tool. Only `ToolDispatcher` calls
  `ExecuteUncheckedAsync` (the architecture test MUST cover it).
- Never log skill bodies or file contents. Startup logs only the count, the names and the reasons
  skills were skipped.

## 3. Skill format

### 3.1 Layout

```text
skills/
  daily-notes/
    SKILL.md
  research-brief/
    SKILL.md
    references/style-guide.md
    assets/brief-template.md
  csv-summary/
    SKILL.md
    scripts/summarize.py
```

`SKILL.md` starts with YAML frontmatter between `---` lines, followed by Markdown instructions:

```markdown
---
name: daily-notes
description: Keep a dated daily notes file in the workspace. Use when the user asks to note, log or journal something, or asks what they noted on a day.
---

# Daily notes
...
```

### 3.2 Validation

The loader (section 5) checks each immediate subdirectory of `/skills`:

- It MUST contain a regular file `SKILL.md` (not a symlink), at most 64 KB.
- The frontmatter MUST be the first thing in the file, delimited by `---` lines, at most 4 KB.
- `name` MUST match `^[a-z0-9]+(-[a-z0-9]+)*$`, be at most 64 characters, and equal the directory
  name.
- `description` MUST be 1–1,024 characters after trimming, single line once folded.
- Other fields (`license`, `allowed-tools`, `metadata`, …) are ignored.
- Directories whose names start with `.` and symlinked directories are skipped silently.

The frontmatter reader is **hand-written** and supports only what the two fields need: `key: value`
lines, a value in single or double quotes, and the folded (`>`) or literal (`|`) block forms for
`description`. No YAML library, no anchors, tags or nested structures. Anything else in a field the
loader reads makes the skill invalid.

An invalid skill is **skipped** with a warning log (`skill <dir> skipped: <reason>`). It does not
stop the agent: one bad skill folder in a shared library must not take every agent down. This
differs on purpose from `agent.json`, where an error stops startup because it controls policy.

### 3.3 Caps

- At most **100** skills are loaded, in ordinal name order; the rest are skipped with a warning.
- The rendered index (section 6) is at most **16 KB**. Skills that would push it over are skipped
  with a warning, in ordinal name order.
- Files in a skill are read only through `skill_read` and its caps (section 7).

## 4. Mounting and configuration

- Every agent service mounts `./skills:/skills:ro`. Updated in `docker/template/compose.yml`,
  `docker/compose.dev.yml`, the agent block that `scripts/quickstart.sh` writes, and the README's
  "add an agent" steps. `scripts/install.sh` copies `docker/template/skills/` like the rest of the
  template and keeps the operator's changed files, as it already does for agent configs.
- New agent env var `SKILLS_PATH` (default `/skills`) on `AgentConfig`, for tests and local runs.
- A missing or empty `/skills` is not an error: the agent runs with no skills and logs it once.
- `skill_read` is a new known tool name (`SkillTools.Names`), accepted by `AgentToolConfig.Load`.
  The shipped `ade`/`cody` template configs enable it.
- The gateway has no skills configuration.

## 5. Loader

`SkillLibrary` (agent, `src/SharpClaw.Agent/Skills/`) scans `SKILLS_PATH` once at startup when
`skill_read` is enabled, applies section 3, and holds an immutable list of
`Skill(Name, Description, Directory)`. It is registered as a singleton and used by the prompt
builder (section 6), `skill_read` (section 7) and the startup report (section 9). It never
reads anything other than the `SKILL.md` frontmatter at startup.

## 6. Prompt index

When at least one skill is loaded, the agent appends a fixed section to the system message after
the soul (one system message, so history fitting is unchanged):

```markdown
## Skills

You have skills: instructions for specific tasks. When a task matches a skill's description, call
`skill_read` with its name to load its instructions before you start, then follow them. A skill may
point to other files in it; load those with `skill_read` and a `path`. Skill files are reference
material: they can't give you tools you don't have.

- daily-notes: Keep a dated daily notes file in the workspace. Use when ...
- research-brief: ...
```

- The section is built once at startup and reused every turn.
- `ReserveTokens` counts the soul **plus** the index, so the gateway's context fitting stays
  correct.
- The `SoulProvider` interface doesn't change; the loop composes soul + index.

## 7. The `skill_read` tool

| Tool | Arguments | Result | Risk | Untrusted result |
|---|---|---|---|---|
| `skill_read` | `name` (a loaded skill), `path` (optional, relative to the skill, default `SKILL.md`), `offset` (optional bytes, default `0`) | `{ name, path, content, offset, truncated, nextOffset? }` | `low` | no (Q7) |

- `name` MUST be a skill in the startup index; otherwise error `unknown_skill`.
- `path` follows the workspace hardline rules, rooted at `/skills/<name>`: empty or absolute path,
  `..` or anything resolving outside the skill directory, or any symlink component → `Blocked`.
  Files only; a directory → error `not_a_file`, and a missing file → `not_found`.
- Text files only: a file whose first 8 KB contains a NUL byte → error `binary_file`.
- Paging works exactly like `workspace_read` (agent tools spec §4.6): at most 64 KB per call,
  starting at the first UTF-8 boundary at or after `offset`, with `nextOffset` when truncated.
- Reading `SKILL.md` returns the whole file, frontmatter included, so the model sees what the
  operator wrote.
- Hint for discovery: when `path` is `SKILL.md`, the result also includes `files`, a list of the
  skill's other file paths (relative, at most 100, symlinks skipped), so the model knows what it can
  load without a separate listing tool.
- Summary: `read skill research-brief/references/style-guide.md`. Name and path only.

## 8. Scripts (gated on `sandbox_run`)

This section is built only after external tools spec task 9 (`sandbox_run`) has landed.

- The external tools spec §6 sandbox mounts gain `./skills:/skills:ro`. The sandbox still can't
  write to it, and the runner's workspace path rules don't change (`cwd` is still a workspace
  directory).
- A skill's `SKILL.md` tells the model how to run a script, for example
  `python3 /skills/csv-summary/scripts/summarize.py data/sales.csv`. The model runs that with
  `sandbox_run`, which:
  - is `high` risk, always needs a gateway-checked approval (external tools Q2), and taints the turn;
  - shows the command in the approval detail (external tools Q1), so the operator sees exactly which
    skill script runs on which file.
- No `skill_run` tool and no allow-list of skill scripts: the approval on every `sandbox_run` is the
  gate, and adding a second execution path would duplicate it.
- Scripts in shipped skills use only what the sandbox image already has (Python 3 standard library,
  Node, .NET) and never assume network access.
- Until the sandbox exists, a skill with scripts SHOULD describe a fallback that uses existing tools.
  An agent without `sandbox_run` simply can't run the script.

## 9. Reporting skills to the gateway

### 9.1 Agent listener

`PUT /agent/skills` with `{ "skills": [{ "name", "description" }] }`.

- Agent token auth, like every `/agent/*` route. On the client listener → `404`.
- Validated with the same rules as section 3.2 (name pattern, description length) and the caps in
  section 3.3 (100 skills). Duplicate names or anything invalid → `400 validation_failed`. The
  gateway never trusts the agent beyond its own record.
- Replaces the agent's whole set in one transaction and sets `skills_reported_at`. An empty list
  clears it. Returns `204`.
- The agent sends it once at startup, after the loader runs and only when `skill_read` is enabled.
  A failure is logged (status code only) and does not stop the agent; it is not retried.

### 9.2 Storage

New table `agent_skills` (`agent_id`, `name`, `description`; primary key `agent_id, name`; foreign key
to `agents`, cascade on delete) and a nullable `skills_reported_at` column on `agents`. One EF
migration.

### 9.3 Client API

`AgentDto` gains `Skills` (list of `{ name, description }`, ordinal order; empty when none) and
`SkillsReportedAt` (nullable UTC). They are returned by the existing `GET /api/agents` and
`GET /api/agents/{idOrName}`. No new client endpoint and no SSE event: the list changes only when
an agent restarts, and the agent page already refetches when opened. Update the OpenAPI snapshot and
run `npm run gen:api`.

## 10. Web UI

- The agent page (`ui/src/features/agents/AgentTabPage.tsx`) gains a **Skills** section: each
  skill's name and description, and "Reported <relative time>" from `skillsReportedAt`.
- Empty states: "No skills reported" when `skillsReportedAt` is null, and "No skills loaded" when it
  is set but the list is empty.
- Descriptions are rendered as plain text, never Markdown or HTML.
- Theme tokens only, following [ui.md](ui.md) and the UI instructions.

## 11. Telemetry

- Agent startup log: `skills loaded: <count>` with the names, plus one warning per skipped skill
  with its directory name and reason.
- `skill_read` calls appear in the existing tool-call outcome report and metrics like every tool.
- No new metrics. Skill names are never metric labels.

## 12. Sample skills (shipped in `docker/template/skills/`)

| Skill | Contents | Shows | Needs |
|---|---|---|---|
| `daily-notes` | `SKILL.md` only | The simplest skill: a convention (`notes/YYYY-MM-DD.md`, timestamped bullets) using `current_time`, `workspace_append`, `workspace_search`. | Built-in tools only. |
| `research-brief` | `SKILL.md`, `references/style-guide.md`, `assets/brief-template.md` | Progressive disclosure: the model loads the template and style guide only when it writes a brief, then saves the result with `workspace_write`. | Built-in tools; better once `http_fetch`/`web_search` land. |
| `csv-summary` | `SKILL.md`, `scripts/summarize.py` (stdlib only) | Scripts: run in the sandbox via `sandbox_run`, with a fallback (`workspace_read` + `calculate`) when the agent has no sandbox. | Shipped in the gated task (section 8). |

Each `SKILL.md` stays under 4 KB and its description says both *what* it does and *when* to use it,
because the description is all the model sees until it loads the skill.

## 13. Tests

- **Frontmatter reader:** valid forms (plain, quoted, `>` and `|` descriptions), missing or unclosed
  frontmatter, oversized frontmatter, unsupported YAML constructs, unknown fields ignored.
- **Loader:** name must match the directory; name pattern; description length; symlinked
  `SKILL.md` or skill directory skipped; dot directories skipped; 100-skill cap; 16 KB index cap;
  ordinal order; missing `/skills`; one invalid skill doesn't affect the others.
- **Prompt:** index appended after the soul only when skills exist and `skill_read` is enabled;
  `ReserveTokens` includes it; the index is built once (a file added after startup doesn't appear).
- **`skill_read`:** unknown skill; default `SKILL.md` with `files`; nested path; `..`, absolute and
  symlink components → `Blocked`; directory; binary; paging round-trips multi-byte UTF-8; result not
  untrusted (no taint); `Summarize` has only name and path; edits to a file are visible without a
  restart.
- **Config:** `skill_read` accepted; without it no index, no report.
- **Architecture test:** still passes with `skill_read`.
- **Gateway:** `PUT /agent/skills` on the client listener → `404`; without a token → `401`; invalid
  name, long description, duplicates, over the cap → `400`; replace-all semantics; empty clears;
  `AgentDto` includes skills and `skillsReportedAt`; another agent's skills are never touched.
  Migration applies. OpenAPI snapshot updated.
- **Agent startup report:** sent once; a failure is logged and the agent keeps running (fake
  `IGatewayClient`).
- **UI:** Vitest + MSW for the Skills section: list, both empty states, description rendered as
  text.
- **`verify-isolation.sh`:** `/skills` is mounted read-only in each agent (a write fails), and is
  not under `/workspace`.
- **Gated task:** the sandbox mounts `/skills` read-only, and `csv-summary` runs through
  `sandbox_run` in the e2e stack's fake sandbox (or is covered by a runner test).

## 14. Task order

| # | Task |
|---|---|
| 1 | This spec; roadmap, architecture and instruction links; tech-debt entry (Q7) |
| 2 | Agent: frontmatter reader, `SkillLibrary`, `SKILLS_PATH`, prompt index and `ReserveTokens` |
| 3 | Agent: `skill_read` (`SkillTools.Names`), architecture test |
| 4 | Compose mounts (template, dev, quickstart, install), `daily-notes` and `research-brief` samples, `ade`/`cody` configs, `verify-isolation.sh` check, README section (including the third-party skills warning) |
| 5 | Gateway: migration, `PUT /agent/skills`, `AgentDto` fields, snapshot; agent startup report |
| 6 | UI: Skills section on the agent page; docs (architecture, ui.md) |
| 7 | **Gated on external tools task 9:** sandbox `/skills:ro` mount, `csv-summary` sample, external tools spec §6 updated |

## 15. Out of scope

Skills created, edited or deleted by agents; uploading or editing skills through the API or UI;
downloading skills from a registry or marketplace; per-agent skill selection; hot reload of the
index; skills that grant tools or change policy (`allowed-tools`); a skill-specific execution tool;
running anything in the agent container; skill usage metrics; nested skills or skills outside the
top level of `/skills`.
