# Telegram Integration

The `SharpClaw.Telegram` service bridges Telegram and SharpClaw, letting users send messages
to a Telegram bot and receive agent responses directly in the chat.

## Architecture

`SharpClaw.Telegram` is a standalone ASP.NET Core service that:

1. Exposes a webhook endpoint (`POST /telegram/webhook`) for Telegram to push updates to.
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
| `TELEGRAM_WEBHOOK_SECRET` | Recommended | Secret string added to the Telegram webhook — Telegram sends it as `X-Telegram-Bot-Api-Secret-Token` |
| `SHARPCLAW_DEFAULT_AGENT_ID` | No | Slug of the agent to assign to new sessions. Defaults to the first enabled persona returned by the API. |
| `TELEGRAM_MAPPING_STORE_PATH` | No | Path to the JSON file that persists chat-to-session mappings. Defaults to `session-mappings.json` beside the binary. |

### 3. Register the webhook with Telegram

Telegram requires the webhook URL to be publicly reachable over HTTPS. After the service is
running (e.g. behind an nginx reverse-proxy with a TLS certificate), register the webhook once:

```bash
curl -X POST "https://api.telegram.org/bot<YOUR_BOT_TOKEN>/setWebhook" \
  -H "Content-Type: application/json" \
  -d '{
    "url": "https://your-domain.example.com/telegram/webhook",
    "secret_token": "<YOUR_WEBHOOK_SECRET>"
  }'
```

Telegram supports ports 80, 88, 443, and 8443 for webhooks. Self-signed certificates are
allowed but require the public key to be uploaded via the `certificate` field (see the
[Telegram webhook guide](https://core.telegram.org/bots/webhooks)).

### 4. Run with Docker Compose

The Telegram service is included in `docker-compose.yml` under the `telegram` profile so it
does not start unless explicitly enabled:

```bash
# Copy and fill in the Telegram-specific variables
cp .env.example .env
# edit TELEGRAM_BOT_TOKEN, TELEGRAM_WEBHOOK_SECRET, SHARPCLAW_DEFAULT_AGENT_ID

docker compose --profile telegram up -d
```

### 5. Run standalone (development)

```bash
export TELEGRAM_BOT_TOKEN=<token>
export TELEGRAM_WEBHOOK_SECRET=<secret>
export SHARPCLAW_API_URL=http://localhost:8080
export SHARPCLAW_API_KEY=<your-api-key>

dotnet run --project SharpClaw.Telegram
```

The service listens on port `8443` by default. Use a tool like
[ngrok](https://ngrok.com/) to expose it during local development:

```bash
ngrok http 8443
# Then register the resulting https URL as the webhook
```

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
