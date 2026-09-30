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
Settings repositories retain their construction target across external profile
changes, preserve drafts and admitted saves, and recompose after those finish.

The source dogfood entrypoint and ZIP/MSIX release paths include the exact
hash-paired native DLL. Default dependency builds use an immutable source pin;
an explicit local source override records revision/dirty state. The reviewed
signed Profiles source is `6b15a352c445efb817634e8ef6be3de4d40818f8`, with
immutable archive SHA-256
`7ac8a6d6494f766b287c1ff031ce4348290a619b34ca353caf52b50a7c4488c4`.
The default archive build records no development override or dirty input.

## Evidence and next work

The active Bridge checkout remains `feature/named-launch-profiles-225`, based on
signed implementation checkpoint `6915fc91820439e45918b4a6f09caf7ef8dd2350`.
Its independent general, hostile and package readings found recovery-target,
physical-alias, cached Settings and package evidence issues. Corrections and
focused regressions are applied; final committed correction review and CI remain
required. SDK alignment and reachable Go vulnerability repair were completed at
the earlier `0c707a0836ec7e8e5be31075d708f93efe56875d` checkpoint.

The required full managed solution passes on this development worktree:
Core 1,221 passed / 8 skipped, WPF 281 passed, and local integration 53 passed /
8 opt-in skipped. All three real native ABI cases then executed and passed in
the ordinary Explorer desktop namespace against the exact immutable build.
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
`artifacts/`. Exact-head publish/inspection also passed at the signed implementation
checkpoint; corrections require renewed validation and independent review.

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
packaged cross-host execution remains a clean-host CI gate. The existing local
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
