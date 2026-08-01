# STFC Community Mod Launcher for Windows

This directory contains the Windows launcher parity+ implementation. `WL-001`
established the build, test, high-DPI shell, per-user ownership model, signing,
and self-contained packaging shape. The integrated launcher now includes
bounded installation discovery, explicit `prime.exe` validation,
user-confirmed selection, composable health, the accepted compact Light/Dark
Home, a schema-driven Settings workspace, and verified transactional mod
deployment.

## Projects

- `STFCCommunityMod.Launcher` — .NET 8 WPF shell for Windows x64.
- `STFCCommunityMod.Launcher.Setup` — signed one-file per-user installer and
  launcher entry point.
- `STFCCommunityMod.Launcher.Core` — UI-independent launcher contracts and
  platform services.
- `STFCCommunityMod.Launcher.Core.Tests` — deterministic unit tests with no
  installed STFC dependency.

The Settings workspace loads the generated Guffawaffle schema embedded in the
launcher package. It provides search, category filtering, source identity,
defaults, concrete scalar/hotkey/notification editors, and a revisioned
Save/Discard session over the source-preserving TOML/atomic-write core.

## Build and test

```powershell
dotnet restore windows-launcher/STFCCommunityMod.Launcher.sln
dotnet test windows-launcher/STFCCommunityMod.Launcher.sln -c Release
dotnet publish windows-launcher/src/STFCCommunityMod.Launcher/STFCCommunityMod.Launcher.csproj `
  -c Release -r win-x64 --self-contained true
```

The repository `global.json` selects .NET 8. The launcher publish is
self-contained and does not require a machine-wide .NET runtime.

## Package the launcher

```powershell
./windows-launcher/scripts/publish.ps1
```

The script writes an unpackaged per-user payload, an internal self-update ZIP,
a SHA-256 sidecar, a small launcher manifest, and the user-facing single-file
`setup/STFCCommunityMod.Launcher.Setup.exe` under `windows-launcher/artifacts/`.
Release users download only the setup executable; it installs without
elevation, creates a Start-menu shortcut, and starts the launcher. Ordinary PR
artifacts are unsigned build evidence and are intentionally rejected by the
production install trust checks.

## Current safety boundary

The launcher reads the official launcher's exact `GAME_PATH` setting, validates
bounded conventional or user-selected folders, and persists a confirmed
selection under launcher-owned state. It can edit the selected installation's
TOML through an explicit revisioned Save/Discard session.

Mod installation and update are explicit, game-closed transactions. The
launcher accepts only the canonical Windows artifact selected from the signed
release contract, verifies its HTTPS response, size, SHA-256, Authenticode
publisher, and embedded version, and commits only `version.dll` through a
persistent same-volume rollback journal. A pre-existing manual DLL is never
silently claimed; the player must explicitly adopt it and its previous bytes
are preserved. Launch handoff, recovery, verified repair, allowlist-only
uninstall, previewed redacted diagnostics, and signed replace-on-exit launcher
self-update are available.

See [the architecture decision](../docs/windows-launcher/ARCHITECTURE_SPIKE.md),
[the discovery and health contract](../docs/windows-launcher/DISCOVERY_AND_HEALTH.md),
[the product contract](../docs/windows-launcher/CONTRACT.md), and
[the Windows signing policy](../docs/windows-launcher/CODE_SIGNING.md).
The accepted compact home, adaptive settings workspace, notification-scale
requirements, themes, diagnostics boundary, and directional mockup are in
[the UX direction](../docs/windows-launcher/UX_DIRECTION.md).
The configuration editor's current safety boundary and next weave are in
[the configuration editor notes](../docs/windows-launcher/CONFIG_EDITOR.md).
