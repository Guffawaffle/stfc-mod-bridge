# WL-001 Windows Launcher Architecture Decision

Status: architecture accepted; release validation carried forward

Issue: `Guffawaffle/stfc-mod#172`

Branch: `spike/wl-001-windows-launcher-architecture`

Base: `be8d75a7613dae2b79cafe504aef500086b3e4e2`

## Decision

Proceed with a self-contained .NET 8 WPF launcher targeting Windows x64.
Keep launcher policy and platform services in a UI-independent core assembly.
Distribute the first production launcher through a per-user bootstrapper, with
an unpackaged ZIP retained as a transparent recovery and CI artifact.

The architecture direction was accepted on 2026-07-27 after the self-contained
preview was launched and reviewed. Remaining clean-machine, DPI, keyboard,
screen-reader, CI-artifact, and signed-tag checks are release-quality evidence;
they do not block downstream work from stacking on PR #198.

## Proven in this spike

- The WPF application and UI-independent unit tests build with the .NET 8 SDK.
- `win-x64` publish is self-contained and produces a launcher executable that
  does not depend on a machine-wide .NET runtime.
- The app manifest requests `asInvoker` and Per-Monitor V2 DPI awareness.
- The LCARS shell uses logical device-independent units, reflow/scroll
  boundaries, text state labels, keyboard mnemonics, visible focus, automation
  names, and no animation.
- A local render at 120 DPI (125% display scale) completed without clipping and
  correctly reported the running `prime.exe` game client.
- Process inspection is isolated behind `IGameProcessInspector` and checks
  `prime.exe` without WMI, injection, or process mutation.
- The Home may subscribe to unprivileged Windows shell creation events and the
  tracked game process's exit signal as event-driven invalidation. The
  inspector remains authoritative, and no WMI subscription, interval polling,
  or process mutation is introduced.
- Per-user program files and mutable state have separate ownership roots.
- CI can test and publish the launcher without changing the C++ build jobs.

## Installed-client launch and process boundary

The launcher will treat STFC and the official launcher as external processes:

1. Detect `prime.exe` by process name through the operating-system process API.
2. Never retain a process handle longer than one bounded query.
3. Deny launcher-managed game-directory mutation while STFC is running.
4. Start the supported official launcher path with Windows shell execution so
   authentication and official preflight remain official-launcher concerns.
5. Wait for the official launcher or game process through an injectable
   process service, then re-run discovery and deployment health checks.
6. Keep direct `prime.exe` launch as an unproven advanced path until a later
   work item records authentication and update behavior.

`WL-002` owns deterministic path discovery. `WL-007` owns process lifecycle,
official-updater handoff, and actual launch behavior.

## Unpackaged install ownership

The recommended per-user layout is:

```text
%LOCALAPPDATA%\Programs\STFC Community Mod Launcher\  immutable program payload
%LOCALAPPDATA%\STFC Community Mod Launcher\           state, logs, journal, rollback
```

Properties:

- no elevation or machine-wide permission changes;
- no launcher-owned files in the game directory except explicitly allowlisted
  mod deployment artifacts;
- uninstall can remove program files independently from user state;
- non-ASCII profile paths remain ordinary Windows paths;
- package replacement can stage on the same volume as the program directory.

The spike publishes a ZIP because it is inspectable and sufficient to prove
self-contained output. Production should add a small signed bootstrapper that
installs into the per-user program directory, creates user-approved shortcuts
and uninstall registration, and invokes the same core transaction policy.

## Self-update decision

Use a separate replace-on-exit bootstrapper. The running WPF process never
overwrites itself.

```text
download -> verify -> stage -> start bootstrapper -> launcher exits
         -> atomic replace -> start/health-check -> commit or rollback
```

Checksum verification detects corruption but is not publisher authentication.
Production self-update therefore requires:

- an Authenticode-signed launcher and bootstrapper;
- an authenticated release manifest with an explicit trust/rotation policy;
- version, architecture, declared size, and SHA-256 verification;
- rollback payload verification using the same policy;
- a withdrawal mechanism that prevents newly offering a compromised release.

The launcher remains usable offline when its installed payload is healthy.

## Accessibility and DPI smoke

Manual acceptance must run the published executable at Windows display scales
of 100%, 150%, and 200% and record:

- no clipped state text or unreachable actions;
- keyboard access to Refresh and Exit;
- a visible focus rectangle;
- usable screen-reader names and live status text;
- readable state without relying on LCARS color;
- stable layout after moving between monitors with different scale factors;
- no motion when Windows reduced-motion preferences are enabled.

The shell intentionally contains no animation, so reduced motion is satisfied
by construction for the spike.

## Rejected alternatives

### Reimplement the official launcher

Rejected. Authentication, base-game updates, and Xsolla ownership remain
outside this product.

### Machine-wide MSI as the only distribution

Rejected for v1 because it makes elevation and administrator policy part of
the happy path. A future optional machine-wide package may wrap the same core
only if it preserves the per-user route.

### In-place self-overwrite

Rejected because Windows executable locking makes recovery fragile and a failed
replacement can strand the launcher.

### UI-owned filesystem and process operations

Rejected because transaction, discovery, redaction, and process policies must
be deterministic and testable without WPF.

## Release evidence carried forward

- Confirm the self-contained artifact starts on a machine without a separately
  installed .NET runtime, or in an equivalent clean Windows sandbox.
- Record the 100%/150%/200% DPI and keyboard smoke.
- Accept the launcher CI artifact on the delivery PR.
- Verify the tagged Authenticode signing path before retiring the sidecar
  rollback credential.
