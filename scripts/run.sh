#!/bin/bash
# ═══════════════════════════════════════════════════════════════════════════
# run.sh — Build everything and run the ScreenGuard application
#
# 1. Builds the C++ native library (libscreencapture.dylib)
# 2. Downloads tessdata if not present
# 3. Builds and runs the C# .NET application
#
# Usage:
#   ./scripts/run.sh [output_directory]
# ═══════════════════════════════════════════════════════════════════════════
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"
APP_DIR="$PROJECT_DIR/app"
TESSDATA_DIR="$PROJECT_DIR/tessdata"
OUTPUT_DIR="${1:-$PROJECT_DIR/output}"

# ── Step 1: Build native library ────────────────────────────────────────
"$SCRIPT_DIR/build_native.sh"

# ── Step 2: Download tessdata if needed ──────────────────────────────────
if [ ! -f "$TESSDATA_DIR/eng.traineddata" ]; then
    echo ""
    echo "[*] Downloading Tesseract English language data..."
    mkdir -p "$TESSDATA_DIR"
    curl -L -o "$TESSDATA_DIR/eng.traineddata" \
        "https://github.com/tesseract-ocr/tessdata/raw/main/eng.traineddata" 2>&1 | tail -1
    echo "    ✓ Downloaded eng.traineddata ($(du -h "$TESSDATA_DIR/eng.traineddata" | cut -f1))"
fi

# ── Step 3: Build and run C# application ────────────────────────────────
echo ""
echo "╔══════════════════════════════════════════════════════╗"
echo "║     Building & Running C# Application                ║"
echo "╚══════════════════════════════════════════════════════╝"
echo ""

cd "$APP_DIR"

# Set DOTNET_ROOT if installed via Homebrew
if [ -d "/opt/homebrew/opt/dotnet@8/libexec" ]; then
    export DOTNET_ROOT="/opt/homebrew/opt/dotnet@8/libexec"
    export PATH="$DOTNET_ROOT:$PATH"
fi

# Ensure the native dylib can be found at runtime
export DYLD_LIBRARY_PATH="$APP_DIR:${DYLD_LIBRARY_PATH:-}"

# Restore NuGet packages and build
dotnet restore
dotnet run --configuration Release -- "$OUTPUT_DIR"
