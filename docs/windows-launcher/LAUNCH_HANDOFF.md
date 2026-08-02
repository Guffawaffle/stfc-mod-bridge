# Windows Launcher Game Handoff

Status: WL-007 supported official-launcher handoff is implemented; installed-client launch smoke remains pending.

## Supported path

The launcher starts the per-user official STFC launcher at
`%LOCALAPPDATA%\Star Trek Fleet Command\launcher.exe`. Authentication,
base-game update, repair, and first-time sign-in remain owned by that launcher.
The community launcher does not reproduce the Xsolla protocol and does not
start `prime.exe` directly.

The action is explicitly a modded launch. An existing manual `version.dll` is
eligible without first being adopted into launcher ownership. Adoption remains
an explicit mod-management action. A missing mod or a launcher-managed install
whose recorded bytes no longer match still blocks launch. An unmodded mode is
represented as a distinct blocked state; v1 does not claim it because safely
disabling and restoring the proxy across an official-launcher handoff has not
been accepted.

## Operation boundary

Launch and deployment use the same per-user process/file operation lease. The
lease is revalidated after acquisition and held until the tracked official
launcher process exits. If that launcher was already running, the supported
executable is invoked to surface it while the exact existing process is
tracked. A concurrent install, update, repair, or uninstall therefore receives
a busy result instead of racing the handoff.

The existing shell/process monitor independently refreshes Home when
`prime.exe` starts or exits. Official-launcher exit also refreshes game and mod
health and yields visible action feedback. Failures never silently reset the
action.

## Offline behavior

Launch eligibility depends only on local game, deployment, process, and
official-launcher health. It does not call release discovery, so a network or
manifest failure cannot remove launch from an already healthy installation.
Whether a particular account session can authenticate offline remains an
official-launcher behavior.

## Automated evidence

The fake-process suite covers explicit modded/unmodded states, locally healthy
offline eligibility, missing official launcher, running-game denial,
cross-operation exclusion, and health re-evaluation after official-launcher
exit. Packaged UI Automation verifies that Home exposes an accessible launch
state without invoking the installed client.
