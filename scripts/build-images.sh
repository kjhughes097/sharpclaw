#!/usr/bin/env bash
# Builds the gateway and agent images from this checkout.
#   scripts/build-images.sh            -> tags :<short-sha>[-dirty] and :latest
#   scripts/build-images.sh --tag dev  -> tags :dev only
# An install's compose.yml runs sharpclaw-*:${SHARPCLAW_TAG:-latest}.
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
SHA="$(git -C "$REPO" rev-parse --short HEAD)"
if [ -n "$(git -C "$REPO" status --porcelain)" ]; then
  SHA="$SHA-dirty"
fi

TAGS=("$SHA" latest)
if [ "${1:-}" = "--tag" ]; then
  [ -n "${2:-}" ] || { echo "usage: $0 [--tag <tag>]" >&2; exit 2; }
  TAGS=("$2")
elif [ -n "${1:-}" ]; then
  echo "usage: $0 [--tag <tag>]" >&2
  exit 2
fi

for IMAGE in gateway agent; do
  ARGS=()
  for TAG in "${TAGS[@]}"; do ARGS+=(-t "sharpclaw-$IMAGE:$TAG"); done
  docker build -f "$REPO/docker/Dockerfile.$IMAGE" --build-arg "GIT_SHA=$SHA" "${ARGS[@]}" "$REPO"
done

echo "Built sharpclaw-gateway and sharpclaw-agent: ${TAGS[*]}"
