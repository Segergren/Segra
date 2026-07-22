#!/bin/bash
# Builds the Linux OBS runtime bundle that Segra downloads at launch, from OBS Studio's OFFICIAL
# Ubuntu .deb published on GitHub releases.
#
#   ./Obs/build-linux-bundle.sh <obs-version> [ubuntu-base]
#   ./Obs/build-linux-bundle.sh 32.2.0            # defaults to the 24.04 base
#
# Why the 24.04 base (and not the newest): a bundle inherits the glibc/FFmpeg floor of the distro it
# was built on. Building on 24.04 (glibc 2.39, FFmpeg 6) yields a bundle that loads on 24.04 LTS and
# every newer release. Building on 26.04 (glibc 2.43, FFmpeg 7) produced a bundle that could not even
# be dlopen'd on 24.04 — libobs referenced GLIBC_2.43 and libavcodec.so.62, neither present there.
# Always target the OLDEST release Segra supports.
#
# Outputs:
#   Obs/OBS <version> linux.tar.gz          the runtime bundle (lib/ + obs-plugins/ + data/)
#   packaging/linux/obs-helpers/{obs-nvenc-test,obs-ffmpeg-mux}
#                                           the two subprocess helpers OBS resolves next to the main
#                                           executable (readlink /proc/self/exe -> dirname). They are
#                                           shipped WITH the app (see build-flatpak.sh, which places them
#                                           beside the Segra binary), not in this bundle, because that is
#                                           where OBS looks for them.
set -euo pipefail

VERSION="${1:-}"
UBUNTU="${2:-24.04}"
if [ -z "$VERSION" ]; then
    echo "usage: $0 <obs-version> [ubuntu-base]   e.g. $0 32.2.0" >&2
    exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

DEB="OBS-Studio-${VERSION}-Ubuntu-${UBUNTU}-x86_64.deb"
URL="https://github.com/obsproject/obs-studio/releases/download/${VERSION}/${DEB}"

echo "=== Downloading $DEB ==="
curl -fL "$URL" -o "$WORK/obs.deb"
dpkg-deb -x "$WORK/obs.deb" "$WORK/root"

SRC="$WORK/root/usr/local"
[ -d "$SRC/lib/x86_64-linux-gnu" ] || SRC="$WORK/root/usr"   # fall back if layout differs
LIBSRC="$SRC/lib/x86_64-linux-gnu"

OUT="$WORK/bundle"
mkdir -p "$OUT/lib" "$OUT/obs-plugins" "$OUT/data"

echo "=== Assembling bundle ==="
# Core libraries (+ symlinks, preserved).
cp -a "$LIBSRC/"libobs*.so* "$OUT/lib/"

# Plugins, minus the Qt/CEF/UI/streaming ones that abort or bloat a headless recorder. This mirrors
# the exclusion list in run.sh (build-local.sh) and Backend/Platform/Linux/LinuxObsRuntime.cs.
for so in "$LIBSRC/obs-plugins/"*.so; do
    [ -e "$so" ] || continue
    b="$(basename "$so")"
    case "$b" in
        # obs-browser + its Chromium Embedded Framework support libs (libcef.so alone is ~230 MB), plus
        # websocket/UI/vst/decklink — none needed by a headless recorder.
        obs-browser.so|obs-websocket.so|frontend-tools.so|obs-vst.so|decklink*.so|*-ui.so|\
        libcef.so|libGLESv2.so|libvk_swiftshader.so|libEGL.so) ;;
        *) cp -a "$so" "$OUT/obs-plugins/" ;;
    esac
done

# Data (libobs effects + per-plugin data).
cp -a "$SRC/share/obs/libobs"      "$OUT/data/"
cp -a "$SRC/share/obs/obs-plugins" "$OUT/data/"

echo "=== Verifying glibc floor (< 2.40 so it loads on 24.04+) ==="
bad=0
for f in "$OUT/lib/"libobs.so.*[0-9] "$OUT/obs-plugins/"*.so; do
    hi="$(objdump -T "$f" 2>/dev/null | grep -oE 'GLIBC_[0-9]+\.[0-9]+' | sort -V | tail -1 || true)"
    case "$hi" in GLIBC_2.4[0-9]) echo "  FAIL: $(basename "$f") needs $hi"; bad=1 ;; esac
done
[ $bad -eq 0 ] && echo "  OK: every component's max glibc is < 2.40" || { echo "ABORT: bundle needs a glibc newer than the 24.04 base."; exit 1; }

echo "=== Packing ==="
TARBALL="$REPO_DIR/Obs/OBS ${VERSION} linux.tar.gz"
( cd "$OUT" && tar --sort=name --owner=0 --group=0 -I 'gzip -9' -cf "$TARBALL" lib obs-plugins data )
echo "  wrote: $TARBALL ($(du -h "$TARBALL" | cut -f1))"

echo "=== Extracting subprocess helpers (shipped with the app, not this bundle) ==="
HELPERS="$REPO_DIR/packaging/linux/obs-helpers"
mkdir -p "$HELPERS"
for h in obs-nvenc-test obs-ffmpeg-mux; do
    src="$(find "$WORK/root" -type f -name "$h" | head -1)"
    if [ -n "$src" ]; then cp -a "$src" "$HELPERS/$h"; chmod +x "$HELPERS/$h"; echo "  $h"; else echo "  WARNING: $h not found in deb"; fi
done

echo ""
echo "=== Done ==="
echo "Commit the new 'Obs/OBS ${VERSION} linux.tar.gz' (Git LFS) and packaging/linux/obs-helpers/."
