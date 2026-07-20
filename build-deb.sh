#!/bin/bash
# Builds the Segra .deb from an existing linux-x64 publish directory.
# Must run on Linux (uses dpkg-deb). Produces ./output/segra_<version>_amd64.deb.
#
#   SEGRA_VERSION=1.7.0 ./build-deb.sh [publish-dir]
#
# The .deb installs the app to /opt/segra, a launcher to /usr/bin/segra, a desktop entry + icon, and
# (when packaging/linux/segra-archive-keyring.gpg exists) the apt-repo signing key. OBS is NOT bundled;
# Segra downloads it on first launch.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

VERSION="${SEGRA_VERSION:-1.0.0}"
PUBLISH="${1:-publish}"

if [ ! -f "$PUBLISH/Segra" ]; then
    echo "error: '$PUBLISH/Segra' not found. Publish first:" >&2
    echo "  dotnet publish Segra.csproj -c Release -f net10.0 -r linux-x64 --self-contained -p:TargetFrameworks=net10.0 -o $PUBLISH" >&2
    exit 1
fi

PKGROOT="$(mktemp -d)"
trap 'rm -rf "$PKGROOT"' EXIT

# App payload -> /opt/segra (read-only at runtime; state lives in ~/.config/Segra).
install -d "$PKGROOT/opt/segra"
cp -r "$PUBLISH"/. "$PKGROOT/opt/segra/"
chmod +x "$PKGROOT/opt/segra/Segra"

# Launcher on PATH.
install -d "$PKGROOT/usr/bin"
cat > "$PKGROOT/usr/bin/segra" <<'LAUNCH'
#!/bin/sh
exec /opt/segra/Segra "$@"
LAUNCH
chmod +x "$PKGROOT/usr/bin/segra"

# Desktop entry + icon (makes it searchable in the app menu).
install -Dm644 packaging/linux/segra.desktop "$PKGROOT/usr/share/applications/segra.desktop"
install -Dm644 icon.png "$PKGROOT/usr/share/icons/hicolor/512x512/apps/segra.png"

# Apt-repo signing key (public), shipped so postinst can register the repo. Optional for test builds.
if [ -f packaging/linux/segra-archive-keyring.gpg ]; then
    install -Dm644 packaging/linux/segra-archive-keyring.gpg "$PKGROOT/usr/share/keyrings/segra-archive-keyring.gpg"
else
    echo "note: packaging/linux/segra-archive-keyring.gpg missing; building without apt-repo registration (no auto-update)."
fi

# Control metadata + maintainer scripts.
install -d "$PKGROOT/DEBIAN"
sed "s/@VERSION@/$VERSION/" packaging/linux/control.in > "$PKGROOT/DEBIAN/control"
install -m755 packaging/linux/postinst "$PKGROOT/DEBIAN/postinst"
install -m755 packaging/linux/postrm  "$PKGROOT/DEBIAN/postrm"

mkdir -p output
DEB="output/segra_${VERSION}_amd64.deb"
dpkg-deb --root-owner-group --build "$PKGROOT" "$DEB"

echo ""
echo "=== Done! ==="
echo "Package: $SCRIPT_DIR/$DEB"
dpkg-deb --info "$DEB" | sed -n '/Package:/,/Description:/p'
