#!/bin/bash
# ═══════════════════════════════════════════════════════════════════════════
# download_models.sh — Download the on-device person-name (NER) model
#
# Model: dslim/bert-base-NER, int8-quantized ONNX export by Xenova (MIT license)
# Pinned to a fixed commit and verified by SHA-256 so builds are reproducible.
# Files land in models/ner/ and are copied to the app output by the .csproj.
#
# Usage:
#   ./scripts/download_models.sh
# ═══════════════════════════════════════════════════════════════════════════
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"
MODEL_DIR="$PROJECT_DIR/models/ner"

REPO="Xenova/bert-base-NER"
COMMIT="8e892123e8b7c2c0c2bd1dcb598b7d244c4e53aa"
BASE_URL="https://huggingface.co/$REPO/resolve/$COMMIT"

# local name | remote path | sha256
FILES=(
    "model.onnx|onnx/model_quantized.onnx|caaee70a5518ec7f9e46e5308fcc9263a8c227703a9ce46cf61c69a552349648"
    "vocab.txt|vocab.txt|eeaa9875b23b04b4c54ef759d03db9d1ba1554838f8fb26c5d96fa551df93d02"
    "config.json|config.json|a73a2eccc921bbdea95a94b49a157d3694b5c2abbae7a6f3000e14404a9c31a8"
)

sha256_of() {
    if command -v shasum &> /dev/null; then shasum -a 256 "$1" | cut -d' ' -f1
    else sha256sum "$1" | cut -d' ' -f1; fi
}

mkdir -p "$MODEL_DIR"
for entry in "${FILES[@]}"; do
    IFS='|' read -r name remote expected <<< "$entry"
    target="$MODEL_DIR/$name"
    if [ -f "$target" ] && [ "$(sha256_of "$target")" = "$expected" ]; then
        echo "       ✓ $name (cached)"
        continue
    fi
    echo "       Downloading $name..."
    curl -fsSL -o "$target.part" "$BASE_URL/$remote"
    actual="$(sha256_of "$target.part")"
    if [ "$actual" != "$expected" ]; then
        rm -f "$target.part"
        echo "✗ Checksum mismatch for $name (expected $expected, got $actual)"
        exit 1
    fi
    mv "$target.part" "$target"
    echo "       ✓ $name"
done
