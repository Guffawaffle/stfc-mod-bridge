# Named profiles: current return point

Status: 2026-09-30 development implementation checkpoint. The
[canonical Profiles catalog contract](https://github.com/Guffawaffle/stfc-profiles/blob/main/docs/PROFILE_CATALOG_CONTRACT.md),
[shared game installation contract](https://github.com/Guffawaffle/stfc-profiles/blob/main/docs/GAME_INSTALLATION_CONTRACT.md)
and [Bridge integration contract](SHARED_INSTALL_PROFILE_CONTRACT.md) own the
accepted direction. Earlier Bridge-owned registry and per-installation selector
notes are superseded. Their historical commit receipts do not authorize restoring
those designs.

## Implemented development source

Bridge uses the shared native catalog, with only its selected immutable ID kept
privately. Create/edit, active/archive listing, whole-directory archive/restore,
profile configuration/data leases and isolation-ready named launch all route to
STFC Profiles. Selected profile Settings and Data Sync target its own staged
`config.toml`; encrypted verified backups travel inside that profile directory.
Ordinary launches retain ordinary preferences, with shared native installation
access held through the exact spawned game process lifetime.

Game client status/check/update/recover use the same native transport. The dialog
shows the exact confirmed installation, checked target version and real native phase/byte
progress. Updating requires stopped installation access and Bridge operation
exclusion. The engine owns payload validation, commit journals and recovery;
Bridge does not contain a separate downloader or patcher. A missing executable
does not erase the confirmed status/recovery target; launch validity stays
separate. Physical path aliases use the existing directory-identity boundary.
Settings retain their construction target across external profile and reviewed
runtime changes, preserve drafts and admitted saves, and recompose after
discard/completion. Raw TOML uses that same binding and resolves it again before
opening. Restoring a profile does not revive a draft against an older runtime
revision; unchanged later runtime evidence still reconciles deferred work.

The source dogfood entrypoint and ZIP/MSIX release paths include the exact
hash-paired native DLL. Default dependency builds use an immutable source pin;
an explicit local source override records revision/dirty state. The reviewed
signed Profiles source is `6b15a352c445efb817634e8ef6be3de4d40818f8`, with
immutable archive SHA-256
`7ac8a6d6494f766b287c1ff031ce4348290a619b34ca353caf52b50a7c4488c4`.
The default archive build records no development override or dirty input.

## Evidence and next work

The active Bridge checkout remains `feature/named-launch-profiles-225` on draft
[PR227](https://github.com/Guffawaffle/stfc-mod-bridge/pull/227). Reviewed signed
implementation checkpoint is recorded at `649856786b8976ad0b5ee990e465a4dfeed3b437`. Later correction heads are listed in the PR.
Independent general, hostile and package reviews and correction deltas have no
unresolved actionable findings. Read the current Git/PR head before resuming;
this recorded checkpoint is evidence, not permission to restore an older tree.
SDK alignment and reachable Go vulnerability repair were completed earlier.

The required full managed solution passed at that recorded implementation:
Core 1,226 passed / 8 skipped, WPF 290 passed, and local integration 53 passed /
8 skipped. Three real native ABI cases passed in the ordinary Explorer desktop
namespace at unchanged product implementation `d008b2de` against the same
immutable native build.
The new DLL SHA-256 is
`779f3e53cb96567ed4e6047456d8c14c87be6cd6b3a56ad30cf7472f842c7b04`.

Required publish and unsigned ZIP/MSIX/App Installer inspection pass with that
exact paired DLL, full GPL text and notices. Locked NuGet audit, Defender scan
with remediation disabled, and payload-only SBOM checks pass. The SBOM retains
all 71 exact Go modules and the explicitly labeled native source/recipe
inventory; it does not claim independently discovered native object provenance.
The production signature gate correctly refuses these unsigned development
artifacts. Gate project discovery uses the reviewed solution, and component
scan excludes historical worktrees/artifacts so they cannot become release
subjects. Bounded command receipts and historical dirty ownership are under
`artifacts/`. Exact-head publish/inspection also passed at the signed implementation.
Later changes require appropriate validation and independent review. Documentation
does not qualify a different artifact or manufacture current-head CI success.

The first clean-host package run passed readiness and standalone physical
catalog/config/native lease verification, then failed when its PID-associated
Process could not read ExitCode without an owned handle. The signed correction
retains SafeHandle through waiting and uses an explicit exit-code getter. Exact
nonzero rejection, nonce/package/profile binding and release-marker/cleanup gates
remain mandatory; real external-child exit0/7 regressions pass. That partial run
did not qualify packaged release, synthetic cleanup or UI smoke. The corrected run qualified installed MSIX catalog/config/native leases, release and synthetic cleanup, then caught a normal-window startup crash: ProgressBar.Value defaults to two-way binding but updater ProgressPercent is read-only. The corrected visual explicitly binds one-way. A real WPF control loaded from the production XAML reproduces the original exception and, with the correction, reads telemetry and clears it when the target changes. The installed-package protocol returns before normal window creation, so it does not substitute for UI smoke. Read the executed
steps and receipts in [implementation CI36699877347](https://github.com/Guffawaffle/stfc-mod-bridge/actions/runs/36699877347)
and current PR checks for the latest outcome.

Windows package context requires separate physical storage qualification. The
OS-user Known Folder string and absence of package identity do not prove that
an opened file is in the ordinary desktop namespace: Codex descendants were
observed opening that path inside private LocalCache storage. The shared core
now rejects redirected physical default roots and checks the actual opened
shared lifecycle-lock handle before native acquisition succeeds.
Bridge declares the Windows 10 filesystem-virtualization fallback and the
Windows 11 exact `STFC Profiles` exclusion. Its synthetic package gate verifies
metadata/config bytes through opened handles and proves shared leases between
standalone and MSIX processes. Fake transport/interop tests pass; actual
packaged cross-host catalog/lease execution passed in CI36699877347 at the recorded implementation. Current-head package and UI smoke remain separate checks. The existing local
Bridge installation remains untouched. The Windows 11 policy also preserves the
existing external `STFC Mod Bridge` state directory for standalone recovery,
Battle state sharing and uninstall retention. Bridge-owned UI selection is not
shared catalog ownership; it does not require package-private filesystem storage.
The existing Battle gate retains its unpackaged observation of a fresh nonce
marker in neutral Bridge state.

The producer repaired the standalone stack-overflow crash and namespace issue.
Its exact signed source passes Windows and both macOS native CI lanes. Parent
qualification reports two healthy standalone profiles from the same dev
executable, unique encrypted preference stores, and busy refusal for duplicate
launch/archive/update/recovery while those sessions run. The authorized dev
client update from 221 to 267 has a verified retained receipt. Full-mod sessions
pass those same guards using the original IDs and encrypted stores. These producer
observations do not establish live Bridge account sign-in or callback behavior.

Complete account identity, reverse restart persistence, browser callback and
packaged cross-host qualification before claiming the full shared-install
player workflow is qualified.

## Settings correction after MVP qualification

The ProgressBar correction at `8de866ac5512a9df6acf72aa2e44547b322674b3`
passed [CI36703488983](https://github.com/Guffawaffle/stfc-mod-bridge/actions/runs/36703488983),
including installed MSIX catalog/lease qualification and both provider UI smoke
stages. This supersedes the earlier current-head CI-pending statement above;
Windows 11 scoped exclusions and real account/callback qualification remain open.

A subsequent Settings report exposed two existing defects: an unsupported
configuration disabled the entire list and scroller, allowing WPF to paint a
white disabled surface; and the mod's own TOML writer emits simple quoted keys
that Bridge previously rejected. Settings and Data Sync now retain scrolling
and help while mutation controls stay gated. Disabled Settings text editors
preserve the chosen palette, and unavailable configuration captions distinguish
provider defaults from loaded values and report safe line metadata.

The parser accepts assignment-only quoted ASCII bare identifier segments,
normalizes their identity for duplicate and namespace checks, and preserves the
original key spelling and file formatting during staged saves. Quoted table
headers and more complex quoted keys remain unsupported. Retained Settings
rows refresh editing availability on target/runtime changes without clearing
drafts. The reported live configuration was accepted without a byte change;
focused staged-save, canonical-alias, real WPF scrolling and dark/light palette
regressions passed. Read the latest PR head and its exact validation/review
receipts before qualifying a corrected package or restoring any checkpoint.
