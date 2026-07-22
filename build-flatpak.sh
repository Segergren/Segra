#!/bin/bash
# Builds the Segra Flatpak — one artifact for every distro.
#
#   SEGRA_VERSION=1.7.0 OBS_VERSION=32.2.0 ./build-flatpak.sh
#
# Steps:
#   1. build the frontend and publish the self-contained .NET app (linux-x64)
#   2. assemble the OBS runtime (libobs + plugins + data + the two helpers) from OBS Studio's official
#      Ubuntu-24.04 build, via Obs/build-linux-bundle.sh
#   3. stage everything into ./flatpak-staging (the manifest installs this verbatim)
#   4. run flatpak-builder and export output/Segra.flatpak
#
# Requires: flatpak, flatpak-builder, dotnet 10 SDK, node. The GNOME 47 runtime/SDK and the
# ffmpeg-full extension are installed from Flathub if missing.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

VERSION="${SEGRA_VERSION:-1.0.0}"
OBS_VERSION="${OBS_VERSION:-32.2.0}"
# Exported so the csproj BuildFrontendAssets target (which re-runs `npm run build` during publish and
# embeds the result) stamps the same version the backend reports — otherwise the embedded UI says
# "Developer Preview".
export SEGRA_VERSION="$VERSION"
APP_ID="tv.segra.Segra"
MANIFEST="packaging/flatpak/${APP_ID}.yml"
STAGING="flatpak-staging"

command -v flatpak-builder >/dev/null 2>&1 || { echo "error: flatpak-builder not installed (apt install flatpak-builder)"; exit 1; }

echo "=== 1/4 Frontend + publish (linux-x64, v$VERSION) ==="
(cd Frontend && npm ci && SEGRA_VERSION="$VERSION" npm run build)
rm -rf publish
dotnet publish Segra.csproj -c Release --self-contained \
    -r linux-x64 -f net10.0 -p:TargetFrameworks=net10.0 -p:Version="$VERSION" -o publish
# PhotinoServer creates its webroot at startup if missing; ship it so nothing is created at runtime.
mkdir -p publish/wwwroot && cp -r Frontend/dist/* publish/wwwroot/ 2>/dev/null || true

echo "=== 2/4 OBS runtime + helpers (OBS $OBS_VERSION, Ubuntu-24.04 base) ==="
./Obs/build-linux-bundle.sh "$OBS_VERSION"
OBS_TARBALL="Obs/OBS ${OBS_VERSION} linux.tar.gz"
[ -f "$OBS_TARBALL" ] || { echo "error: '$OBS_TARBALL' not produced"; exit 1; }

echo "=== 3/4 Stage payload -> $STAGING ==="
rm -rf "$STAGING"
mkdir -p "$STAGING/payload"
# App (Segra binary + .NET runtime + wwwroot)
cp -a publish/. "$STAGING/payload/"
# OBS runtime (lib/ + obs-plugins/ + data/) unpacked NEXT TO the Segra binary so LinuxObsRuntime's
# self-contained-bundle path (appDir/lib/libobs.so.0) resolves it with no download.
tar xzf "$OBS_TARBALL" -C "$STAGING/payload"
# The two subprocess helpers, beside the Segra binary (libobs finds them via /proc/self/exe).
cp -a packaging/linux/obs-helpers/obs-nvenc-test packaging/linux/obs-helpers/obs-ffmpeg-mux "$STAGING/payload/"
chmod +x "$STAGING/payload/Segra" "$STAGING/payload/obs-nvenc-test" "$STAGING/payload/obs-ffmpeg-mux"

# Bundle OBS's media dependencies from this Ubuntu-24.04 host. The Flatpak runtime ships FFmpeg 7
# (libavcodec.so.61), but the Ubuntu-24.04 OBS links FFmpeg 6 (libavcodec.so.60) plus x264/jansson/
# rist/srt, none matching the runtime — so libobs can't dlopen and OBS silently fails to start. Copy the
# exact libraries OBS resolves here (its flattened ldd closure), filtered to media/codec libs. GL / GTK /
# glibc / system libs are intentionally NOT bundled — those must come from the runtime.
LIBDST="$STAGING/payload/lib"
# Build the set of sonames the GNOME runtime already ships, and bundle ONLY what it lacks. This is matched
# by soname, so a version-mismatched library is still bundled (libicuuc.so.74 vs the runtime's .75,
# libjpeg.so.8 vs .62, OBS's FFmpeg 6 vs the runtime's 7) while everything the runtime provides — glibc,
# GL, X11/Wayland, GTK/GLib, and critically WebKitGTK (whose WebKitNetworkProcess/WebProcess helpers are
# path-coupled to the runtime) — comes from the runtime. Enumerating the runtime beats a hand-written
# denylist that can never be complete.
declare -A RUNTIME_PROVIDES
RT="$(flatpak info -l org.gnome.Platform//47 2>/dev/null || true)"
if [ -n "$RT" ] && [ -d "$RT/files" ]; then
  while IFS= read -r so; do RUNTIME_PROVIDES["$(basename "$so")"]=1; done \
    < <(find "$RT/files" -name '*.so*' 2>/dev/null)
  echo "runtime provides ${#RUNTIME_PROVIDES[@]} sonames; bundling only what it lacks"
else
  echo "WARNING: could not locate org.gnome.Platform//47 inventory; OBS deps may be incomplete"
fi
bundle_media_dep() {   # $1 = resolved host path
  local src="$1" base; base="$(basename "$src")"
  [ -n "${RUNTIME_PROVIDES[$base]:-}" ] && return 1   # runtime already ships this soname — don't bundle
  local real; real="$(readlink -f "$src")"
  cp -n "$real" "$LIBDST/$(basename "$real")" 2>/dev/null || true
  [ "$(basename "$real")" != "$base" ] && ln -sf "$(basename "$real")" "$LIBDST/$base"
  return 0
}
# Scan OBS's libraries AND the app's own native libraries (Photino.Native.so, libSystem.*.Native.so, …):
# Photino.Native is built on Ubuntu 24.04 and needs libicuuc.so.74, but the runtime ships ICU 75, so that
# too must be bundled.
{ for f in "$LIBDST"/libobs*.so.*[0-9] "$STAGING/payload/obs-plugins/"*.so \
           "$STAGING/payload/"*.so \
           "$STAGING/payload/obs-nvenc-test" "$STAGING/payload/obs-ffmpeg-mux"; do
    [ -e "$f" ] && ldd "$f" 2>/dev/null
  done; } | grep -oE '=> /[^ ]+' | awk '{print $2}' | sort -u | while read -r p; do
  bundle_media_dep "$p" || true
done
echo "bundled $(ls "$LIBDST" | grep -cvE '^libobs') media libs into payload/lib"
# Flatpak metadata + launcher + icon the manifest installs
cp packaging/flatpak/segra.sh "$STAGING/"
cp "packaging/flatpak/${APP_ID}.desktop" "$STAGING/"
cp "packaging/flatpak/${APP_ID}.metainfo.xml" "$STAGING/"
# 256x256 PNG (the repo's icon.png is 1000x1000; Flatpak caps hicolor icons at 512x512).
cp packaging/flatpak/icon-256.png "$STAGING/icon-256.png"

echo "=== 4/4 flatpak-builder ==="
# Ensure the runtime/SDK/extension are available (no-op if already installed).
flatpak remote-add --if-not-exists --user flathub https://flathub.org/repo/flathub.flatpakrepo || true
flatpak install --user -y --noninteractive flathub \
    org.gnome.Platform//47 org.gnome.Sdk//47 org.freedesktop.Platform.ffmpeg-full//24.08 || true

rm -rf build-dir repo output
flatpak-builder --user --force-clean --repo=repo build-dir "$MANIFEST"
mkdir -p output
flatpak build-bundle repo "output/Segra.flatpak" "$APP_ID"

echo ""
echo "=== Done ==="
echo "Bundle: $SCRIPT_DIR/output/Segra.flatpak"
echo "Install/run:"
echo "  flatpak install --user ./output/Segra.flatpak"
echo "  flatpak run $APP_ID"
