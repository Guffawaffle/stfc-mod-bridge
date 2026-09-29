# Named launch profiles: recovery checkpoint

Status: local work-in-progress checkpoint on `feature/named-launch-profiles-225`.
Do not treat the current Bridge UI as a working multi-account launcher.
Issue #225 covers named profiles; issue #226 covers routing game-client updates.

## What is implemented

- `LauncherProfiles.cs` provides a versioned JSON registry, profile create/edit/
  remove/select operations, folder and name validation, and derived mod config
  and Unity log paths. It stores UI metadata, not game login credentials.
- `MainWindow.Profiles.cs` and `MainWindow.xaml` expose new/adopt/edit/remove
  controls plus an explicit Default/named launch selection. New writes a
  marker only after verifying a profile-capable DLL and confirming the target
  game is stopped under the operation lock. Adopt reads an existing marker and
  DLL export without loading the DLL or changing the marker. The mod creates
  the receipt and encrypted bin on first launch. Removal only removes Bridge
  metadata.
- Profile registry saves compare a file revision under the shared operation
  lock, so a stale Bridge window cannot replace another window's changes.
  Profile changes and launch also compare the displayed Default folder with
  the persisted installation selection under that lock. Launch compares the
  account shown on the button to the refreshed registry, then rechecks
  selection and files under the launch lock. Physical directory identity
  keeps junction aliases from crossing Default and named installs or hiding a
  running game from marker provisioning.
- Editing an enrolled profile changes its display name only. Its stable ID and
  game folder stay bound; moving that folder needs a separate rebind flow.
  A folder remains reserved for named use after its Bridge entry is removed
  while its marker remains.
- Named launches use their own `prime.exe`, pass the derived `-ccm` path and
  a per-ID Unity `-logFile` path as separate arguments, and never route through
  the official launcher. Default launch rejects a marked or named install.
- Bridge now accepts only the mod's lowercase, non-device-name profile IDs.
- The corrected branch builds with zero warnings and errors on .NET SDK
  8.0.425. Focused profile, launch, and process-inspector tests passed (46
  passed, 1 skipped), as did all 263 WPF UI tests. The exact-head review and
  pinned-SDK full suite remain qualification gates.
- `PROFILE_MARKER_DESIGN.md` and `GAME_CLIENT_UPDATE.md` capture the proposed
  bootstrap contract and the observed official-launcher update probe.

## What is not implemented

- The selected Bridge profile does not affect deployment, TOML editing, or
  updater behavior. Bridge creates a marker for New, but only the mod creates
  or repairs the pending/completed enrollment receipt and encrypted bin.
- The Windows mod science branch now reads markers and enrolls an install after
  its profile hooks are installed; this Bridge branch has not had a live named
  launch smoke. Neither branch is a shipped multi-account launcher.
- The profile dialog is not a sign-in or login test. A successful `Process.Start`
  does not prove the game reached the selected account.
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
- Default remains unmarked. A new profile ID begins with empty local
  preferences and requires sign-in. The mod rejects a copied install trying
  to enroll an ID already bound elsewhere. Bare launches may share Unity's
  ordinary `Player.log`; Bridge-managed named launches pass `-logFile`.
- The marker is the profile selector; the prior environment selector was
  removed. The current mod requires `-ccm` to resolve to the derived profile
  TOML for a marked install. Bridge and mod IDs follow the same validator.
- An unmarked, unenrolled mod launch returns to the ordinary path before the
  per-install profile lock. Bridge does not mutate a marker while the target
  game is running; marker replacement and disenrollment need separate work.
- Native Unity `Screenmanager` display settings are excluded from isolation
  by user decision. Mac parity and account-specific LocalLow cache behavior
  remain separate work.
- The updater probe showed that routing two official-launcher INI paths can
  update a child client and restore the original INI byte for byte. Production
  code still needs a lease, durable recovery journal, process attribution,
  rollback, and post-update mod compatibility verification. See
  `GAME_CLIENT_UPDATE.md` for exact evidence and untested cases.

## Resume sequence

1. Read `PROFILE_MARKER_DESIGN.md`, `GAME_CLIENT_UPDATE.md`, and this note;
   inspect the branch diff against `main` before modifying it.
2. Complete the independent Bridge correction review and run the repository
   test gate on the exact head. CI installs the pinned SDK; the local pin is
   still unresolved.
3. Smoke-test Bridge's Default, new-marker, and adopted-child launch routes on
   exact game artifacts with the human confirming the reached account. Do not
   claim isolation from a successful build or static export check alone.
4. Treat official-launcher update coordination as the separate #226 slice.
   Preserve a known-old client fixture for its concurrency and recovery tests;
   do not mutate the original older install.

## Validation caveat

The focused profile/launch/process tests and the WPF UI suite passed on the
correction candidate. The full solution test attempt was not green: this
machine has SDK 8.0.425 but `global.json` pins unavailable 8.0.424, so
child-process tests that invoke `dotnet` failed; an unrelated publish-catalog
expectation also differs under 8.0.425. Automatic approval review blocked a
temporary local `global.json` edit with only “blocked by policy,” so it was
not changed. Run the full suite in CI with the pinned SDK before promotion.
