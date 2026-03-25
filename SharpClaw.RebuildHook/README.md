# SharpClaw.RebuildHook

A minimal .NET 10 Web API that acts as a local webhook listener for triggering Docker Compose per-service rebuilds.

## Build & Run

```bash
cd SharpClaw.RebuildHook
dotnet build
dotnet run
```

The service binds to `http://127.0.0.1:9876`.

## Install as a systemd Service

1. Publish the application:

```bash
dotnet publish -c Release -o /opt/sharpclaw-rebuild-hook
```

2. Create a systemd unit file at `/etc/systemd/system/sharpclaw-rebuild-hook.service`:

```ini
[Unit]
Description=SharpClaw Rebuild Hook
After=network.target

[Service]
Type=simple
WorkingDirectory=/opt/sharpclaw-rebuild-hook
ExecStart=/opt/sharpclaw-rebuild-hook/SharpClaw.RebuildHook
Restart=on-failure
RestartSec=5
User=sharpclaw

[Install]
WantedBy=multi-user.target
```

3. Enable and start the service:

```bash
sudo systemctl daemon-reload
sudo systemctl enable sharpclaw-rebuild-hook
sudo systemctl start sharpclaw-rebuild-hook
```

## Configuration

Edit `appsettings.json` to set your secret, working directory, and allowed services:

```json
{
  "WebhookSettings": {
    "Secret": "your-secret-here",
    "ComposeDirectory": "/opt/sharpclaw",
    "AllowedServices": ["service-a", "service-b"]
  }
}
```

## Endpoints

### `GET /health`

Returns `200 OK` with `{ "status": "ok" }`. No authentication required.

### `POST /rebuild`

Triggers a `docker compose build` + `docker compose up -d` for the specified service.

**Headers:**

| Header | Description |
|---|---|
| `X-Webhook-Secret` | Must match `WebhookSettings:Secret` in config |
| `Content-Type` | `application/json` |

**Request body:**

```json
{
  "service": "service-a",
  "message": "Deploy triggered by push to main"
}
```

**Responses:**

| Status | Meaning |
|---|---|
| `202 Accepted` | Rebuild started in the background |
| `400 Bad Request` | Service not in allowed list |
| `401 Unauthorized` | Missing or wrong `X-Webhook-Secret` |

**Example curl command:**

```bash
curl -X POST http://127.0.0.1:9876/rebuild \
  -H "Content-Type: application/json" \
  -H "X-Webhook-Secret: changeme" \
  -d '{"service": "service-a", "message": "manual rebuild"}'
```
