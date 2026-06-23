# Contributing to Segra

A quick, practical guide to get you developing on both the backend (C#/.NET) and the frontend (React/Vite).

## Proposing New Features
- Before building a new feature, open a GitHub issue describing it and wait for it to be accepted by a maintainer.
- This avoids wasted effort: PRs that add features without an approved issue may be closed if the feature isn't a fit for the project's direction.
- Bug fixes and small improvements don't require a prior issue, though one is still welcome for anything non-trivial.

## Requirements
- Windows 10 (build 19041 / version 2004) or newer
- .NET SDK 10.0.x (Windows targeting)
- Git
- Node.js 20+ and npm (for frontend tooling, git hooks, and the frontend dev server)
- IDEs (pick what you like):
  - Visual Studio Code + C# Dev Kit OR Visual Studio

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
