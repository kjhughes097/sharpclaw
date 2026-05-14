---
sidebar_position: 4
---

# Skills

Skills are reusable prompt fragments that get injected into an agent's system prompt at runtime. They allow sharing instructions across multiple agents without duplicating text.

## File Format

Skills live in `src/SharpClaw/skills/` as `{name}.skill.md` files:

```markdown
---
description: Coding standards for .NET projects
---

## Coding Standards

- Use primary constructors for DI
- Seal all concrete classes
- Use records for immutable data
- Nullable enabled, no ! suppression
```

The filename (without `.skill.md`) becomes the skill name.

## Loading

`SkillLoader` scans the skills directory at startup and registers entries in `ISkillRegistry`.

## Agent Binding

Agents reference skills by name:

```yaml
skills: [coding-standards, memory-format]
```

## Prompt Injection

When `AgentInvoker` builds the system prompt for an agent, referenced skill content is appended to the base system prompt. This happens before the prompt is sent to the Copilot SDK.

## Adding a New Skill

1. Create `src/SharpClaw/skills/{name}.skill.md`
2. Optionally add YAML frontmatter with a `description`
3. Write the prompt content as the markdown body
4. Reference the skill name in agent frontmatter
5. Restart (or `.reload`)
