#!/bin/bash
# ═══════════════════════════════════════════════════════════════════════════
# download_models.sh — Download the on-device detection models
#
#   models/ner/   person names: dslim/bert-base-NER, int8 ONNX export by Xenova (MIT)
#   models/face/  faces: UltraFace version-RFB-640 from the ONNX model zoo (MIT)
#
# Each file is pinned to a fixed commit and verified by SHA-256 so builds are
# reproducible. The .csproj copies models/** into the app output.
#
# Usage:
#   ./scripts/download_models.sh
# ═══════════════════════════════════════════════════════════════════════════
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"
MODELS_DIR="$PROJECT_DIR/models"

NER_URL="https://huggingface.co/Xenova/bert-base-NER/resolve/8e892123e8b7c2c0c2bd1dcb598b7d244c4e53aa"
FACE_URL="https://media.githubusercontent.com/media/onnx/models/4c46cd00fbdb7cd30b6c1c17ab54f2e1f4f7b177/validated/vision/body_analysis/ultraface/models"

# local path (under models/) | url | sha256
FILES=(
    "ner/model.onnx|$NER_URL/onnx/model_quantized.onnx|caaee70a5518ec7f9e46e5308fcc9263a8c227703a9ce46cf61c69a552349648"
    "ner/vocab.txt|$NER_URL/vocab.txt|eeaa9875b23b04b4c54ef759d03db9d1ba1554838f8fb26c5d96fa551df93d02"
    "ner/config.json|$NER_URL/config.json|a73a2eccc921bbdea95a94b49a157d3694b5c2abbae7a6f3000e14404a9c31a8"
    "face/version-RFB-640.onnx|$FACE_URL/version-RFB-640.onnx|8f4c659275977e7a3bfbfa339a9c769ad793df50f9c0baa8c14b11baa1646430"
)

sha256_of() {
    if command -v shasum &> /dev/null; then shasum -a 256 "$1" | cut -d' ' -f1
    else sha256sum "$1" | cut -d' ' -f1; fi
}

for entry in "${FILES[@]}"; do
    IFS='|' read -r name url expected <<< "$entry"
    target="$MODELS_DIR/$name"
    mkdir -p "$(dirname "$target")"
    if [ -f "$target" ] && [ "$(sha256_of "$target")" = "$expected" ]; then
        echo "       ✓ $name (cached)"
        continue
    fi
    echo "       Downloading $name..."
    curl -fsSL -o "$target.part" "$url"
    actual="$(sha256_of "$target.part")"
    if [ "$actual" != "$expected" ]; then
        rm -f "$target.part"
        echo "✗ Checksum mismatch for $name (expected $expected, got $actual)"
        exit 1
    fi
    mv "$target.part" "$target"
    echo "       ✓ $name"
done
