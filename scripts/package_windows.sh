#!/bin/bash
# ═══════════════════════════════════════════════════════════════════════════
# package_windows.sh — Package ScreenGuard for Microsoft Windows (x64)
#
# Generates a standalone self-contained Windows package containing:
#   1. ScreenGuard.exe (Self-contained .NET 8 executable)
#   2. Pure Win32 GDI screen capture & mouse hook engine
#   3. SkiaSharp native Windows binaries (libSkiaSharp.dll)
#   4. Tesseract OCR models (tessdata/eng.traineddata)
#   5. run.bat launcher
#   6. ScreenGuard-Windows-x64-v{version}.zip
#
# Usage:
#   ./scripts/package_windows.sh [version]
# ═══════════════════════════════════════════════════════════════════════════
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"
APP_DIR="$PROJECT_DIR/app"
TESSDATA_DIR="$PROJECT_DIR/tessdata"
DIST_DIR="$PROJECT_DIR/dist"

VERSION="${1:-1.0.0}"
RID="win-x64"

echo ""
echo "╔══════════════════════════════════════════════════════╗"
echo "║   ScreenGuard Windows (x64) Packaging               ║"
echo "╚══════════════════════════════════════════════════════╝"
echo "  Target Architecture: win-x64"
echo "  Version:             $VERSION"
echo ""

# Ensure Tesseract traineddata exists
echo "[1/3] Ensuring Tesseract OCR language assets..."
mkdir -p "$TESSDATA_DIR"
if [ ! -f "$TESSDATA_DIR/eng.traineddata" ]; then
    echo "       Downloading eng.traineddata..."
    curl -L -s -o "$TESSDATA_DIR/eng.traineddata" \
        "https://github.com/tesseract-ocr/tessdata/raw/main/eng.traineddata"
fi
echo "       ✓ eng.traineddata ready"

# Name-recognition (NER) model, copied into the publish output by the .csproj
"$SCRIPT_DIR/download_models.sh"

# Publish self-contained Windows application
echo ""
echo "[2/3] Publishing self-contained .NET 8 application for $RID..."
STAGING_PUBLISH="$DIST_DIR/publish-$RID"
rm -rf "$STAGING_PUBLISH"
mkdir -p "$STAGING_PUBLISH"

cd "$APP_DIR"
dotnet publish ScreenGuard.App.csproj \
    -c Release \
    -r "$RID" \
    --self-contained true \
    -p:PublishSingleFile=false \
    -p:Version="$VERSION" \
    -o "$STAGING_PUBLISH" 2>&1 | sed 's/^/       /'

# The macOS native library is copied by the csproj when present; it is not used on Windows
rm -f "$STAGING_PUBLISH/libscreencapture.dylib"

# Ensure tessdata is bundled
mkdir -p "$STAGING_PUBLISH/tessdata"
cp -f "$TESSDATA_DIR/eng.traineddata" "$STAGING_PUBLISH/tessdata/"

# Add run.bat launcher
cat > "$STAGING_PUBLISH/run.bat" << 'EOF'
@echo off
title ScreenGuard PII Masker
cd /d "%~dp0"
echo Starting ScreenGuard for Windows...
ScreenGuard.exe %*
pause
EOF

# Add Windows README
cat > "$STAGING_PUBLISH/README.txt" << EOF
ScreenGuard — Screen Capture & PII Masker for Windows
Version: ${VERSION} (win-x64)
=====================================================

QUICK START:
1. Double-click "run.bat" or open Command Prompt / PowerShell:
   ScreenGuard.exe

2. Command-Line Options:
   ScreenGuard.exe --benchmark
   ScreenGuard.exe --test
   ScreenGuard.exe --scan <image_path>
   ScreenGuard.exe -o C:\path\to\output
   ScreenGuard.exe --no-original

REQUIREMENTS:
- Windows 10 or 11 (x64).
- OCR uses the bundled Tesseract engine, which needs the Microsoft Visual C++
  2015-2022 Redistributable (x64): https://aka.ms/vs/17/release/vc_redist.x64.exe
  If OCR cannot start, every capture is fully redacted rather than saved unmasked.

FEATURES ON WINDOWS:
- Zero external runtime required (self-contained .NET 8).
- Per-monitor, DPI-aware Win32 GDI capture of the display that was clicked.
- Global mouse click interception via SetWindowsHookEx (WH_MOUSE_LL).
- Touchpad vs external mouse detection via Raw Input (precision + legacy touchpads).
- Automatic GPU hardware detection (Intel / NVIDIA / AMD).
- Offline Tesseract OCR & regex PII redaction engine.
- On-device person-name recognition (BERT NER via ONNX Runtime).
EOF

# Create ZIP archive
echo ""
echo "[3/3] Creating Windows distribution ZIP archive..."
ZIP_NAME="ScreenGuard-Windows-x64-v${VERSION}.zip"
ZIP_PATH="$DIST_DIR/$ZIP_NAME"
rm -f "$ZIP_PATH"

cd "$STAGING_PUBLISH"
zip -q -r "$ZIP_PATH" .

echo "       ✓ Created: $ZIP_PATH ($(du -h "$ZIP_PATH" | cut -f1))"
echo ""
echo "╔══════════════════════════════════════════════════════╗"
echo "║     ✓ Windows distribution package ready!            ║"
echo "╚══════════════════════════════════════════════════════╝"
echo ""
