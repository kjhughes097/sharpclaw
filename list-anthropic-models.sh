#!/usr/bin/env bash
# List available Anthropic model names using the /v1/models API endpoint.
# Reads API key from appsettings.Development.json or ANTHROPIC_API_KEY env var.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SETTINGS_FILE="$SCRIPT_DIR/SharpClaw/appsettings.Development.json"

# Resolve API key
if [[ -n "${ANTHROPIC_API_KEY:-}" ]]; then
    API_KEY="$ANTHROPIC_API_KEY"
elif [[ -f "$SETTINGS_FILE" ]]; then
    API_KEY=$(python3 -c "import json; print(json.load(open('$SETTINGS_FILE'))['Anthropic']['ApiKey'])")
else
    echo "Error: No ANTHROPIC_API_KEY env var and $SETTINGS_FILE not found" >&2
    exit 1
fi

if [[ -z "$API_KEY" ]]; then
    echo "Error: API key is empty" >&2
    exit 1
fi

# Fetch models and extract names
curl -s https://api.anthropic.com/v1/models \
    -H "x-api-key: $API_KEY" \
    -H "anthropic-version: 2023-06-01" \
  | python3 -c "
import json, sys
data = json.load(sys.stdin)
if 'error' in data:
    print(f\"Error: {data['error']['message']}\", file=sys.stderr)
    sys.exit(1)
models = sorted(m['id'] for m in data.get('data', []))
for m in models:
    print(m)
"
