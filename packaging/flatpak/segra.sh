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

exec /app/segra/Segra "$@"
