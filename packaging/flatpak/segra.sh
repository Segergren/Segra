#!/bin/sh
# Segra Flatpak launcher (installed as /app/bin/segra, the manifest `command`).
# Points the app at the bundled OBS runtime, which skips LinuxObsRuntime's download/re-exec.
export SEGRA_OBS_MODULE_PATH=/app/segra/obs-plugins
export SEGRA_OBS_MODULE_DATA_PATH=/app/segra/data/obs-plugins/%module%
export SEGRA_OBS_DATA_PATH=/app/segra/data/libobs

# /app/segra/lib first, so libobs's own FFmpeg 6 wins over the runtime's FFmpeg 7.
export LD_LIBRARY_PATH="/app/segra/lib:/app/segra${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"

# WebKit's DMA-BUF renderer fails under NVIDIA + the sandbox ("Failed to create GBM buffer"); force software.
export WEBKIT_DISABLE_DMABUF_RENDERER=1

# Flatpak falls back to UTC when the host's /etc/localtime isn't a direct link into $TZDIR (common on
# NixOS), so take the zone from the host's fully resolved link instead. A zone name, not a path, since
# WebKit's JavaScript dates only understand names.
if [ -z "$TZ" ] && [ "$(date +%Z)" = "UTC" ]; then
    zone=$(timeout 5 flatpak-spawn --host --directory=/ readlink -f /etc/localtime 2>/dev/null | sed -n 's|.*/zoneinfo/||p')
    if [ -n "$zone" ] && [ -f "/usr/share/zoneinfo/$zone" ]; then
        export TZ=":$zone"
    fi
fi

exec /app/segra/Segra "$@"
