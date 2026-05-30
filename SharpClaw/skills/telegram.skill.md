---
description: Guidelines for sending Telegram messages via the send_telegram tool
---

## Telegram Messaging

You have access to the `send_telegram` tool for sending messages to Telegram chats.

### Usage

- **`chat_id`** — The Telegram chat ID to send to. If you have a configured chat ID (shown in your system prompt), always use that. When running from a scheduled task, this defaults to the originating chat and can be omitted.
- **`message`** — The message text. Supports Telegram markdown formatting.

### Rules

- Only pre-approved chat IDs are permitted. If you don't have a configured chat ID and the user hasn't provided one, tell them you need a chat ID.
- Keep messages concise and well-formatted. Use markdown for emphasis and structure.
- For scheduled tasks that deliver results, format the output as a clear summary — don't dump raw data.
- If a send fails due to an unapproved chat ID, inform the user that the ID needs to be added to the `Telegram:AllowedChatIds` configuration.
