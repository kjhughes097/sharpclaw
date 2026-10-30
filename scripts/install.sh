#!/usr/bin/env bash
# Creates (or tops up) a SharpClaw install from docker/template/.
#   scripts/install.sh [dir]   (default: ~/sharpclaw)
# Never overwrites an existing file; lists the ones it skipped so you can merge by hand.
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
TEMPLATE="$REPO/docker/template"
DEST="${1:-$HOME/sharpclaw}"

mkdir -p "$DEST"
DEST="$(cd "$DEST" && pwd)"

SKIPPED=()
while IFS= read -r -d '' FILE; do
  REL="${FILE#"$TEMPLATE"/}"
  # An existing install decides which agents it has: don't bring back a sample agent its
  # compose.yml no longer defines (e.g. one the quick start left out).
  if [[ "$REL" == agents/* ]] && [ -f "$DEST/compose.yml" ]; then
    AGENT="${REL#agents/}"; AGENT="${AGENT%%/*}"
    grep -q "^  agent-$AGENT:" "$DEST/compose.yml" || continue
  fi
  if [ -e "$DEST/$REL" ]; then
    cmp -s "$FILE" "$DEST/$REL" || SKIPPED+=("$REL")
  else
    mkdir -p "$(dirname "$DEST/$REL")"
    cp "$FILE" "$DEST/$REL"
    echo "created $REL"
  fi
done < <(find "$TEMPLATE" -type f -print0)

if [ ! -e "$DEST/.env" ]; then
  # Keep secrets private from the moment the file exists.
  (umask 077 && cp "$TEMPLATE/.env.example" "$DEST/.env")
  sed -i \
    -e "s/^ADMIN_KEY=$/ADMIN_KEY=$(openssl rand -hex 32)/" \
    -e "s/^SANDBOX_CODER_SECRET=$/SANDBOX_CODER_SECRET=$(openssl rand -hex 32)/" \
    -e "s/^SANDBOX_CODER_PROXY_SECRET=$/SANDBOX_CODER_PROXY_SECRET=$(openssl rand -hex 32)/" \
    -e "s/^SHARPCLAW_UID=$/SHARPCLAW_UID=$(id -u)/" \
    -e "s/^SHARPCLAW_GID=$/SHARPCLAW_GID=$(id -g)/" \
    "$DEST/.env"
  echo "created .env (ADMIN_KEY and sandbox secrets generated)"
fi

mkdir -p "$DEST/data"
if [ ! -w "$DEST/data" ] || [ ! -x "$DEST/data" ]; then
  echo "WARNING: $DEST/data is not writable by $(id -un); the gateway runs as this user and will fail to open its database." >&2
fi
# The shared skill library every agent mounts read-only (it must exist for the bind mount).
mkdir -p "$DEST/skills"
for AGENT in "$DEST"/agents/*/; do
  mkdir -p "$AGENT/workspace"
done

if [ ${#SKIPPED[@]} -gt 0 ]; then
  echo
  echo "Kept your version of these files (they differ from the template):"
  for REL in "${SKIPPED[@]}"; do
    echo "  diff -u '$DEST/$REL' '$TEMPLATE/$REL'"
  done
fi

echo
echo "SharpClaw install ready in $DEST — see $DEST/README.md"
