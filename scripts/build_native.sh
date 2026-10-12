#!/bin/bash
# ═══════════════════════════════════════════════════════════════════════════
# build_native.sh — Build the C++ native screen capture library
#
# Compiles the native C++ code into libscreencapture.dylib and copies it
# to the C# app directory for P/Invoke loading.
#
# Prerequisites:
#   - CMake 3.20+
#   - Xcode Command Line Tools (clang++)
#
# Usage:
#   ./scripts/build_native.sh
# ═══════════════════════════════════════════════════════════════════════════
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"
NATIVE_DIR="$PROJECT_DIR/native"
BUILD_DIR="$NATIVE_DIR/build"
APP_DIR="$PROJECT_DIR/app"

echo ""
echo "╔══════════════════════════════════════════════════════╗"
echo "║     Building C++ Native Library (macOS)              ║"
echo "╚══════════════════════════════════════════════════════╝"
echo ""

# Check prerequisites
if ! command -v cmake &> /dev/null; then
    echo "✗ CMake not found. Install with: brew install cmake"
    exit 1
fi

echo "[1/3] Configuring CMake..."
mkdir -p "$BUILD_DIR"
cd "$BUILD_DIR"
cmake .. -DCMAKE_BUILD_TYPE=Release 2>&1 | sed 's/^/       /'

echo ""
echo "[2/3] Compiling..."
cmake --build . --config Release 2>&1 | sed 's/^/       /'

echo ""
echo "[3/3] Copying library to app directory..."
DYLIB_PATH="$BUILD_DIR/libscreencapture.dylib"
if [ -f "$DYLIB_PATH" ]; then
    cp "$DYLIB_PATH" "$APP_DIR/"
    echo "       ✓ Copied libscreencapture.dylib → app/"

    # Show library info
    echo ""
    echo "   Library details:"
    echo "       Path: $APP_DIR/libscreencapture.dylib"
    echo "       Size: $(du -h "$APP_DIR/libscreencapture.dylib" | cut -f1)"
    echo "       Arch: $(file "$APP_DIR/libscreencapture.dylib" | grep -o 'arm64\|x86_64')"

    # Show exported symbols
    echo ""
    echo "   Exported API symbols:"
    nm -gU "$APP_DIR/libscreencapture.dylib" 2>/dev/null | grep "Skan" | sed 's/^/       /' || true
else
    echo "       ✗ Build failed: libscreencapture.dylib not found at $DYLIB_PATH"
    exit 1
fi

echo ""
echo "╔══════════════════════════════════════════════════════╗"
echo "║     ✓ Native library built successfully              ║"
echo "╚══════════════════════════════════════════════════════╝"
echo ""
