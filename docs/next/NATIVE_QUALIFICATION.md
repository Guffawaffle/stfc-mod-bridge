# Native qualification prerequisites

Status: br-00 prerequisite contract, 2026-10-03. Implementation is tracked by
[epic #231](https://github.com/Guffawaffle/stfc-mod-bridge/issues/231) and
[br-00 #232](https://github.com/Guffawaffle/stfc-mod-bridge/issues/232).
The dependency graph is [work-packages.json](../plans/rust-tauri-cross-platform/work-packages.json).
This document records observations and required evidence; it does not qualify a
release or turn a proposed gate into an executable command.

## Required host and process matrix

The first release supports Windows x64 and Apple Silicon macOS. Intel Mac host
support is outside this release. Windows and macOS require independent build,
package, runtime and user-interface qualification.

| Component | Windows | Apple Silicon macOS |
| --- | --- | --- |
| Bridge shell/backend | Native x64 | Native arm64 |
| Profiles/TOML libraries loaded into Bridge | Match the Bridge process architecture | Match the arm64 Bridge process |
| Selected game executable | Inspect the exact executable and running process | Inspect actual Mach-O slices and process execution architecture |
| Runtime injected into the game | Match the game process and selected client | Match the actual game process, which must be observed independently of Bridge |
| Loader/browser guardian/CLI companions | Verify selected executable, dependencies and launch contract | Verify each companion's architecture, signed bytes and actual execution route |

br-00 records the supported game/runtime route before release packaging decisions.
An arm64 Bridge build does not establish that the selected game is arm64. If a
selected game runs x86_64 under Rosetta, br-09/br-10/br-29 must qualify that exact
translated game, loader, injected runtime and browser route on Apple Silicon.
Translation coverage does not reintroduce Intel host support or establish native
arm64 game coverage. A universal binary requires evidence for each shipped slice;
its presence alone does not prove either slice ran.

## Observed native execution routes

The following read-only observations were collected on 2026-10-03:

| Route | Exact evidence | Established boundary |
| --- | --- | --- |
| Profiles Apple Silicon CI | Source `aeef4861693613fab119edcd03117b4e8de01d4b`; [run 37095877830](https://github.com/Guffawaffle/stfc-profiles/actions/runs/37095877830); successful arm64 job `111125576567`, runner label `macos-15` | Native compilation and the existing synthetic verification suite |
| Mod Apple Silicon CI | Source `f07a113dcaf5cd0e25778e81ec324fdb0a9213f8`; [run 36978379006](https://github.com/Guffawaffle/stfc-mod/actions/runs/36978379006); successful `build-mac (arm64)` job `110747115691`, runner label `macos-26`; successful `package-mac` job `110749416912` | Existing producer compilation and packaging |
| Persistent game/test executor | Repository runner APIs returned zero self-hosted runners for Guffawaffle Profiles, mod and Bridge. netniV runner inventory returned HTTP 403. Codex's connected project inventory exposed local Windows only; no Mac SSH alias was found. | No live Mac game/test environment or canonical native checkout is bound to this session; netniV's private runner inventory remains unknown |
| Mod signing/notarization | Guffawaffle/stfc-mod has `macos-release`, certificate/notary secret-name metadata and Apple identity variable-name metadata. Existing signing receipts record accepted notarization. The environment requires Guffawaffle review and restricted branch/tag selection. | An existing mod signing route is provisioned; secret values were not read |
| Bridge Mac signing | Guffawaffle/stfc-mod-bridge exposed only its existing `windows-release` environment and no repository-level secret names. | No Bridge Mac signing/notary binding was established by this inspection; other provisioning mechanisms were not proven absent |

GitHub-hosted native Mac CI is available. Its successful jobs do not establish
live game loading, saved-account correctness, sign-in callbacks, permission
consent, installed WKWebView behavior or release qualification for new code.
Existing mod signing credentials and approval scope do not automatically bind a
different Bridge bundle, channel or repository.

Before br-23 release signing, record the Bridge bundle/package identity,
publisher/team/signing identity, update channel, notarization route and approved
credential binding. Keep credentials in the approved secret store; receipts
contain identity metadata and artifact hashes, never credential values. Follow
the configured environment's approval rules at execution time.

## Native checkout and target binding

Before preparing a native execution packet, bind:

1. The approved CI job or live executor, OS version and physical host architecture.
2. The owning repository URL, actual native absolute checkout path, exact source
   revision and dirty-state observation. Windows paths in planning artifacts are
   ownership references, not Mac cwd values.
3. Toolchain/dependency versions, build mode and target triple/architecture.
4. For runtime work, the explicitly selected game installation, physical
   identity, client/bundle version, executable hash, requested profile kind/ID
   and exact process identity when addressing a running session.
5. Runtime/loader/native-companion hashes, code-signing identity and observed
   process architecture, including translation when applicable.

An ephemeral CI checkout may bind native compilation and synthetic tests.
A live-game packet additionally needs an approved game/test environment and
explicit target. Do not invent a Mac path, default installation or tester result.
No instruction to configure a new account or copy real account state follows
from a successful build. Resource identity must be rechecked under the owning
operation's exclusion when the operation executes.

## Available baseline commands

These commands already exist in the owning repositories. Run each from its
canonical checkout on the matching native host. They are baseline checks, not
acceptance of the Rust rewrite:

| Owner / host | Existing command | Coverage and limitation |
| --- | --- | --- |
| Profiles / Windows x64 | `pwsh -NoLogo -NoProfile -File ./scripts/Test.ps1 -Platform windows -Architecture x64` | Synthetic identity/preferences/catalog/installation/import/CLI tests; adapter/runtime and independent consumer build |
| Profiles / Apple Silicon | `pwsh -NoLogo -NoProfile -File ./scripts/Test.ps1 -Platform macosx -Architecture arm64` | Native core/catalog/preferences and consumer tests plus adapter/runtime build; existing script does not run Mac live loading/updater/account qualification |
| Mod / Apple Silicon | `bash scripts/mac-build-test-debug.sh -a arm64 -m release build` | Existing native producer build; no game launch or gameplay claim |

The Profiles Mac CI fixture `tests/macos_keychain_fixture.sh` requires an
ephemeral GitHub Actions Mac runner and restores its test Keychain state. Do not
apply its setup to a developer's login Keychain. Use synthetic preference and
browser fixtures; do not copy real accounts into test artifacts.

New `scripts/next/qualify.mjs`, Profiles
`scripts/Qualify-CrossPlatform.ps1`, producer `scripts/Qualify-MacLaunch.ps1`,
Cargo/frontend suites and their work-package commands are planned deliverables.
Their implementation, supported parameters and correct-host behavior must be
verified before execution. Missing scripts, wrong hosts or missing required
receipts are blocked evidence, never a successful placeholder gate. Old WPF
tests and existing producer CI are reference coverage, not Rust acceptance.

## Static game assets and source observations

An existing local official-game research asset is available at
`D:/dev/stfc-mod/.codex/ctrl-click-warp/mac-inputs/native/arm64/GameAssembly.dylib`.
It is a 211,439,360-byte ARM64 Mach-O image, SHA-256
`e8ccd34b705932456abe6dcff7d1248180bcd5193b7af14d7514c5a6c73d77c3`.
The adjacent historical review disposition attributes it to Mac client **197**,
bundle **1.000.52361**, obtained through the official distribution route.
It permits static ABI/entry analysis for that image, not a claim about today's
installed client, runtime behavior or current updater target. Proprietary images
and generated dumps remain ignored/local.

The prior Bridge feature branch's immutable native source pins are recorded in
[DEPENDENCY_OWNERSHIP.md](DEPENDENCY_OWNERSHIP.md). They are observations awaiting
review/adoption, not current Rust dependency pins or compatibility evidence.

## Required closure packages and receipts

| Package | Required closure |
| --- | --- |
| br-05 | Safe native ABI use and actual per-target module pairing; reviewed immutable inputs |
| br-07 | Actual Mac process/filesystem/permission/signature/platform services |
| br-08 | Profiles Mac installation identity/custody, ordinary setup kind and protected-state foundation |
| br-09 | Owning producer's explicit target/argument/environment/loading contract and compatible artifact |
| br-10 | Ordinary/named launch, exact readiness, writer/browser/installation lifetime and callback behavior |
| br-11 | Qualified Mac game-update route, checked target binding, admission and recovery |
| br-23 | Native package pairing, Bridge signing/notarization, identity and clean-host installation |
| br-27 / br-29 | Actual Windows / Apple Silicon packaged UI, accessibility and required live journeys |
| br-30 | Aggregation and independent review of exact accepted candidate evidence |

A receipt records the package/gate, owning repo, source revision and dirty-state
or candidate-content identity, command and cwd, start/end UTC times, duration,
exit code, bounded output, host/OS/toolchain, target architecture and exact final
artifact hashes. Runtime receipts add the client, selected installation/profile,
process executable/PID/start identity, observed execution architecture, loading
evidence and journey outcome. Package receipts identify the signed bytes and
signer/notarization evidence. Keep account values and browser credentials out.

Required live journeys include ordinary launch; two isolated IDs from one
installation; same-ID duplicate refusal; reverse restart persistence; sign-in
callbacks; stopped archive/restore; update exclusion and unresolved-journal
admission; retained operation ownership after UI closure; and recovery after
interruption. Readiness and a correct logged-in account are separate observations.
UI acceptance uses actual WebView2/WKWebView, keyboard/screen reader, native
menus/windows and display/text scaling. Browser-only mock tests prove the
frontend contract and interaction states, not installed native behavior.

Changed source, inputs or artifacts invalidate only affected evidence through
the dependency graph. Receipts for one OS, process architecture, client or
artifact cannot qualify another. A native build, signed package or passed plan
validation alone cannot close a required live journey.

The Windows `windows-private-journal-fixtures` suite collects a selected private
storage subset described in [PRIVATE_JOURNAL_STORAGE.md](PRIVATE_JOURNAL_STORAGE.md).
Its exact 38-name library inventory, 28 default cases, five ownership documents
and nine ignored native selections are distinct from the existing fourteen
Windows platform cases. It requires current compiler selection, privilege
context, bounded raw markers and normal-build exclusion evidence. The suite
cannot accept full `br-06`; the dispatcher refuses that package while required
native integration is partial. No installed game, production namespace, power-loss
durability, Apple Silicon storage or release qualification follows from these
Windows observations.
