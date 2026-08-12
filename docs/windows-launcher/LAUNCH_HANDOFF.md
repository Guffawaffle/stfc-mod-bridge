# Windows Launcher Game Handoff

Status: selected-target split launch is implemented; installed-client dogfood and screenshots remain pending.

## Player-facing targets

Home exposes one compound split button with two ordinary launcher-owned targets:

- `Open Scopely launcher` starts or safely reuses the exact per-user official launcher at
  `%LOCALAPPDATA%\Star Trek Fleet Command\launcher.exe`;
- `Launch prime.exe` starts the validated executable in the confirmed game directory directly.

The selected target is persisted in `ui-preferences.json`, never mod TOML. New, migrated, malformed, and unknown
preference documents default to `Open Scopely launcher`, preserving the pre-split behavior. Selecting an unavailable target
does not rewrite the preference; its structured reason and next action remain visible.

The Scopely path owns authentication, base-game update, repair, and first-time sign-in. Direct launch does not add any of
those responsibilities to the community launcher. Direct launch is a base-game action: it may start a healthy managed mod,
start without the mod when no `version.dll` exists, or require an explicit per-attempt warning override when a proxy exists
but Mod Bridge cannot verify it.

## Eligibility

The two targets deliberately resolve health independently.

`Open Scopely launcher` requires only the exact supported official launcher. It remains available while the game is running
and when a game directory has not been selected.

`Launch prime.exe` requires:

- a confirmed valid directory containing `prime.exe`;
- no running STFC game process;
- no active, incomplete, or malformed mod transaction;
- the shared launcher operation lease.

Proxy state changes the launch presentation without turning mod health into a blanket launch gate:

- no `version.dll`: launch is immediately available and explicitly identified as running without the community mod;
- exact verified managed `version.dll`: launch is immediately available with the managed mod;
- unrecorded, changed, or unreadable `version.dll` evidence: launch remains available only through an explicit
  **Launch anyway** warning;
- active/incomplete deployment, invalid game target, a running or unattributable game process, or a busy operation lease:
  launch remains blocked.

**Launch anyway** is authorization for one launch attempt. It does not persist trust, adopt the proxy, alter provenance,
move or quarantine files, repair the mod, or weaken a later warning. The dialog states that Windows may load the present
proxy automatically and that Mod Bridge cannot vouch for its source or behavior.

Each Core presentation carries a stable target, reason, and `LauncherLaunchRecoveryAction`. WPF projects those fields; it
does not infer recovery guidance from display text. Home and UI Automation reasons are stable and path-free; raw filesystem
and deployment exception detail belongs only in the redacted Diagnostics workflow.

## Operation boundary

Both targets share the per-user launcher operation lease with install, update, repair, recovery, and uninstall. Eligibility
is revalidated after lease acquisition.

For Scopely handoff, the lease is held until the exact tracked launcher process exits. The application service first looks
for a safely inspectable running process whose executable path exactly matches the supported launcher:

- a newly started process produces a changed result;
- an exact already-running process is reused and produces a truthful no-change result; the executable is still invoked to
  surface its UI, the newly returned activation handle is released immediately, and the exact pre-existing process remains
  the tracked lifetime boundary;
- a process that cannot be inspected or does not match is never adopted as the lifetime boundary.

For direct launch, the lease is held through final validation and successful `prime.exe` process creation, then released.
The game-process monitor owns subsequent running-state transitions. A concurrent launcher mutation receives a busy result
instead of racing either handoff boundary.

## Feedback and failure behavior

Launch has an observable action-feedback channel independent from mod mutation and launcher self-update. Accepted,
changed, no-change, failed, unavailable, and duplicate-activation behavior follows `ACTION_FEEDBACK.md`.

Home displays the active action first and otherwise the most recently changed Mod or Launch feedback. A persistent launch
completion therefore cannot mask a later mod operation. Failures and transient unavailability never silently change the
selected target.

## Automated evidence and manual residuals

Core tests cover both targets, Scopely changed/no-change identity, post-lock revalidation, cross-operation exclusion,
running-game independence, direct managed and unmodded launch, per-attempt unverified-proxy approval, failures, and
preference migration. WPF tests cover split geometry, the warning and **Launch anyway** action, keyboard/focus behavior,
non-color selection state, structured availability projection, and feedback arbitration.

Packaged UI Automation opens the target menu, accepts either persisted primary target, and finds both exact choices without
invoking either executable. The disposable smoke also proves the active TOML hash is unchanged.

Still manual:

- launch a real detected `prime.exe`;
- open and reuse the real Scopely launcher;
- capture both selections, the open menu, running-game state, and unavailable state.
