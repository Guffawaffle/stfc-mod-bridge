# STFC Community Mod Launcher for Windows

This directory contains the Windows launcher parity+ implementation. `WL-001`
established the build, test, high-DPI shell, per-user ownership model, signing,
and self-contained packaging shape. The integrated launcher now includes
bounded installation discovery, explicit `prime.exe` validation,
user-confirmed selection, composable read-only health, the accepted compact
Light/Dark Home, and the first schema-driven Settings workspace.

## Projects

- `STFCCommunityMod.Launcher` — .NET 8 WPF shell for Windows x64.
- `STFCCommunityMod.Launcher.Core` — UI-independent launcher contracts and
  platform services.
- `STFCCommunityMod.Launcher.Core.Tests` — deterministic unit tests with no
  installed STFC dependency.

The Settings workspace loads the generated Guffawaffle schema embedded in the
launcher package. Its current vertical slice provides search, category
filtering, source identity, defaults, raw-file access gating, and the
source-preserving TOML/atomic-write core. Concrete scalar, hotkey, and
notification editors plus staged Save/Discard integration remain in `WL-006`;
disabled actions stay visibly unavailable rather than acting as no-ops.

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

The script writes an unpackaged per-user payload, a ZIP, a SHA-256 sidecar, and
a small launcher manifest under `windows-launcher/artifacts/`.

## Current safety boundary

The current launcher is deliberately read-only with respect to the game
installation. It may inspect whether `prime.exe` is running, read the official
launcher's exact `GAME_PATH` setting, validate bounded conventional or
user-selected folders, persist a confirmed selection under launcher-owned
state, load the configuration catalog, and open an existing TOML file through
an explicit user action. The tested TOML engine is not wired to UI mutation
until the staged Save/Discard session is in place. Deployment, update, repair,
configuration save, and launch mutations belong to their dependent work items.

See [the architecture decision](../docs/windows-launcher/ARCHITECTURE_SPIKE.md),
[the discovery and health contract](../docs/windows-launcher/DISCOVERY_AND_HEALTH.md),
[the product contract](../docs/windows-launcher/CONTRACT.md), and
[the Windows signing policy](../docs/windows-launcher/CODE_SIGNING.md).
The accepted compact home, adaptive settings workspace, notification-scale
requirements, themes, diagnostics boundary, and directional mockup are in
[the UX direction](../docs/windows-launcher/UX_DIRECTION.md).
The configuration editor's current safety boundary and next weave are in
[the configuration editor notes](../docs/windows-launcher/CONFIG_EDITOR.md).
