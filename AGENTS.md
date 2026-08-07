# Repository working agreement

This repository owns the Windows launcher, installer, updater, provider-pack
contracts, launcher documentation, and launcher release automation.

The C++ mod runtime and the macOS launcher are outside this repository. Do not
copy or move them here. Mod repositories remain authoritative producers of
their runtime manifests, configuration schemas, and mod release artifacts.

## Branches and delivery

- Branch from current `main`.
- Every non-default branch name must end in a GitHub issue number, for example
  `feature/provider-catalog-12`.
- If no issue exists, discover or create one before branching.
- Keep commits signed.
- Return changes through a pull request to `main`.

## Verification

For launcher changes, run the narrowest relevant tests and, before handoff:

```powershell
dotnet test STFCCommunityMod.Launcher.sln -c Release
git diff --check
```

For packaging changes, also run `./scripts/publish.ps1` and verify the
`.appinstaller` descriptor is the user-facing install artifact, the signed
MSIX contains only reviewed package executables, and the standalone ZIP remains
explicitly labeled as a fallback artifact.

## Safety boundary

- Keep provider-specific behavior behind stable provider IDs and catalog data;
  do not branch on display names.
- Preserve staged Save/Discard semantics for configuration and Data Sync.
- Treat mod install/update/repair and launcher self-update as independent trust
  domains.
- Do not add network mutation or destructive game-file operations without an
  explicit issue, transaction design, rollback, and focused tests.
