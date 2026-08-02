# STFC Community Mod Launcher

Source-neutral Windows launcher, installer, configuration manager, and Data
Sync workspace for STFC community mods.

The launcher is a .NET 8 WPF application for Windows x64. It discovers and
validates an STFC installation, launches the official client, manages verified
mod artifacts transactionally, edits configuration through staged Save/Discard
sessions, and provides a destination-oriented Data Sync workspace.

## Repository boundary

This repository owns the Windows application and its release lifecycle. It
does **not** own the C++ mod runtime or the macOS launcher. A mod distribution
supplies versioned provider data—release location, runtime manifest,
configuration schema, capabilities, trust rules, and migration metadata—rather
than requiring a distribution-specific launcher build.

The first bundled provider is Guffawaffle. NetniV support is a provider-pack
integration target, not a fork of this application.

## Projects

- `STFCCommunityMod.Launcher` — WPF application.
- `STFCCommunityMod.Launcher.Setup` — one-file, per-user installer.
- `STFCCommunityMod.Launcher.Updater` — replace-on-exit update helper.
- `STFCCommunityMod.Launcher.Core` — UI-independent contracts and services.
- test projects under `tests/` — deterministic unit and WPF projection tests.

## Build and test

```powershell
dotnet restore STFCCommunityMod.Launcher.sln
dotnet test STFCCommunityMod.Launcher.sln -c Release
```

Double-click `run-launcher.cmd` to build and start the exact Release executable
from this checkout. A failed build remains visible and never launches stale
output.

## Package

```powershell
./scripts/publish.ps1
```

Package output is written under `artifacts/win-x64`. The setup executable is
the sole user-facing install artifact; the ZIP is an internal self-update
payload.

## Architecture and provenance

- [Repository extraction provenance](docs/EXTRACTION_PROVENANCE.md)
- [Provider-pack boundary](docs/PROVIDER_PACKS.md)
- [Product contract](docs/windows-launcher/CONTRACT.md)
- [UX direction](docs/windows-launcher/UX_DIRECTION.md)
- [Data Sync capability matrix](docs/windows-launcher/data-sync-capabilities.md)
- [Signing policy](docs/windows-launcher/CODE_SIGNING.md)
