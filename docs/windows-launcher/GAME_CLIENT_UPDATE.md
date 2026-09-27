# Windows game-client update handoff

Status: observed local probe and proposed Bridge requirements; not an implemented updater.

This document concerns updates to an installed Windows STFC game client. It does
not change Bridge self-update or community-mod deployment. The official Scopely
launcher remains the updater; Bridge must not implement the Xsolla patch protocol
as part of this handoff.

## Observed on September 27, 2026

- A base install was at client 265 and a separate child install was at client
  251. The child was stopped, the official launcher was fully exited, and the
  child install was backed up before the probe. The base game was intentionally
  closed for this update run.
- The official launcher's per-user `launcher_settings.ini` contained
  `152033..GAME_PATH` and `152033..GAME_TEMP_PATH`. After a full-file backup and
  hash, changing only those two paths to the child install and its update
  directory made the official launcher offer **Update** for the child.
- The player clicked **Update**. The child reported client 265 afterward;
  `GameAssembly.dll`, `UnityPlayer.dll`, `prime.exe`, and `.version` matched the
  base install by SHA-256. The official updater left the child's existing
  `version.dll` in place. That mod DLL differed from the base install's DLL;
  matching game files alone did not establish mod compatibility.
- After the official launcher exited, restoring the two paths reproduced the
  original settings file byte for byte. The base path and the child backup were
  preserved. This is one successful route/update/restore sample, not a general
  recovery or compatibility certification.
- With the launcher configured for the base install, its X closed normally
  while a `prime.exe` from a different install remained running. With the
  configured base game's `prime.exe` running, X showed "Can't close launcher
  while game is running." A targeted force-close of the exact verified
  `launcher.exe` process left that base game responsive and did not change the
  settings file.

The close behavior suggests the official launcher checks its configured game
installation rather than every process named `prime.exe`. The exact detection
mechanism was not inspected. In particular, the update probe did **not** test
updating one installation while a different installation stayed in game.

## Proposed handoff requirements

1. Resolve and validate the exact official launcher executable, selected game
   directory, and update directory. Attribute running `prime.exe` processes by
   their executable paths. The installation being updated must not be running;
   an unrelated installation should not be stopped merely because it is open.
2. Take the existing Bridge operation lease. Refuse to route while an official
   launcher update, install, repair, or another Bridge mutation is in progress.
   A blocked or uninspectable process state must fail closed.
3. Back up the complete launcher settings file and its hash before mutation.
   Record a durable, recoverable journal containing original and routed paths,
   target installation, and phase. Change only the two game paths using an
   atomic replacement; preserve unrelated keys and encoding.
4. The official launcher must be fully closed before changing paths. First
   request a normal exit. If it refuses because its currently configured game
   is running, a narrowly targeted force-close may be offered only after
   verifying the process executable and that no update or install is active.
   Never terminate `prime.exe` as a shortcut.
5. Re-read the settings to verify the route, then start the verified official
   launcher and let it perform the update. Keep the operation lease and journal
   active until the launcher has fully exited. Do not auto-click **Play** or
   infer success merely because its window disappeared.
6. Restore the original paths after exit, including on cancellation or failure.
   Recovery on the next Bridge start must be possible after interruption.
   Before restoring, reject unexpected third-party path changes rather than
   overwriting them. Verify the restored settings against the saved hash;
   report any unrelated launcher-written changes separately.
7. Verify the selected install's version and required game files after the
   update. Reassess the installed mod DLL independently before offering direct
   launch. Preserve backups and failure evidence until the user explicitly
   chooses cleanup or a documented retention policy applies. Never copy an
   unverified game-file delta from a different install as an update shortcut.

The route/restore probe was run with a local supervisor and recovery journal.
Those probe files are evidence for these requirements, not production code.

## Open validation

- Repeat with the base game still running while a different selected install
  updates. The process-close comparison supports this path but does not prove
  the official updater will complete safely under concurrent play. A separate
  client-221 install was found in local storage and could seed a disposable
  older-client fixture; its original must not be updated or modified for this
  test. Do not copy account tokens or mod state into the fixture by default.
- Exercise interrupted download/extraction, launcher crash, externally edited
  settings, missing or malformed INI, insufficient disk space, and recovery
  after Bridge or Windows terminates unexpectedly.
- Determine whether the official launcher can start a game automatically after
  an update and how Bridge should block or attribute that launch.
- Check what the official updater preserves across subsequent client versions,
  including `version.dll` and any future per-install profile marker. Recheck
  mod/client compatibility after every update.

Routine game launches may use `prime.exe` directly. A per-install profile
marker is a separate [bootstrap design](PROFILE_MARKER_DESIGN.md); it must
select identity before login state is read and must not silently fall back to
the default account when an enrolled child install loses its marker.
