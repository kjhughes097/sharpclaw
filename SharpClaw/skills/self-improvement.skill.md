---
description: Guidelines for proactively creating new tools when capability gaps are identified
---

## Self-Improvement: Creating Tools

When you identify a **capability gap** — something you need to do but lack a tool for — you should proactively create that tool rather than reporting limitations.

### Process

1. **Identify the gap**: Recognize when a user request requires a capability you don't have
2. **Check feasibility**: Ensure the underlying infrastructure exists (e.g., TelegramService for Telegram messages)
3. **Decide**: Simple wrapper? Do it yourself. Complex architecture? Delegate to Cody.
4. **Implement the tool**: Create a new `ITool` implementation in `Tools/` (or ask Cody to)
5. **Register it**: Add DI registration in `Program.cs` and add to tool registry
6. **Document it**: Update your memory and relevant documentation
7. **Verify**: Build and test to ensure it works

### When to Create Tools

✅ **Do create when**:
- User request requires a capability that doesn't exist as a tool
- Underlying service/API exists, just needs a tool wrapper
- The capability would be reusable across multiple use cases
- You have the technical knowledge to implement it correctly

❌ **Don't create when**:
- A one-off bash script would suffice
- Requires significant infrastructure that doesn't exist
- The capability already exists in an MCP server
- Security or architectural concerns make it inappropriate

### When to Delegate to Cody

For **simple tool wrappers** that expose existing services/APIs through `ITool`, implement them yourself:
- Low risk, clear pattern to follow
- Existing service handles the hard work
- Example: `SendTelegramTool` wrapping `TelegramService`

For **architectural changes** (new services, database schema, complex logic), delegate to Cody via `spawn_agent`:
- High risk, requires deep software engineering expertise
- Examples: refactoring `AgentRunner`, adding new MCP transport type, database migrations

**Use judgment**: If you're confident and following established patterns, proceed. If uncertain about design implications or .NET conventions, consult Cody.

### Mindset

**You are not just a user of tools — you are a builder of tools.** Extend your own capabilities when needed. This is what distinguishes an autonomous agent from a reactive chatbot.

### Real Example

**Scenario**: User requests scheduled Telegram messages for budget reports.

**Gap Identified**: `schedule_task` exists, but no tool to send Telegram messages from within scheduled prompts.

**Action Taken**: Created `SendTelegramTool` that wraps the existing `TelegramService`, registered it conditionally (only when Telegram is configured), and added it to the tool registry.

**Result**: Capability gap closed. Future scheduled tasks can now send Telegram messages.

### Architecture Notes

- All tools live in `src/SharpClaw/Tools/`
- Tools must implement `ITool` interface
- Tools are registered in `Program.cs` via DI
- Tools are added to `IToolRegistry` (automatically scanned at startup)
- Tools can have conditional registration (e.g., only when config is present)
- Follow existing patterns: see `SpawnAgentTool`, `ScheduleTaskTool` for examples
