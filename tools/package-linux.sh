#!/usr/bin/env bash
# Package a self-contained Linux publish into a .deb and an AppImage.
# Usage: package-linux.sh <publish-dir> <version> <out-dir> <icon-png>
set -euo pipefail

PUBDIR="$1"          # self-contained publish (contains the Ircuitry binary)
VERSION="$2"         # e.g. 0.1.1 (no leading v)
OUT="${3:-.}"
ICON="${4:-assets/icons/icon-256.png}"
APPID=ircuitry
WORK="$(mktemp -d)"
mkdir -p "$OUT"

# ---------------------------------------------------------------- .deb
DEB="$WORK/deb"
install -d "$DEB/opt/$APPID" "$DEB/usr/bin" \
          "$DEB/usr/share/applications" \
          "$DEB/usr/share/icons/hicolor/256x256/apps" "$DEB/DEBIAN"
cp -a "$PUBDIR"/. "$DEB/opt/$APPID/"
chmod +x "$DEB/opt/$APPID/Ircuitry"

cat > "$DEB/usr/bin/$APPID" <<EOF
#!/bin/sh
exec /opt/$APPID/Ircuitry "\$@"
EOF
chmod +x "$DEB/usr/bin/$APPID"

cp "$ICON" "$DEB/usr/share/icons/hicolor/256x256/apps/$APPID.png"
cat > "$DEB/usr/share/applications/$APPID.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=ircuitry
Comment=Visual IRCv3 bot builder
Exec=$APPID
Icon=$APPID
Terminal=false
Categories=Development;Network;
EOF

SIZE=$(du -sk "$DEB/opt" "$DEB/usr" | awk '{s+=$1} END{print s}')
cat > "$DEB/DEBIAN/control" <<EOF
Package: $APPID
Version: $VERSION
Section: devel
Priority: optional
Architecture: amd64
Maintainer: ircuitry contributors <noreply@users.noreply.github.com>
Installed-Size: $SIZE
Description: Visual IRCv3 bot builder
 ircuitry is a cozy node-graph desktop app for building and
 running IRCv3 bots. Self-contained; no .NET install required.
EOF
dpkg-deb --root-owner-group --build "$DEB" "$OUT/ircuitry-$VERSION-amd64.deb"

# ----------------------------------------------------------- AppImage
APPDIR="$WORK/$APPID.AppDir"
install -d "$APPDIR/usr/bin" "$APPDIR/usr/share/applications" \
          "$APPDIR/usr/share/icons/hicolor/256x256/apps"
cp -a "$PUBDIR"/. "$APPDIR/usr/bin/"
chmod +x "$APPDIR/usr/bin/Ircuitry"
cp "$ICON" "$APPDIR/$APPID.png"
cp "$ICON" "$APPDIR/usr/share/icons/hicolor/256x256/apps/$APPID.png"
cat > "$APPDIR/$APPID.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=ircuitry
Comment=Visual IRCv3 bot builder
Exec=Ircuitry
Icon=$APPID
Terminal=false
Categories=Development;Network;
EOF
cp "$APPDIR/$APPID.desktop" "$APPDIR/usr/share/applications/$APPID.desktop"
cat > "$APPDIR/AppRun" <<'EOF'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/bin/Ircuitry" "$@"
EOF
chmod +x "$APPDIR/AppRun"

TOOL="$WORK/appimagetool"
curl -fsSL -o "$TOOL" \
  "https://github.com/AppImage/AppImageKit/releases/download/continuous/appimagetool-x86_64.AppImage"
chmod +x "$TOOL"
ARCH=x86_64 "$TOOL" --appimage-extract-and-run "$APPDIR" "$OUT/ircuitry-$VERSION-x86_64.AppImage"

rm -rf "$WORK"
echo "packaged:"
ls -lh "$OUT"/ircuitry-"$VERSION"-amd64.deb "$OUT"/ircuitry-"$VERSION"-x86_64.AppImage
