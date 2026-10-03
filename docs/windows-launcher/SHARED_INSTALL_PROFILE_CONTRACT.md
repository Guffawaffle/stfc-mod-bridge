# Explicit shared-install profile contract

Status: accepted product direction and development contract; live runtime
qualification remains open. The [STFC Profiles catalog contract](https://github.com/Guffawaffle/stfc-profiles/blob/main/docs/PROFILE_CATALOG_CONTRACT.md)
is canonical for shared identity, storage, archive lifecycle and CLI direction. Guff directed removal of the installation-marker
method on 2026-09-29. The prior per-install experiment is superseded outright.
There is no compatibility selector, marker adoption, fallback or migration work.

## Activation and ownership

Installing either runtime provides capability. Ordinary prime/official-launcher
launches retain ordinary OS-user state. Isolated launches supply a stable ID per
process and must isolate that exact ID or stop. Default has its own immutable
catalog ID and `windows-user` kind, while launching in ordinary mode without
isolated arguments, stores or readiness claims. Its descriptor contains metadata
only and belongs to the current Windows user. GUI selection and other active
sessions never redirect a bare launch.

STFC Profiles owns the shared per-user directory catalog. Bridge and the CLI
consume its common discovery and mutation operations; Bridge keeps only its
private UI selection. Profile identity belongs to the immutable ID, so multiple
profiles and the ordinary launch target may use the same canonical executable.
Display names and preferred installations are editable metadata, not identity.
No file beside the executable selects or binds its account.

The accepted Windows root is `%LOCALAPPDATA%\STFC Profiles`, with sibling
`profiles/<id>` and `archives/<id>` directories. Each profile contains plaintext
`metadata.json`; isolated profiles also own protected `player_prefs.bin` and
profile-owned logs/data. Default owns no copy of the Windows-user setup and
cannot be archived or deleted. Installation registrations live independently
under `installations/<id>/metadata.json`, with physical directory identity.
Directories are the catalog; there is no central registry file. Archive and
restore move the whole directory while preserving its ID. They require a stopped
session and exclusion shared with launch admission whose identity survives the
move. Permanent deletion is a separate explicit operation. macOS uses its native
Application Support root under the same shared contract.

Bridge consumes the packaged `stfc-profiles-native.dll` through its versioned
UTF-8 JSON C ABI. The shared native catalog owns enumeration, create/edit,
Windows-user import, archive/restore, live sessions and explicit launch. The
stable C symbols accept JSON API v2 for typed profiles and installations. API v1
never exposes Default as an isolated entry. Bridge stores only an immutable
selected ID in `profile-ui-selection.json`; invalid or archived selection stays
explicitly unavailable until the user restores it or chooses Default. Duplicate
display names remain distinguishable by their IDs. There is no private Bridge
catalog or installation-marker reader/adoption path.

Settings and Data Sync bind to the selected profile's `config.toml`, with its
configuration target visibly labeled. A missing configuration starts an empty
staged draft and is created only on Save. Native profile data leases keep the
active directory stable through each read/save. Verified DPAPI configuration
backups live inside that same profile's `backups/configuration` directory and
move with archive/restore. Changing selection or editing configuration never
copies account credentials. The explicit Windows-user import below creates a
new profile from selected ordinary STFC preferences.
Default settings bind to the selected installation's existing TOML and ordinary
provider backup/history flow. Engineering's runtime and client management bind
to the selected profile's next-launch installation. Running sessions retain
their original installation/process snapshot. Selecting an installation changes
that profile's preference only; it never relocates another registration or
repoints an update/recovery journal.

Shuttle Bay and Engineering share one visible profile selection. Shuttle Bay
provides named Launch/Focus actions; Engineering provides profile lifecycle,
settings and installation/runtime management. View-only switching preserves
drafts. Changing a Settings or Data Sync target offers Save, Discard or Stay. There is
no second hidden launch-selection button.

The profile library accepts explicit ID and store lifecycle inputs from its host.
`New`, `Resume` and `Existing` remain explicit modes; missing established stores
must not silently become empty accounts. Per-profile writer exclusion is owned
by the runtime, independent of whether Bridge stays open.

## Import from a Windows user

Profiles offers **Import from Windows user** alongside New profile. Source users
come from the shared library's `import-sources` operation and are selected by SID,
not commander. Only users with STFC data are shown. Readable matches remain
available even when protected users cannot yet be checked. **Find other Windows
users…** shows a friendly explanation before a separate, short-lived native UAC
discovery check. It returns names/IDs/status, copies no login values and creates
no profile. Cancel retains the list, source, name and folder. A later protected
account import has its own reviewed permission request; Bridge itself stays at
its original permission level. Bridge supplies a new display name and preferred game folder to
`prepare-user-import`, then shows the returned source, destination Windows user,
copy scope and actual access explanation. Close the source user's game first so
its preferences finish saving.

Every import has a friendly **Continue / Not now** review. Required facts are
visible; **Why is this needed?** contains optional detail. Keyboard, screen-reader
labels and neutral status announcements use the existing dialog controls. If
read access requires elevation, Continue permits the shared native owner to open
Windows UAC. A standard user may enter administrator credentials into Windows;
Bridge has no password form. The helper reads only source preferences and returns
them through a private in-memory channel. The original destination user encrypts
and publishes the new immutable-ID profile. No personal browser data is copied.

Closing the review, Escape, declining UAC or any capture failure publishes no
profile. The source, name and folder stay available to retry. Pending operations
prevent duplicate submission. The native import operation does not change Bridge
selection. After a reviewed import succeeds, Bridge explicitly selects the new
profile and opens its detail without launching it. This is a one-time copy:
ordinary game launches and source Windows-user data stay as they are.

## Launch forms

Bridge invokes the shared native launch operation with the immutable ID and
exact installation after its existing provider/artifact preflight. The native
coordinator owns reservation, spawn and isolation-readiness observation. Bridge
reports a successful named launch only when the native response includes a
process ID and `readiness: ready`. A spawned process whose readiness fails is
reported explicitly and is not silently terminated or retried.

The CLI and shortcuts bind immutable IDs; names are presentation. Missing,
invalid or duplicate active IDs and conflicting lifecycle requests reject.
Isolation-ready and logged-in remain separate observations. A launched process
alone is not evidence of account correctness.

Default Bridge launches keep ordinary account preferences and direct
`Process.Start` behavior. They acquire shared native installation access before
spawn and retain it through the exact child lifetime. Both runtime distributions
also hold installation access for their entire game lifetime. Updating requires
exclusive access; an unfinished game-update journal blocks launch admission.
Mod install/update/removal retain their existing global stopped-game checks.

## Repository and distribution

[Guffawaffle/stfc-profiles](https://github.com/Guffawaffle/stfc-profiles) at
`D:/dev/stfc-profiles` owns the static library, platform adapters and standalone
bootstrap. `xmake/library.lua` declares `stfc-profiles-core`. The mod will consume
an immutable source pin; explicit local development overrides identify their
revision and dirty state. The independent core has no mod feature/config globals.

Users install either the profile-only distribution or the full community mod in
one installation. Both compile the same library into their own single Windows
`version.dll`, with one early initializer and one copy of each profile hook.
Distribution switching is a stopped-install operation preserving profile data.
The observed game loads VERSION.dll through UnityPlayer's normal imports;
arbitrary DLL filenames require their own loading mechanism and qualification.

GPL version 3 text and source provenance are retained. The initial marker
extraction at `271fb770bd2fea61bd8e258ce545efb2f2991ddc` is historical; consume the
subsequent marker-free revision, not that initial snapshot.

## Game client integration

The shared native installation operations own status, official update checks,
full-image update application and recovery. Bridge's Game client dialog shows
the selected profile's exact next-launch installation, installed/available versions and actual
native phase/byte progress. The checked target version is bound into Update.
The visible target is captured for each operation. Changing selection cannot
change an already-started operation or its checked update target.
Readiness to update and mod compatibility are separate evidence.

All sessions using the target must stop. Bridge holds its operation lease
through a running native mutation, preserves staged Save/Discard boundaries,
and retains the game update journal/recovery evidence through the shared engine.
The [shared installation/update contract](https://github.com/Guffawaffle/stfc-profiles/blob/main/docs/GAME_INSTALLATION_CONTRACT.md)
is canonical for payload validation, commit, recovery and retained evidence.

## Remaining qualification

The adapters and shared operations are implemented in the current development
worktree. Required full managed tests, all three real native ABI cases in the
ordinary desktop namespace, publish, unsigned package pairing inspection and
pre-signing audit/scan/SBOM checks pass. The dependency uses signed immutable
Profiles source `6b15a352c445efb817634e8ef6be3de4d40818f8`; exact archive/native
hashes and command receipts are recorded in `PROFILE_WORK_RECOVERY.md` and
`artifacts/`. Independent Bridge review remains bound to the committed candidate;
these development artifacts are unsigned and use the base commit as build
metadata, so they are not an exact-head signed release candidate.

A Known Folder string and package identity query alone cannot establish shared
physical storage. The synthetic package gate must verify opened metadata/config
handles resolve to the neutral OS-user catalog, then prove visibility and
profile/installation exclusion between standalone and MSIX processes. Windows
10 uses the documented disabled filesystem-virtualization fallback; Windows 11
uses exact `STFC Profiles` and `STFC Mod Bridge` exclusions. Bridge-owned UI state
keeps its existing external path and ownership for standalone recovery, Battle
sharing and uninstall retention, without copying or migration. Actual packaged
cross-host execution remains unverified until the clean-host gate passes.

The shared producer reports two healthy standalone sessions from one dev
executable with unique encrypted stores, stopped-session exclusion and a
verified 221-to-267 update receipt. Live qualification still must prove account
identity, reverse restart persistence, archive/restore retention and browser
callbacks, including the Bridge path. Native Windows and both macOS architecture
CI lanes pass the pinned source; macOS live protected storage/loading/updater
behavior needs separate runtime evidence. No complete shared-install player
workflow is qualified by Bridge's unit tests or package inspection.
