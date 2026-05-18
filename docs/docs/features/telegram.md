---
sidebar_position: 8
---

# Telegram

SharpClaw integrates with Telegram as its primary chat interface via the `Telegram.Bot` library.

## TelegramService

A `BackgroundService` that receives messages via long polling:

```csharp
public sealed class TelegramService(
    ITelegramBotClient botClient,
    AgentSessionRegistry sessionRegistry,
    AgentInvoker invoker,
    TelegramAgentRouter router,
    IOptions<TelegramOptions> telegramOptions,
    ILogger<TelegramService> logger) : BackgroundService
```

### Message Flow

1. Receive `Update` with `Message.Text`
2. If the message includes a document or photo caption, download the file into `{WorkspacePath}/{agent-name}/uploads/`
3. For small text-based uploads like CSV/TSV/JSON, inline the file contents into the prompt alongside the saved file path
4. For spreadsheet uploads like `.xlsx`, generate a compact workbook summary with sheet names, columns, and sample rows
5. Validate sender username against `AllowedUsers` whitelist
6. Resolve agent via `TelegramAgentRouter` (per-chat mapping)
7. Get or create `AgentSession` keyed by chat ID
8. Publish inbound message to session bus
9. Start typing indicator loop
10. Call `AgentInvoker.InvokeAsync()`
11. Send response back via `botClient.SendMessage()`

### Security

Only usernames listed in `Telegram:AllowedUsers` configuration are processed. All other messages are silently dropped.

### Typing Indicator

While the agent is processing, a typing indicator is sent every 4 seconds to show the bot is active.

## TelegramAgentRouter

Maps Telegram chat IDs to agent names. Allows different chats to talk to different agents:

```csharp
public void Map(long chatId, string agentName);
public string? Resolve(long chatId);
```

Updated when a `.switch` command is used.

### Agent Resolution Order

For each incoming message, the agent is resolved in this order:

1. **Explicit mapping** — if the user previously switched agents with `.{letter}`, that mapping (stored in `TelegramAgentRouter`) takes priority.
2. **Group title match** — for group and supergroup chats, if the group title exactly matches a registered agent name (case-insensitive), that agent is used automatically. For example, a group named "myles" will use the `myles` agent without any manual switch.
3. **DefaultAgent** — the `Telegram:DefaultAgent` value from configuration.

This means you can create a Telegram group per agent and messages will automatically route to the correct one. The `.{letter}` command still works to override the group-name default within a session.

### File Uploads

Uploaded documents are saved under `{WorkspacePath}/{agent-name}/uploads/`. When the file looks like text and is small enough to safely inspect, SharpClaw also inlines the file contents into the prompt so the agent can reason over CSVs and similar files without needing a separate file-reading tool.

CSV and TSV uploads also get a compact summary block with column names and a few sample rows. `.xlsx` uploads get a workbook summary with worksheet names plus a preview of the first sheet, which makes spreadsheet-style uploads usable even though the binary file itself is not inlined.

## Configuration

```json
{
  "Telegram": {
    "BotToken": "123456:ABC-DEF...",
    "AllowedUsers": ["alice", "bob"],
    "DefaultAgent": "ade"
  }
}
```

## Known Issue

`Telegram.Bot` exports a `MessageOrigin` type that clashes with `SharpClaw.Models.MessageOrigin`. Resolved with a using alias:

```csharp
using ChannelMessageOrigin = SharpClaw.Models.MessageOrigin;
```
