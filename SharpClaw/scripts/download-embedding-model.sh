#!/usr/bin/env bash
# Downloads the all-MiniLM-L6-v2 ONNX model for semantic memory embeddings.
# Run from the repo root: ./SharpClaw/scripts/download-embedding-model.sh

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"
MODEL_DIR="$PROJECT_DIR/models"

MODEL_URL="https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx"
VOCAB_URL="https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/vocab.txt"

echo "==> Downloading all-MiniLM-L6-v2 ONNX model..."
echo "    Target: $MODEL_DIR"

mkdir -p "$MODEL_DIR"

if [ -f "$MODEL_DIR/all-MiniLM-L6-v2.onnx" ]; then
    echo "    Model already exists, skipping download."
else
    echo "    Downloading model (~90MB)..."
    curl -L --progress-bar -o "$MODEL_DIR/all-MiniLM-L6-v2.onnx" "$MODEL_URL"
    echo "    ✓ Model downloaded."
fi

if [ -f "$MODEL_DIR/vocab.txt" ]; then
    echo "    Vocab already exists, skipping download."
else
    echo "    Downloading WordPiece vocabulary..."
    curl -L --progress-bar -o "$MODEL_DIR/vocab.txt" "$VOCAB_URL"
    echo "    ✓ Vocab downloaded."
fi

echo ""
echo "==> Done! Model files:"
ls -lh "$MODEL_DIR"/all-MiniLM-L6-v2.onnx "$MODEL_DIR"/vocab.txt
echo ""
echo "Enable semantic memory in appsettings.json:"
echo '  "SemanticMemory": { "Enabled": true }'
