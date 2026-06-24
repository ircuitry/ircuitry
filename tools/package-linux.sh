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
Exec=$APPID %U
Icon=$APPID
Terminal=false
Categories=Development;Network;
MimeType=x-scheme-handler/ircuitry;x-scheme-handler/ircbot;application/x-ircuitry-bot;application/x-ircuitry-node;
EOF

# MIME glob: *.ircbot / *.ircnode -> our types (dpkg's shared-mime-info trigger runs update-mime-database on install)
install -d "$DEB/usr/share/mime/packages"
cat > "$DEB/usr/share/mime/packages/$APPID.xml" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<mime-info xmlns="http://www.freedesktop.org/standards/shared-mime-info">
  <mime-type type="application/x-ircuitry-bot"><comment>ircuitry workflow</comment><glob pattern="*.ircbot"/><icon name="$APPID"/></mime-type>
  <mime-type type="application/x-ircuitry-node"><comment>ircuitry node</comment><glob pattern="*.ircnode"/><icon name="$APPID"/></mime-type>
</mime-info>
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
Exec=Ircuitry %U
Icon=$APPID
Terminal=false
Categories=Development;Network;
MimeType=x-scheme-handler/ircuitry;x-scheme-handler/ircbot;application/x-ircuitry-bot;application/x-ircuitry-node;
EOF
cp "$APPDIR/$APPID.desktop" "$APPDIR/usr/share/applications/$APPID.desktop"
install -d "$APPDIR/usr/share/mime/packages"
cat > "$APPDIR/usr/share/mime/packages/$APPID.xml" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<mime-info xmlns="http://www.freedesktop.org/standards/shared-mime-info">
  <mime-type type="application/x-ircuitry-bot"><comment>ircuitry workflow</comment><glob pattern="*.ircbot"/><icon name="$APPID"/></mime-type>
  <mime-type type="application/x-ircuitry-node"><comment>ircuitry node</comment><glob pattern="*.ircnode"/><icon name="$APPID"/></mime-type>
</mime-info>
EOF
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
