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
`SEGRA_BUILD_TARGET=windows|linux` to skip the menu). On Linux with the Velopack CLI installed
(`dotnet tool install -g vpk`), the script also produces a `.AppImage` under `output/`.

A `linux-x64` build can be cross-compiled from Windows, but running/recording needs a Linux host with:
`libwebkit2gtk-4.1`, `gtk3`, `pipewire` + `wireplumber`, `xdg-desktop-portal` (+ a backend), `zenity`,
`pulseaudio-utils`, `x11-xserver-utils`, `xclip`, `ffmpeg`, and `gstreamer1.0-libav` +
`gstreamer1.0-plugins-{good,bad}` (for in-app H.264/AAC playback). `xrandr`/`pactl`/`xclip` are optional
and no-op if missing. Display capture uses `xshm_input` on X11 and the PipeWire portal source on Wayland.

### Linux OBS runtime
Segra resolves its recorder at launch (`Backend/Platform/Linux/LinuxObsRuntime.cs`), preferring a bundle
downloaded from the API, then one shipped with the app, then a system `obs-studio` install, and re-execs
once to apply `LD_LIBRARY_PATH`. The download client queries
`https://segra.tv/api/obs/versions?isLinux=true` (override with `SEGRA_OBS_VERSIONS_URL`).

Bundles are built by `Obs/build-linux-bundle.sh <version>`, which assembles them from OBS Studio's
**official Ubuntu-24.04 `.deb`** on GitHub releases. The 24.04 base is deliberate: a bundle inherits the
glibc/FFmpeg floor of the distro it was built on, so building on 24.04 (glibc 2.39, FFmpeg 6) loads on
24.04 LTS and everything newer. Building on a bleeding-edge base does not — a bundle built on 26.04
referenced `GLIBC_2.43` and `libavcodec.so.62` and could not be `dlopen`'d on 24.04 at all. Always target
the oldest release Segra supports.

The same script also refreshes `packaging/linux/obs-helpers/{obs-nvenc-test,obs-ffmpeg-mux}`. These two
subprocess helpers are resolved by libobs next to the **running executable** (`readlink /proc/self/exe` →
`dirname`), not in the OBS bundle, so they are shipped *with the app* (`build-local.sh` copies them beside
`Segra` in the AppImage; `build-deb.sh` into `/opt/segra`). Without `obs-nvenc-test`, NVENC is reported
unsupported; without `obs-ffmpeg-mux`, recordings and replay saves never mux to disk.

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
