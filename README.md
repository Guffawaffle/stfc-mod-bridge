# STFC Community Mod Launcher for Windows

This directory contains the Windows launcher parity+ implementation. The
current `WL-001` slice is an architecture spike: it proves the build, test,
high-DPI shell, per-user ownership model, and self-contained packaging shape
without modifying the game installation.

## Projects

- `STFCCommunityMod.Launcher` — .NET 8 WPF shell for Windows x64.
- `STFCCommunityMod.Launcher.Core` — UI-independent launcher contracts and
  platform services.
- `STFCCommunityMod.Launcher.Core.Tests` — deterministic unit tests with no
  installed STFC dependency.

## Build and test

```powershell
dotnet restore windows-launcher/STFCCommunityMod.Launcher.sln
dotnet test windows-launcher/STFCCommunityMod.Launcher.sln -c Release
dotnet publish windows-launcher/src/STFCCommunityMod.Launcher/STFCCommunityMod.Launcher.csproj `
  -c Release -r win-x64 --self-contained true
```

The repository `global.json` selects .NET 8. The launcher publish is
self-contained and does not require a machine-wide .NET runtime.

## Package the spike

```powershell
./windows-launcher/scripts/publish.ps1
```

The script writes an unpackaged per-user payload, a ZIP, a SHA-256 sidecar, and
a small architecture-spike manifest under `windows-launcher/artifacts/`.

## Current safety boundary

The spike is deliberately read-only. It may inspect whether `prime.exe` is
running and display the proposed per-user install location. Discovery,
deployment, update, repair, configuration, and launch mutations belong to
their dependent work items.

See [the architecture decision](../docs/windows-launcher/ARCHITECTURE_SPIKE.md)
[the product contract](../docs/windows-launcher/CONTRACT.md), and
[the Windows signing policy](../docs/windows-launcher/CODE_SIGNING.md).
