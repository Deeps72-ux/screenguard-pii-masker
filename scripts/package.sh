#!/bin/bash
# ═══════════════════════════════════════════════════════════════════════════
# package.sh — Package ScreenGuard into macOS .app bundle, .zip, and .dmg
#
# Generates production distribution artifacts containing:
#   1. ScreenGuard.app (macOS Application Bundle)
#   2. libscreencapture.dylib (Native C++ event tap & capture engine)
#   3. .NET 8 self-contained runtime binaries
#   4. Tesseract OCR language assets
#   5. ScreenGuard-macOS-{arch}.zip
#   6. ScreenGuard-macOS-{arch}.dmg
#
# Usage:
#   ./scripts/package.sh [arm64|x64] [version]
# ═══════════════════════════════════════════════════════════════════════════
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"
APP_DIR="$PROJECT_DIR/app"
NATIVE_DIR="$PROJECT_DIR/native"
TESSDATA_DIR="$PROJECT_DIR/tessdata"
DIST_DIR="$PROJECT_DIR/dist"

ARCH="${1:-arm64}"
VERSION="${2:-1.0.0}"
RID="osx-${ARCH}"

echo ""
echo "╔══════════════════════════════════════════════════════╗"
echo "║    ScreenGuard macOS Packaging & Distribution        ║"
echo "╚══════════════════════════════════════════════════════╝"
echo "  Target Architecture: $ARCH ($RID)"
echo "  Version:             $VERSION"
echo "  Output Directory:    $DIST_DIR"
echo ""

# ── Step 1: Build native C++ library ─────────────────────────────────────────
echo "[1/6] Building native C++ library (libscreencapture.dylib)..."
"$SCRIPT_DIR/build_native.sh"

DYLIB_SRC="$APP_DIR/libscreencapture.dylib"
if [ ! -f "$DYLIB_SRC" ]; then
    echo "✗ Error: libscreencapture.dylib not found at $DYLIB_SRC"
    exit 1
fi

# ── Step 2: Ensure Tesseract traineddata exists ──────────────────────────────
echo ""
echo "[2/6] Ensuring Tesseract OCR language assets..."
mkdir -p "$TESSDATA_DIR"
if [ ! -f "$TESSDATA_DIR/eng.traineddata" ]; then
    echo "       Downloading eng.traineddata..."
    curl -L -s -o "$TESSDATA_DIR/eng.traineddata" \
        "https://github.com/tesseract-ocr/tessdata/raw/main/eng.traineddata"
fi
echo "       ✓ eng.traineddata ready ($(du -h "$TESSDATA_DIR/eng.traineddata" | cut -f1))"

# ── Step 3: Publish self-contained .NET 8 app ────────────────────────────────
echo ""
echo "[3/6] Publishing self-contained .NET 8 application for $RID..."
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

# Ensure native library and tessdata are inside publish directory
cp -f "$DYLIB_SRC" "$STAGING_PUBLISH/libscreencapture.dylib"
mkdir -p "$STAGING_PUBLISH/tessdata"
cp -f "$TESSDATA_DIR/eng.traineddata" "$STAGING_PUBLISH/tessdata/"
chmod +x "$STAGING_PUBLISH/ScreenGuard"

echo "       ✓ Self-contained application published."

# ── Step 4: Assemble macOS .app Bundle ───────────────────────────────────────
echo ""
echo "[4/6] Assembling ScreenGuard.app bundle..."
APP_BUNDLE="$DIST_DIR/ScreenGuard.app"
CONTENTS_DIR="$APP_BUNDLE/Contents"
MACOS_DIR="$CONTENTS_DIR/MacOS"
RESOURCES_DIR="$CONTENTS_DIR/Resources"

rm -rf "$APP_BUNDLE"
mkdir -p "$MACOS_DIR" "$RESOURCES_DIR"

# Copy all published files into Contents/MacOS
cp -R "$STAGING_PUBLISH/"* "$MACOS_DIR/"

# Also copy tessdata into Contents/Resources/tessdata
mkdir -p "$RESOURCES_DIR/tessdata"
cp -f "$TESSDATA_DIR/eng.traineddata" "$RESOURCES_DIR/tessdata/"

# Create Info.plist
cat > "$CONTENTS_DIR/Info.plist" << EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleDevelopmentRegion</key>
    <string>en</string>
    <key>CFBundleDisplayName</key>
    <string>ScreenGuard</string>
    <key>CFBundleExecutable</key>
    <string>ScreenGuard</string>
    <key>CFBundleIdentifier</key>
    <string>com.screenguard.app</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>CFBundleName</key>
    <string>ScreenGuard</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>${VERSION}</string>
    <key>CFBundleVersion</key>
    <string>${VERSION}</string>
    <key>LSMinimumSystemVersion</key>
    <string>14.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSSupportsAutomaticGraphicsSwitching</key>
    <true/>
</dict>
</plist>
EOF

# Create PkgInfo
printf "APPL????" > "$CONTENTS_DIR/PkgInfo"

# Ad-hoc code sign the dylib and executable for Apple Silicon
if command -v codesign &> /dev/null; then
    echo "       Applying ad-hoc code signature..."
    codesign --force --deep --sign - "$MACOS_DIR/libscreencapture.dylib" 2>/dev/null || true
    codesign --force --deep --sign - "$APP_BUNDLE" 2>/dev/null || true
fi

echo "       ✓ Assembled $APP_BUNDLE"

# ── Step 5: Create ZIP Archive ───────────────────────────────────────────────
echo ""
echo "[5/6] Creating ZIP distribution package..."
ZIP_NAME="ScreenGuard-macOS-${ARCH}-v${VERSION}.zip"
ZIP_PATH="$DIST_DIR/$ZIP_NAME"
rm -f "$ZIP_PATH"

STAGING_ZIP="$DIST_DIR/zip_staging"
rm -rf "$STAGING_ZIP"
mkdir -p "$STAGING_ZIP"

cp -R "$APP_BUNDLE" "$STAGING_ZIP/"

# Create double-clickable run.command launcher
cat > "$STAGING_ZIP/run.command" << 'EOF'
#!/bin/bash
DIR="$(cd "$(dirname "$0")" && pwd)"
echo "Starting ScreenGuard..."
cd "$DIR"
exec "$DIR/ScreenGuard.app/Contents/MacOS/ScreenGuard" "$@"
EOF
chmod +x "$STAGING_ZIP/run.command"

# Create README for end-users
cat > "$STAGING_ZIP/README.txt" << EOF
ScreenGuard — macOS Screen Capture & PII Masker
Version: ${VERSION} (${ARCH})
=================================================

QUICK START:
1. Double-click "run.command" or run from Terminal:
   ./ScreenGuard.app/Contents/MacOS/ScreenGuard

2. If macOS blocks execution with "unidentified developer" or Gatekeeper:
   Run in Terminal:
   xattr -cr ScreenGuard.app

3. SYSTEM PERMISSIONS REQUIRED:
   When prompted, grant:
   - Screen Recording: System Settings > Privacy & Security > Screen Recording
   - Accessibility:    System Settings > Privacy & Security > Accessibility

COMMAND-LINE OPTIONS:
  ./ScreenGuard.app/Contents/MacOS/ScreenGuard [output_dir]
  ./ScreenGuard.app/Contents/MacOS/ScreenGuard --benchmark
  ./ScreenGuard.app/Contents/MacOS/ScreenGuard --test
  ./ScreenGuard.app/Contents/MacOS/ScreenGuard --scan <image_file>
EOF

cd "$STAGING_ZIP"
zip -q -r -y "$ZIP_PATH" .
rm -rf "$STAGING_ZIP"

echo "       ✓ Created: $ZIP_PATH ($(du -h "$ZIP_PATH" | cut -f1))"

# ── Step 6: Create DMG Disk Image ────────────────────────────────────────────
echo ""
echo "[6/6] Creating DMG disk image installer..."
DMG_NAME="ScreenGuard-macOS-${ARCH}-v${VERSION}.dmg"
DMG_PATH="$DIST_DIR/$DMG_NAME"
rm -f "$DMG_PATH"

STAGING_DMG="$DIST_DIR/dmg_staging"
rm -rf "$STAGING_DMG"
mkdir -p "$STAGING_DMG"

cp -R "$APP_BUNDLE" "$STAGING_DMG/"
ln -s /Applications "$STAGING_DMG/Applications"

hdiutil create \
    -volname "ScreenGuard" \
    -srcfolder "$STAGING_DMG" \
    -ov \
    -format UDZO \
    "$DMG_PATH" 2>&1 | sed 's/^/       /'

rm -rf "$STAGING_DMG"

echo "       ✓ Created: $DMG_PATH ($(du -h "$DMG_PATH" | cut -f1))"

# ── Summary ──────────────────────────────────────────────────────────────────
echo ""
echo "╔══════════════════════════════════════════════════════╗"
echo "║     ✓ Distribution packages built successfully!      ║"
echo "╚══════════════════════════════════════════════════════╝"
echo ""
echo "  Artifacts ready in dist/:"
ls -lh "$DIST_DIR"/*.zip "$DIST_DIR"/*.dmg | awk '{print "    • " $9 " (" $5 ")"}'
echo ""
