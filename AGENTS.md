# Repository working agreement

This repository owns STFC Mod Bridge's application, installer, updater,
provider-pack contracts, documentation and release automation. The authorized
Rust rewrite targets Windows x64 and Apple Silicon macOS together; Intel Mac
host support is outside the initial release. See issue #231 and
`docs/next/OPERATING_CONTRACT.md` for the new application contract.

The C++ mod runtime and producer macOS loader remain outside this repository.
Do not copy or move them here. Producers own their runtime manifests,
configuration schemas, loading hooks and mod artifacts. STFC Profiles owns the
canonical catalog, stores, lifetime exclusions and game update routes; Bridge
consumes those native APIs rather than creating competing authorities.

## Branches and delivery

- Branch from current `main`.
- Every non-default branch name must end in a GitHub issue number, for example
  `feature/provider-catalog-12`.
- If no issue exists, discover or create one before branching.
- Keep commits signed.
- Return changes through a pull request to `main`.

## Verification

For Rust-stack changes, run each affected package's real qualification command
from this checkout and retain candidate-bound LexRunner gate evidence:

```powershell
node scripts/next/qualify.mjs --package br-00 --host any
```

Replace the package ID with the implemented package. Missing suites, wrong
native hosts and missing prerequisite evidence must block acceptance. Browser
mock, native build and installed live-game evidence are different boundaries.
Windows and Apple Silicon qualify independently. Root owns shared manifests,
lockfiles, generated contract registration, app composition and CI; workers
edit only their declared disjoint scopes.

The Rust engine/domain must not depend on Tauri, webviews or frontend types.
CLI and Tauri consume one versioned dispatcher contract. Frontend development
against typed mocks must work without Rust, native modules or a running game.
No C#/WPF implementation-preservation constraint applies to the rewrite.

For changes to the retained .NET product, run its relevant tests and before
handoff:

```powershell
dotnet test STFCCommunityMod.Launcher.sln -c Release
git diff --check
```

For retained WPF packaging changes, also run `./scripts/publish.ps1` and verify the
`.appinstaller` descriptor is the user-facing install artifact, the signed
MSIX contains only reviewed package executables, and the standalone ZIP remains
explicitly labeled as a fallback artifact. New-stack packaging needs its own
reviewed identity/signing/update pipeline. Do not send a new Rust release tag
through the existing WPF release/channel workflows before reviewed cutover.

## Safety boundary

- Keep provider-specific behavior behind stable provider IDs and catalog data;
  do not branch on display names.
- Preserve staged Save/Discard semantics for configuration and Data Sync.
- Capture explicit installation/profile/session targets for every operation;
  revalidate physical and revision identity under retained exclusions.
- Treat mod install/update/repair, official game updates and Bridge self-update
  as independent trust domains.
- Normal host exit must wait for admitted work to reach a safe durable state;
  forced process death requires journal recovery and cannot imply continuation.
- Do not add network mutation or destructive game-file operations without an
  explicit issue, transaction design, rollback, and focused tests.
