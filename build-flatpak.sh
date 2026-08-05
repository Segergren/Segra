#!/bin/bash
# Builds the Segra Flatpak, one artifact for every distro.
#
#   SEGRA_VERSION=1.7.0 OBS_VERSION=32.2.0 ./build-flatpak.sh
#
# Requires: flatpak, flatpak-builder, dotnet 10 SDK, node, ffmpeg (installs the GNOME 47 runtime/SDK +
# ffmpeg-full from Flathub if missing).
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

VERSION="${SEGRA_VERSION:-1.0.0}"
OBS_VERSION="${OBS_VERSION:-32.2.0}"
# Exported so csproj's BuildFrontendAssets target stamps the frontend build with the same version.
export SEGRA_VERSION="$VERSION"
APP_ID="tv.segra.Segra"
MANIFEST="packaging/flatpak/${APP_ID}.yml"
STAGING="flatpak-staging"

command -v flatpak-builder >/dev/null 2>&1 || { echo "error: flatpak-builder not installed (apt install flatpak-builder)"; exit 1; }
command -v ffmpeg >/dev/null 2>&1 || { echo "error: ffmpeg not installed (apt install ffmpeg); its binary gets bundled into the payload"; exit 1; }

# Must run before staging, which enumerates the runtime's sonames to decide what OBS deps to bundle.
echo "=== Runtime/SDK (no-op if already installed) ==="
flatpak remote-add --if-not-exists --user flathub https://flathub.org/repo/flathub.flatpakrepo || true
flatpak install --user -y --noninteractive flathub \
    org.gnome.Platform//47 org.gnome.Sdk//47 org.freedesktop.Platform.ffmpeg-full//24.08 || true

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
# The ffmpeg CLI (clips/thumbnails/waveforms): neither the GNOME runtime nor the ffmpeg-full
# extension ships the binary, so bundle the host's, beside Segra where FFmpegService looks first.
cp -L "$(command -v ffmpeg)" "$STAGING/payload/ffmpeg"
chmod +x "$STAGING/payload/Segra" "$STAGING/payload/obs-nvenc-test" "$STAGING/payload/obs-ffmpeg-mux" "$STAGING/payload/ffmpeg"

# Bundle OBS's media deps (Ubuntu-24.04 FFmpeg 6/x264/jansson/rist/srt) that the runtime's FFmpeg 7 can't satisfy.
LIBDST="$STAGING/payload/lib"
# Bundle only sonames the GNOME runtime doesn't already provide, so glibc/GL/GTK/WebKitGTK stay runtime-supplied.
declare -A RUNTIME_PROVIDES
RT="$(flatpak info -l org.gnome.Platform//47 2>/dev/null || true)"
# Fail rather than warn: an empty inventory would silently bundle the entire ldd closure instead.
[ -n "$RT" ] && [ -d "$RT/files" ] || { echo "error: org.gnome.Platform//47 not installed; cannot determine which libraries to bundle"; exit 1; }
while IFS= read -r so; do RUNTIME_PROVIDES["$(basename "$so")"]=1; done \
  < <(find "$RT/files" -name '*.so*' 2>/dev/null)
[ "${#RUNTIME_PROVIDES[@]}" -gt 0 ] || { echo "error: runtime inventory is empty (looked in $RT/files)"; exit 1; }
echo "runtime provides ${#RUNTIME_PROVIDES[@]} sonames; bundling only what it lacks"
bundle_media_dep() {   # $1 = resolved host path
  local src="$1" base; base="$(basename "$src")"
  [ -n "${RUNTIME_PROVIDES[$base]:-}" ] && return 1   # runtime already ships this soname, don't bundle
  local real; real="$(readlink -f "$src")"
  cp -n "$real" "$LIBDST/$(basename "$real")" 2>/dev/null || true
  [ "$(basename "$real")" != "$base" ] && ln -sf "$(basename "$real")" "$LIBDST/$base"
  return 0
}
# Scan OBS's libraries AND the app's own native libraries (Photino.Native.so needs ICU 74, runtime ships 75).
LDD_OUT="$({ for f in "$LIBDST"/libobs*.so.*[0-9] "$STAGING/payload/obs-plugins/"*.so \
           "$STAGING/payload/"*.so \
           "$STAGING/payload/obs-nvenc-test" "$STAGING/payload/obs-ffmpeg-mux" \
           "$STAGING/payload/ffmpeg"; do
    [ -e "$f" ] || continue
    # .NET's optional LTTng tracing shim; only dlopen'd when tracing is enabled, fine unresolved.
    [ "$(basename "$f")" = libcoreclrtraceptprovider.so ] && continue
    ldd "$f" 2>/dev/null
  done; true; })"
grep -oE '=> /[^ ]+' <<<"$LDD_OUT" | awk '{print $2}' | sort -u | while read -r p; do
  bundle_media_dep "$p" || true
done
# A dep the host lacks shows as '=> not found' above and is silently skipped by the resolved-path grep
# (CI once shipped builds without libsrt.so.1.5/libmbedtls.so.14 this way). Fail unless the runtime or
# the payload itself (bundled libs reference each other, e.g. libobs.so) provides the soname.
MISSING=""
while IFS= read -r so; do
  [ -z "$so" ] && continue
  [ -n "${RUNTIME_PROVIDES[$so]:-}" ] && continue
  [ -e "$LIBDST/$so" ] || [ -e "$STAGING/payload/$so" ] || MISSING="$MISSING $so"
done < <(awk '$2 == "=>" && $3 == "not" && $4 == "found" {print $1}' <<<"$LDD_OUT" | sort -u)
[ -z "$MISSING" ] || { echo "error: unresolved plugin deps not bundled:$MISSING (install them on the build host and rebuild)"; exit 1; }
echo "bundled $(ls "$LIBDST" | grep -cvE '^libobs') media libs into payload/lib"
# Flatpak metadata + launcher + icon the manifest installs
cp packaging/flatpak/segra.sh "$STAGING/"
cp "packaging/flatpak/${APP_ID}.desktop" "$STAGING/"
sed -e "s/@VERSION@/$VERSION/" -e "s/@DATE@/$(date -u +%Y-%m-%d)/" \
    "packaging/flatpak/${APP_ID}.metainfo.xml" > "$STAGING/${APP_ID}.metainfo.xml"
# 256x256 PNG (the repo's icon.png is 1000x1000; Flatpak caps hicolor icons at 512x512).
cp packaging/flatpak/icon-256.png "$STAGING/icon-256.png"

echo "=== 4/4 flatpak-builder ==="
rm -rf build-dir repo output
flatpak-builder --user --force-clean --repo=repo build-dir "$MANIFEST"
mkdir -p output
flatpak build-bundle repo "output/Segra.flatpak" "$APP_ID"

# The same staged tree, as a tarball the Flathub manifest consumes by url + sha256.
PAYLOAD="output/segra-${VERSION}-x86_64.tar.gz"
tar czf "$PAYLOAD" -C "$STAGING" .
sha256sum "$PAYLOAD" | awk '{print $1}' > "$PAYLOAD.sha256"

echo ""
echo "=== Done ==="
echo "Bundle:  $SCRIPT_DIR/output/Segra.flatpak"
echo "Payload: $SCRIPT_DIR/$PAYLOAD"
echo "sha256:  $(cat "$PAYLOAD.sha256")"
echo "Install/run:"
echo "  flatpak install --user ./output/Segra.flatpak"
echo "  flatpak run $APP_ID"
