# Telegram Integration

The `SharpClaw.Telegram` service bridges a Telegram bot and SharpClaw, letting users send
messages to a Telegram bot and receive agent responses directly in the chat.

## Architecture

`SharpClaw.Telegram` is a .NET Worker Service that uses the Telegram.Bot library's built-in
long-polling mechanism. It:

1. Connects to the Telegram Bot API on startup and polls for new messages automatically.
2. Maps each Telegram chat ID to a SharpClaw session, persisting the mapping across restarts.
3. Forwards incoming text messages to the SharpClaw API and streams back the agent response.
4. Supports `/start` and `/new` commands to begin a fresh session at any time.

No webhook URL or public HTTPS endpoint is required — the service reaches out to Telegram
rather than waiting for Telegram to push updates inbound.

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

### 2. Configure Telegram settings in SharpClaw

Configure Telegram in the web UI under `Configure > Telegram`:

- Enabled state
- Bot token from BotFather
- Optional allowlists (user IDs and usernames)
- Generate a long-lived worker API token (used for `SHARPCLAW_API_TOKEN`)

These settings are persisted in PostgreSQL and exposed to the Telegram worker at runtime via the API.

### 3. Set required worker environment variables

| Variable | Required | Description |
|---|---|---|
| `SHARPCLAW_API_URL` | Yes | URL of the SharpClaw API (e.g. `http://localhost:8080`) |
| `SHARPCLAW_API_TOKEN` | Yes | Bearer token used by the Telegram worker to call SharpClaw API |

Generate this token from `Configure > Telegram` using **Generate Worker API Token**, then copy it into `SHARPCLAW_API_TOKEN`.

**Note:** The mapping store path is now database-backed and configurable via the web UI under `Configure > Telegram`. It defaults to a sensible platform-specific location (`%LOCALAPPDATA%/sharpclaw` on Windows, user profile home on other systems).

### 4. Run with Docker Compose

The Telegram service is defined in `docker-compose.yml` and can run once API connectivity is configured:

```bash
cp .env.example .env
# set SHARPCLAW_API_TOKEN for the Telegram worker

docker compose up -d telegram
```

### 5. Run standalone (development)

```bash
export SHARPCLAW_API_URL=http://localhost:8080
export SHARPCLAW_API_TOKEN=<jwt-bearer-token>

dotnet run --project SharpClaw.Telegram
```

The service connects to the Telegram Bot API directly. No inbound port or public URL is
required during development.

## Supported commands

| Command | Description |
|---|---|
| `/start` | Creates a new SharpClaw session for this chat |
| `/new` | Alias for `/start` — same behaviour |
| _(any text)_ | Forwarded as a user message to the current session |
