#!/usr/bin/env bash
# Download a Whisper.cpp ggml model from HuggingFace.
# Usage: ./scripts/download-whisper-model.sh [size]
#   size: tiny | base | small | medium | large-v3 (default: base)

set -euo pipefail

SIZE="${1:-base}"
DIR="${WHISPER_MODEL_DIR:-SharpClaw/models/whisper}"
URL="https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-${SIZE}.bin"
OUT="${DIR}/ggml-${SIZE}.bin"

mkdir -p "$DIR"

if [[ -f "$OUT" && -s "$OUT" ]]; then
    echo "Model already present: $OUT ($(du -h "$OUT" | cut -f1))"
    exit 0
fi

echo "Downloading $URL → $OUT"
curl -L --fail --progress-bar -o "$OUT.part" "$URL"
mv "$OUT.part" "$OUT"
echo "Done: $OUT ($(du -h "$OUT" | cut -f1))"
