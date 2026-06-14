#!/usr/bin/env bash
# Wrap a self-contained macOS publish into a double-clickable .app bundle, zipped.
# Usage: package-macos.sh <publish-dir> <version> <rid> <out-dir> <icon-png>
set -euo pipefail

PUBDIR="$1"          # self-contained publish (contains the Ircuitry binary)
VERSION="$2"         # e.g. 0.1.1 (no leading v)
RID="$3"             # osx-x64 | osx-arm64
OUT="${4:-.}"
ICON="${5:-assets/icons/icon-256.png}"
WORK="$(mktemp -d)"
APP="$WORK/ircuitry.app"
mkdir -p "$OUT"; OUT="$(cd "$OUT" && pwd)"

mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -a "$PUBDIR"/. "$APP/Contents/MacOS/"
chmod +x "$APP/Contents/MacOS/Ircuitry"

# optional .icns (ImageMagick); the app still runs without it
ICNS=""
if command -v convert >/dev/null 2>&1 && convert "$ICON" "$APP/Contents/Resources/ircuitry.icns" 2>/dev/null; then
  ICNS="<key>CFBundleIconFile</key><string>ircuitry.icns</string>"
fi

cat > "$APP/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>ircuitry</string>
  <key>CFBundleDisplayName</key><string>ircuitry</string>
  <key>CFBundleIdentifier</key><string>io.github.ircuitry</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleExecutable</key><string>Ircuitry</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>LSMinimumSystemVersion</key><string>10.15</string>
  <key>NSHighResolutionCapable</key><true/>
  $ICNS
</dict></plist>
EOF

( cd "$WORK" && zip -r -y -q "$OUT/ircuitry-$RID.zip" ircuitry.app )
rm -rf "$WORK"
echo "packaged: $OUT/ircuitry-$RID.zip"
unzip -l "$OUT/ircuitry-$RID.zip" | grep -E 'Info.plist|MacOS/Ircuitry$|\.icns' || true
