# Named launch profiles: recovery checkpoint

Status: local work-in-progress checkpoint on `feature/named-launch-profiles-225`.
Do not treat the current Bridge UI as a working multi-account launcher.
Issue #225 covers named profiles; issue #226 covers routing game-client updates.

## What is implemented

- `LauncherProfiles.cs` provides a versioned JSON registry, profile create/edit/
  remove/select operations, folder and name validation, and derived mod config
  and Unity log paths. It stores UI metadata, not game login credentials.
- `MainWindow.Profiles.cs` and `MainWindow.xaml` expose new/adopt/edit/remove
  profile controls. Removal only removes the Bridge registry entry. The default
  game folder cannot also be assigned to a named profile.
- `LauncherProfilesTests.cs` has six focused passing tests for this model.
  The Release solution build passes using local .NET SDK 8.0.425.
- `PROFILE_MARKER_DESIGN.md` and `GAME_CLIENT_UPDATE.md` capture the proposed
  bootstrap contract and the observed official-launcher update probe.

## What is not implemented

- The selected Bridge profile does not affect Launch, deployment, TOML, or
  updater behavior. No `stfc_community_mod.profile` marker or enrollment
  receipt is read or written by Bridge or the mod yet.
- No early runtime marker selection, fail-closed hook installation, or
  per-profile mod-file routing exists for a bare `prime.exe` launch.
- Bridge does not yet set `STFC_MOD_ISOLATED_PROFILE`, `-ccm`, or `-logFile` for
  named-profile launches. The profile dialog is not a sign-in or login test.
- The official-launcher path switch was a supervised local probe, not Bridge
  updater code. Updating a child while another install stays in game remains
  untested.

## Decisions and open contracts

- An install with neither marker nor enrollment stays on the existing Default
  or batch path. A valid marker may enroll on first modded launch. Once an
  install is enrolled, a missing, malformed, or mismatched marker must stop
  before login rather than silently selecting Default. A missing mod DLL is
  ordinary unmodded play by user choice; a DLL that fails to load needs a
  clear diagnostic but cannot enforce this contract.
- Default remains unmarked. A new profile ID is a fresh device and requires
  sign-in. Copying an install with its marker may deliberately reuse the same
  account; it must not be presented as a new account. Unity `Player.log` for
  bare launches is parked; Bridge-managed launches can pass `-logFile`.
- The marker and `STFC_MOD_ISOLATED_PROFILE` must agree when both are present.
  Define the `-ccm` conflict rule before enabling marker launches; never load
  another profile's TOML silently. Bridge's adopted-ID validator currently
  accepts uppercase while the proposed marker and mod selector require
  lowercase; reconcile them before enrollment.
- The updater probe showed that routing two official-launcher INI paths can
  update a child client and restore the original INI byte for byte. Production
  code still needs a lease, durable recovery journal, process attribution,
  rollback, and post-update mod compatibility verification. See
  `GAME_CLIENT_UPDATE.md` for exact evidence and untested cases.

## Resume sequence

1. Read `PROFILE_MARKER_DESIGN.md`, `GAME_CLIENT_UPDATE.md`, and this note;
   inspect the branch diff against `main` before modifying it.
2. Finalize the shared lowercase ID, canonical install-path, marker/receipt,
   and `-ccm` precedence contracts. Keep the runtime and Bridge validators in
   agreement. Resolve selectors before mod files or login state are accessed.
3. Implement and test the mod bootstrap and failure paths, then Bridge marker
   enrollment and launch wiring. Smoke-test Default, the existing linked child
   profile, and a fresh child without copying account tokens. Do not claim
   isolation from a successful build alone.
4. Treat official-launcher update coordination as the separate #226 slice.
   Preserve a known-old client fixture for its concurrency and recovery tests;
   do not mutate the original older install.

## Validation caveat

This checkpoint passed six `LauncherProfilesTests` and a Release solution
build. The full solution test attempt was not green: this machine has SDK
8.0.425 but `global.json` pins unavailable 8.0.424, so child-process tests
that invoke `dotnet` failed; an unrelated publish-catalog expectation also
differs under 8.0.425. Re-run the full suite with the pinned SDK before PR
handoff. The WPF analyzer issue found during that attempt was fixed, and the
subsequent full build passed with zero warnings and errors.
