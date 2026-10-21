#!/bin/bash
# ═══════════════════════════════════════════════════════════════════════════
# package.sh — Package ScreenGuard into macOS .app bundle, .zip, and .dmg
#
# Generates production distribution artifacts containing:
#   1. ScreenGuard.app (macOS Application Bundle)
#   2. libscreencapture.dylib (Native C++ event tap & capture engine)
#   3. .NET 8 self-contained runtime binaries
#   4. Tesseract OCR engine (CLI + dylibs, relinked into the bundle) and language assets
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
NATIVE_ARCH="$([ "$ARCH" = "x64" ] && echo x86_64 || echo arm64)" "$SCRIPT_DIR/build_native.sh"

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

# Name-recognition (NER) model, copied into the publish output by the .csproj
"$SCRIPT_DIR/download_models.sh"

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

# Bundle entry point. ScreenGuard is a console app: double-clicked from Finder it would
# have no window and no keyboard, so the launcher reopens it inside a Terminal window.
# Run from a terminal (or with arguments), it runs the app directly.
cat > "$MACOS_DIR/ScreenGuardLauncher" << 'LAUNCHER'
#!/bin/bash
DIR="$(cd "$(dirname "$0")" && pwd)"
if [ -t 1 ] || [ $# -gt 0 ]; then
    exec "$DIR/ScreenGuard" "$@"
fi
exec open -a Terminal "$DIR/ScreenGuard"
LAUNCHER
chmod +x "$MACOS_DIR/ScreenGuardLauncher"

# Also copy tessdata into Contents/Resources/tessdata
mkdir -p "$RESOURCES_DIR/tessdata"
cp -f "$TESSDATA_DIR/eng.traineddata" "$RESOURCES_DIR/tessdata/"

# Bundle the tesseract CLI and every non-system dylib it needs, rewritten to load from
# the bundle, so OCR works on Macs without Homebrew.
bundle_tesseract() {
    local dest="$CONTENTS_DIR/Frameworks/tesseract"
    local src
    src="$(command -v tesseract || true)"
    if [ -z "$src" ]; then
        echo "✗ Error: tesseract not found. Install with: brew install tesseract"
        exit 1
    fi
    src="$(realpath "$src")"
    mkdir -p "$dest"
    cp -f "$src" "$dest/tesseract"
    chmod u+w "$dest/tesseract"

    local queue=("$dest/tesseract")
    local -a origins=("$(dirname "$src")")
    while [ ${#queue[@]} -gt 0 ]; do
        local file="${queue[0]}"
        local origin="${origins[0]}"
        queue=("${queue[@]:1}")
        origins=("${origins[@]:1}")

        # Skip the first line (file name) and, for dylibs, the LC_ID_DYLIB self-reference
        local deps
        deps="$(otool -L "$file" | tail -n +2 | awk '{print $1}')"
        for dep in $deps; do
            case "$dep" in
                /usr/lib/*|/System/*) continue ;;
            esac
            local resolved="$dep"
            case "$dep" in
                @loader_path/*) resolved="$origin/${dep#@loader_path/}" ;;
                @rpath/*)
                    resolved=""
                    for candidate in "$origin/${dep#@rpath/}" "$origin/../lib/${dep#@rpath/}" \
                                     "/opt/homebrew/lib/${dep#@rpath/}" "/usr/local/lib/${dep#@rpath/}"; do
                        if [ -f "$candidate" ]; then resolved="$candidate"; break; fi
                    done
                    ;;
            esac
            if [ -z "$resolved" ] || [ ! -f "$resolved" ]; then
                echo "✗ Error: cannot resolve $dep needed by $(basename "$file")"
                exit 1
            fi

            local name
            name="$(basename "$dep")"
            if [ "$(basename "$file")" = "$name" ]; then continue; fi
            if [ ! -f "$dest/$name" ]; then
                cp -fL "$resolved" "$dest/$name"
                chmod u+w "$dest/$name"
                install_name_tool -id "@loader_path/$name" "$dest/$name" 2>/dev/null
                queue+=("$dest/$name")
                origins+=("$(dirname "$(realpath "$resolved")")")
            fi
            install_name_tool -change "$dep" "@loader_path/$name" "$file" 2>/dev/null
        done
    done

    # Relinking invalidates signatures; re-sign ad hoc so Apple Silicon will load them
    for f in "$dest"/*; do
        codesign --force --sign - "$f" 2>/dev/null
    done

    # Verify nothing still points outside the bundle
    if otool -L "$dest"/* | grep -E "^\s+/(opt/homebrew|usr/local)/" >/dev/null; then
        echo "✗ Error: bundled tesseract still references Homebrew paths"
        exit 1
    fi
    "$dest/tesseract" --version >/dev/null 2>&1 || { echo "✗ Error: bundled tesseract does not run"; exit 1; }
    echo "       ✓ Bundled tesseract + $(($(ls "$dest" | wc -l) - 1)) libraries into Frameworks/tesseract"
}
bundle_tesseract

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
    <string>ScreenGuardLauncher</string>
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
    <string>12.3</string>
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
1. Double-click ScreenGuard.app (or "run.command"). It opens in a Terminal
   window; click anywhere to capture. Press ENTER or Ctrl+C to stop.
   Results are saved to ~/ScreenGuard/output (original, masked,
   comparison, reports).

2. If macOS blocks execution with "unidentified developer" or Gatekeeper:
   Run in Terminal:
   xattr -cr ScreenGuard.app

3. SYSTEM PERMISSIONS REQUIRED:
   When prompted, grant:
   - Input Monitoring: System Settings > Privacy & Security > Input Monitoring
   - Screen Recording: System Settings > Privacy & Security > Screen Recording
   Grant them to Terminal (ScreenGuard runs inside Terminal), then quit
   Terminal completely (Cmd+Q) and open ScreenGuard again.

COMMAND-LINE OPTIONS:
  ./ScreenGuard.app/Contents/MacOS/ScreenGuard [output_dir]
  ./ScreenGuard.app/Contents/MacOS/ScreenGuard --benchmark
  ./ScreenGuard.app/Contents/MacOS/ScreenGuard --test
  ./ScreenGuard.app/Contents/MacOS/ScreenGuard --scan <image_file>
  ./ScreenGuard.app/Contents/MacOS/ScreenGuard --no-original
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
