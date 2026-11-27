# Contributing to Volume Mixer

Thank you for your interest in contributing!

## Prerequisites

- **OS**: Windows 10 (build 22621+) or Windows 11
- **Desktop App**: [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- **Browser Extension**: Node.js 20+ (for syntax validation) and a Chromium-based browser (Chrome, Brave, Edge, Opera, Vivaldi)

## Local Development

### Build & Run Desktop App

```powershell
# Restore & build the solution
dotnet build VolumeMixer.sln -c Release

# Publish standalone single-file binary into a clean folder
dotnet publish VolumeMixer.App/VolumeMixer.App.csproj -c Release -r win-x64 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --self-contained false -o dist/publish
```

### Validate & Test Browser Extension

1. Validate JavaScript syntax and `manifest.json`:
   ```powershell
   node --check VolumeMixer.Extension/background.js
   node --check VolumeMixer.Extension/content.js
   node --check VolumeMixer.Extension/page_bridge.js
   node -e "JSON.parse(require('fs').readFileSync('VolumeMixer.Extension/manifest.json','utf8'))"
   ```
2. Open `chrome://extensions` (or `brave://extensions`), enable **Developer mode**, and click **Load unpacked** pointing to `VolumeMixer.Extension/`.
3. After modifying extension scripts, click the **Reload (`↻`)** button on the extension card and refresh affected open tabs (`F5`).

## Commit Convention

This project uses [Conventional Commits](https://www.conventionalcommits.org/):

- `feat(scope): ...` — new feature
- `fix(scope): ...` — bug fix
- `perf(scope): ...` — performance optimization
- `docs: ...` — documentation updates
- `ci: ...` — GitHub Actions / build workflow changes
- `chore: ...` — tooling, scaffolding, housekeeping

Common scopes: `app`, `audio`, `bridge`, `system`, `ws`, `ui`, `extension`.

## Pull Request Checklist

- `dotnet build VolumeMixer.sln -c Release` succeeds with `0` warnings and `0` errors.
- All extension scripts pass `node --check` and `manifest.json` parses cleanly.
- No hardcoded local paths, user-specific config files, or `bin/`/`obj/`/`publish/` artifacts are committed.
- UI changes preserve `WS_EX_NOACTIVATE` overlay behavior and avoid idle background polling when minimized to tray.
