# Telegram Integration

The `SharpClaw.Telegram` service bridges Telegram and SharpClaw, letting users send messages
to a Telegram bot and receive agent responses directly in the chat.

## Architecture

`SharpClaw.Telegram` is a standalone ASP.NET Core service that:

1. Connects to the Telegram Bot API using **long polling** to receive messages — no public
   URL or webhook registration required.
2. Maps each Telegram chat ID to a SharpClaw session, persisting the mapping across restarts.
3. Forwards incoming text messages to the SharpClaw API and streams back the agent response.
4. Supports `/start` and `/new` commands to begin a fresh session at any time.

The service communicates with the SharpClaw API over HTTP and does not connect to the
PostgreSQL database directly.

## Session mapping

Each Telegram chat has exactly one active SharpClaw session. The mapping is stored in a JSON
file (default: `session-mappings.json` next to the binary) so it survives container restarts.
Sending `/start` or `/new` removes the existing mapping and creates a fresh session.

## Tool permissions

When the assigned agent has tools with an `Ask` permission policy, `SharpClaw.Telegram`
automatically approves them. This keeps the conversation flowing without a separate approval
UI. To restrict tool access, set the relevant policy to `Deny` in the agent configuration.

## Setup

### 1. Create a Telegram bot

1. Talk to [@BotFather](https://t.me/botfather) on Telegram and run `/newbot`.
2. Copy the bot token it gives you.

### 2. Set required environment variables

| Variable | Required | Description |
|---|---|---|
| `TELEGRAM_BOT_TOKEN` | Yes | Bot token from BotFather |
| `SHARPCLAW_API_URL` | Yes | URL of the SharpClaw API (e.g. `http://localhost:8080`) |
| `SHARPCLAW_API_KEY` | Yes | The `SHARPCLAW_API_KEY` used by the main API |
| `SHARPCLAW_DEFAULT_AGENT_ID` | No | Slug of the agent to assign to new sessions. Defaults to the first enabled persona returned by the API. |
| `TELEGRAM_MAPPING_STORE_PATH` | No | Path to the JSON file that persists chat-to-session mappings. Defaults to `session-mappings.json` beside the binary. |

### 3. Run with Docker Compose

The Telegram service is included in `docker-compose.yml` under the `telegram` profile so it
does not start unless explicitly enabled:

```bash
# Copy and fill in the Telegram-specific variables
cp .env.example .env
# edit TELEGRAM_BOT_TOKEN, SHARPCLAW_DEFAULT_AGENT_ID

docker compose --profile telegram up -d
```

### 4. Run standalone (development)

```bash
export TELEGRAM_BOT_TOKEN=<token>
export SHARPCLAW_API_URL=http://localhost:8080
export SHARPCLAW_API_KEY=<your-api-key>

dotnet run --project SharpClaw.Telegram
```

The service automatically polls the Telegram API for new messages — no webhook
registration, public URL, or TLS certificate is needed.

## Health check

```
GET /health
```

Returns `{"status":"ok"}` when the service is running.

## Supported commands

| Command | Description |
|---|---|
| `/start` | Creates a new SharpClaw session for this chat |
| `/new` | Alias for `/start` — same behaviour |
| _(any text)_ | Forwarded as a user message to the current session |
