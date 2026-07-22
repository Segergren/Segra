# Contributing to Segra

A quick, practical guide to get you developing on both the backend (C#/.NET) and the frontend (React/Vite).

## Proposing New Features
- Before building a new feature, open a GitHub issue describing it and wait for it to be accepted by a maintainer.
- This avoids wasted effort: PRs that add features without an approved issue may be closed if the feature isn't a fit for the project's direction.
- Bug fixes and small improvements don't require a prior issue, though one is still welcome for anything non-trivial.

## Requirements
- Windows 10 (build 19041 / version 2004) or newer, **or** a modern Linux distro (see below)
- .NET SDK 10.0.x
- Git
- Node.js 20+ and npm (for frontend tooling, git hooks, and the frontend dev server)
- IDEs (pick what you like):
  - Visual Studio Code + C# Dev Kit OR Visual Studio

### Platform targets
The project multi-targets `net10.0-windows10.0.19041.0` (Windows) and `net10.0` (Linux). Game-capture,
HDR, the WinRT OCR game integrations, and Game Mode are Windows-only; Linux records via OBS/PipeWire
desktop capture.

### Building
Use `./build-local.sh` and pick **Windows** or **Linux** with the Up/Down arrows (or set
`SEGRA_BUILD_TARGET=windows|linux` to skip the menu). The Linux target produces a runnable `publish/`
for quick local dev (launch `publish/run.sh`).

The Linux **distributable is a Flatpak** — one artifact for every distro, with OBS bundled inside. Build
it with `./build-flatpak.sh` (needs `flatpak` + `flatpak-builder`); it produces `output/Segra.flatpak`.
Windows still ships via Velopack (`vpk`).

A `linux-x64` dev build can be cross-compiled from Windows, but running/recording needs a Linux host with:
`libwebkit2gtk-4.1`, `gtk3`, `pipewire` + `wireplumber`, `xdg-desktop-portal` (+ a backend), `zenity`,
`pulseaudio-utils`, `x11-xserver-utils`, `xclip`, `ffmpeg`, and `gstreamer1.0-libav` +
`gstreamer1.0-plugins-{good,bad}` (for in-app H.264/AAC playback). The Flatpak gets all of this from its
runtime, so an end user installing the Flatpak needs none of it. Display capture uses `xshm_input` on X11
and the PipeWire portal source on Wayland.

### Linux packaging (Flatpak)
The Flatpak (`packaging/flatpak/tv.segra.Segra.yml`) targets the GNOME 47 runtime and bundles the app
plus the OBS recorder (`lib/` + `obs-plugins/` + `data/` + the two helpers) next to the `Segra` binary at
`/app/segra`. A launcher (`packaging/flatpak/segra.sh`) exports `SEGRA_OBS_*` + `LD_LIBRARY_PATH`, which
makes `LinuxObsRuntime` resolve the bundled OBS with no download and no re-exec. FFmpeg comes from the
`org.freedesktop.Platform.ffmpeg-full` runtime extension.

The OBS binaries are assembled by `Obs/build-linux-bundle.sh <version>` from OBS Studio's **official
Ubuntu-24.04 `.deb`** on GitHub releases. The 24.04 base is deliberate: OBS built there needs at most
`GLIBC_2.34` and links FFmpeg 6, so it loads under the runtime; a 26.04 build referenced `GLIBC_2.43` and
`libavcodec.so.62` and would not `dlopen`. The same script refreshes
`packaging/linux/obs-helpers/{obs-nvenc-test,obs-ffmpeg-mux}` — libobs resolves these next to the
**running executable** (`readlink /proc/self/exe` → `dirname`), so `build-flatpak.sh` places them beside
`Segra`. Without `obs-nvenc-test`, NVENC is reported unsupported; without `obs-ffmpeg-mux`, recordings and
replay saves never mux to disk.

> Note: the app still contains the older runtime-download path (`CheckIfExistsOrDownloadAsync`) and
> Velopack update logic, which are dormant inside the Flatpak (OBS is found via the bundled `appDir`
> layout, and Flatpak handles updates). They remain as the fallback for a raw `publish/` dev run.

## Repo Layout
- `Segra.sln` — solution root
- `Backend/` — app services, models, utils
- `Frontend/` — React + Vite app (TypeScript, Tailwind, DaisyUI)

## First-Time Setup
1. Clone the repo
   - `git clone <your-fork-or-upstream> && cd Segra`
2. Install root dev tools (husky/lint-staged for hooks)
   - `npm install` (also runs `prepare` to set up husky)
3. Install frontend deps
   - `cd Frontend && npm install && cd ..`
4. Ensure .NET SDK 10 is on PATH
   - `dotnet --info` should show `Version: 10.x` and `OS: Windows`

## Developing
There are two parts running during development: the backend (Photino.NET desktop app) and the frontend (Vite dev server on port 2882).

### Start the Frontend (Vite)
- `cd Frontend && npm run dev` (serves on http://localhost:2882)

### Start the Backend (.NET)
- From the repo root:
  - `dotnet run --project Segra.csproj`
- Notes:
  - In Debug mode the app expects the frontend on `http://localhost:2882`.
  - If Node/npm is installed, the backend attempts to auto-run `npm run dev` in `Frontend/` if nothing is listening on 2882.

## Building
- Backend (Release): `dotnet build -c Release`
- Backend publish (self-contained optional): `dotnet publish -c Release`
- Frontend (bundle): `cd Frontend && npm run build`

## Linting & Formatting
- EditorConfig is enforced across the repo:
  - Global: CRLF line endings and 2-space indent
  - C#: CRLF line endings, 4-space indent
- C# formatting (via `dotnet format`):
  - Pre-commit: runs `dotnet format` on the solution when `*.cs` files are staged
  - Pre-push: verifies no formatting drift in the solution
- Frontend (in `Frontend/`):
  - Prettier + ESLint
  - Scripts:
    - `npm run format` / `npm run format:check`
    - `npm run lint` / `npm run lint:fix`

## Git Hooks (Husky + lint-staged)
- Installed at repo root via npm.
- Pre-commit:
  - Prettier + ESLint on staged files in `Frontend/`
  - `dotnet format` on the solution when `*.cs` files are staged
- Pre-push:
  - `dotnet format --verify-no-changes` on the solution

If hooks don't run:
- Ensure Node.js/npm is on PATH for your Git shell
- Re-run: `npm install` (re-runs `prepare`/husky)

## Pull Requests
- For new features, link the approved issue your PR addresses
- Keep PRs focused and small
- Run format and lint before pushing

Thanks for contributing ❤️
