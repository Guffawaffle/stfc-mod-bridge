# Named launch profiles: current return point

Status: 2026-09-29 documentation checkpoint. The
[canonical STFC Profiles catalog contract](https://github.com/Guffawaffle/stfc-profiles/blob/main/docs/PROFILE_CATALOG_CONTRACT.md) and the local
[Bridge launch contract](SHARED_INSTALL_PROFILE_CONTRACT.md) supersede the earlier
Bridge-owned registry design. Historical commits and verification receipts remain
evidence of their exact implementation checkpoints.

## Accepted target

STFC Profiles owns a shared per-user directory catalog. Active profiles live in
`profiles/<immutable-id>`; archived profiles live in sibling `archives/<same-id>`.
Each directory holds editable `metadata.json`, encrypted preferences and owned
logs/data. There is no central registry. Bridge and the standalone CLI use common
operations; Bridge's saved UI selection cannot redirect ordinary launches.

Archive/restore preserve the whole directory and ID, require a stopped session,
and coordinate with launch admission through stable lifecycle exclusion.
Permanent deletion is separate and explicit. Several profiles may share one
game executable. Names and installation associations never change the ID.

Ordinary launches retain ordinary OS-user state. Explicit named launches must
isolate the requested ID or stop. The retired installation selector, adoption,
compatibility, fallback and migration have no implementation or test work items.

## Current implementation evidence

Bridge branch `feature/named-launch-profiles-225` at
`3b43f44550230be5b2aca4744a2e983014d59a9e` tracks draft PR #227, based on
`aad5fda716d9cb402965c294d7bc33b061ab1d8c` for the completed review checkpoint.
At that source checkpoint, Bridge persists `launch-profiles.json`, has stable IDs,
revision/operation locking, and passes `-stfc-profile <id>` with config/log argv.
Those are existing mechanics to replace or integrate, not authority to keep a
private catalog. The current running-process gate still blocks a second game.

The profile library at `f8fe406cbc69e86db3a6c47e839ae1c67be2baf4` contains ID
validation, Windows DPAPI storage and an explicit-input adapter. Its old
`STFC Community Mod\Profiles` path and directory-local lock do not implement
the accepted storage/lifecycle contract. The active mod at
`0c34f249b1ea7bf00dd5221e9834983af7c5883a` removed the obsolete capability export;
the new explicit-launch runtime is not integrated.

SDK alignment and Go vulnerability repair are complete at the Bridge checkpoint.
Full local validation and exact-head CI passed; that evidence does not qualify
profile runtime behavior or apply automatically to a later candidate.

## Next work

Implement the shared catalog/lifecycle operations, CLI and Bridge consumption;
then integrate both runtime hosts, readiness/admission and macOS protection and
loading. Qualify ordinary launch, two correct accounts from one executable,
reverse restart persistence, duplicate-profile refusal, browser callbacks and
stopped-session archive/restore. No shared-install player runtime is qualified.
