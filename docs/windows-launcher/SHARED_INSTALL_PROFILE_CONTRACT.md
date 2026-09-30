# Explicit shared-install profile contract

Status: accepted product direction and development contract; live runtime
qualification remains open. Guff directed removal of the installation-marker
method on 2026-09-29. The prior per-install experiment is superseded outright.
There is no compatibility selector, marker adoption, fallback or migration work.

## Activation and ownership

Installing either runtime provides capability. Ordinary prime/official-launcher
launches retain ordinary OS-user state. Named launches supply a stable ID per
process and must isolate that exact ID or stop. GUI selection and other active
sessions never redirect a bare launch.

Bridge owns its JSON profile registry, display names, stable IDs, installation
metadata and launch selection. Profile identity belongs to the ID, so multiple
profiles and Default may refer to the same canonical prime executable. Removing
a Bridge entry removes metadata only. Renames and folder edits do not change IDs.
No file beside the executable selects or binds its account.

The profile library accepts explicit ID and store lifecycle inputs from its host.
`New`, `Resume` and `Existing` remain explicit modes; missing established stores
must not silently become empty accounts. Per-profile writer exclusion is owned
by the runtime, independent of whether Bridge stays open.

## Launch forms

Bridge named launches use `prime.exe -stfc-profile <id>` with separate argv tokens
for the ID, config path and Unity log path. The CLI/shortcut coordinator should
bind immutable IDs; display names are presentation. Missing/invalid/duplicate IDs
and conflicting lifecycle requests reject. Isolation-ready and logged-in are
separate observations. A launched process is not evidence of account correctness.
Runtime integration of these arguments is still being implemented.

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

## Remaining qualification

1. Integrate the explicit host API and startup ordering in the mod and standalone
   bootstrap. Validate the exact client hooks on each platform.
2. Prove two correct accounts from the same canonical executable, reverse restart
   order, per-profile writer exclusion and sign-in callback routing.
3. Define readiness and atomic installation/session admission, then coordinate
   install/update/removal across ordinary and named sessions.
4. Qualify macOS protected storage, loading and architecture coverage separately.

There are no player deployments of this feature. Live game files, accounts and
stores have not been changed by the source removal.
