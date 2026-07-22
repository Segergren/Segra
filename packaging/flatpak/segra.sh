#!/bin/sh
# Segra Flatpak launcher (installed as /app/bin/segra, the manifest `command`).
#
# Points the app at the OBS runtime bundled inside the Flatpak. Setting SEGRA_OBS_DATA_PATH also makes
# LinuxObsRuntime.ConfigureAndReexecIfNeeded() return immediately — no runtime download, no re-exec — so
# /proc/self/exe stays /app/segra/Segra and libobs finds obs-nvenc-test / obs-ffmpeg-mux right beside it.
export SEGRA_OBS_MODULE_PATH=/app/segra/obs-plugins
export SEGRA_OBS_MODULE_DATA_PATH=/app/segra/data/obs-plugins/%module%
export SEGRA_OBS_DATA_PATH=/app/segra/data/libobs

# libobs, its unversioned aliases, and OBS's bundled media deps (FFmpeg 6, x264, …) live in
# /app/segra/lib — put it FIRST so they win over the runtime's FFmpeg 7.
export LD_LIBRARY_PATH="/app/segra/lib:/app/segra${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"

# WebKit's DMA-BUF renderer fails to allocate GPU buffers under NVIDIA + the Flatpak sandbox
# ("Failed to create GBM buffer"), leaving a blank window. Force the software path — the UI is a light
# dashboard, so there's no visible cost, and it renders on every GPU/driver.
export WEBKIT_DISABLE_DMABUF_RENDERER=1

exec /app/segra/Segra "$@"
