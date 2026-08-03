# Windows Launcher Parity+ Contract

Status: WL-001 architecture spike active
Repository: `Guffawaffle/stfc-mod`
Baseline commit: `be8d75a7613dae2b79cafe504aef500086b3e4e2`

## Product statement

Build a Windows desktop launcher that makes the supported STFC Community Mod
path discoverable, installable, updateable, configurable, launchable, and
repairable without requiring users to manually copy `version.dll`.

“Parity+” means preserving the useful outcomes of the macOS launcher while
adapting them to the Windows proxy-DLL deployment model:

| Outcome | macOS today | Windows parity+ |
|---|---|---|
| Detect STFC | Xsolla launcher INI | deterministic discovery plus user override |
| Update STFC | launcher-owned Xsolla client | delegate to official launcher and re-check afterward |
| Install/update mod | bundled dylib only | verified release download and transactional `version.dll` deployment |
| Launch modded game | DYLD helper | launch through the supported Windows game/launcher path; proxy loads naturally |
| Configure mod | open raw TOML | schema-driven editor plus raw-file escape hatch |
| Repair | entitlement repair | diagnose and repair proxy/config/deployment state |
| Update launcher | absent | signed/verified self-update contract |
| Diagnostics | OSLog only | redacted diagnostic bundle and actionable health report |

## Architecture decision

The proposed UI stack is self-contained .NET 8 WPF targeting Windows x64.

Reasons:

- Mature Windows 10/11 desktop behavior and accessibility.
- Straightforward custom LCARS rendering without requiring MSIX.
- Reliable filesystem, process, HTTP, JSON, and registry APIs.
- Unit-testable application services independent of the UI.
- The launcher can consume the existing XMake `version.dll` artifact without
  changing the injected mod runtime.

The launcher should live under `windows-launcher/` and remain a separate build
target from the C++ proxy. Release CI assembles the launcher and mod artifacts.

This decision is provisional until the architecture spike proves:

1. a self-contained build on the Windows CI image;
2. launch and process detection against an installed STFC client;
3. custom LCARS shell rendering at 100%, 150%, and 200% display scale;
4. an unpackaged update strategy that does not require administrator rights.

## Ownership boundaries

### Launcher owns

- Game-install discovery and explicit path override.
- Community-mod installation, update, rollback, repair, and removal.
- Launcher self-update.
- Editing and validating user-facing mod configuration.
- Starting the supported STFC launch path.
- Health checks and redacted diagnostics.
- Release-channel selection and installed-version state.

### Existing mod runtime owns

- `version.dll` proxy loading and system `version.dll` forwarding.
- IL2CPP hook installation.
- Runtime configuration parsing and defaults.
- Runtime logs, state snapshots, and feature behavior.

### Official STFC launcher owns

- Authentication.
- Base-game installation and updates.
- Xsolla protocol and game-file repair.

The Windows launcher must not reproduce Xsolla’s Windows updater in the first
production release. Its default launch target starts or safely reuses the exact
official launcher, waits for that tracked process to exit, and then re-evaluates
local state. A separately selected direct `prime.exe` target does not inherit
official authentication, update, or repair responsibilities.

### Future-state integrated game client

The post-v1 product direction is to replace the official launcher for routine
play on an established installation. The Windows launcher should eventually
own both of these player-facing actions:

- launch the installed STFC client with the selected community-mod release;
- detect and apply an available base-game update without requiring the player
  to open the official launcher.

The Home `Game client` row becomes an operational surface rather than passive
status. It offers `Launch game` whenever launch is safe and adds an `Update`
action when a base-game update is available. Launch, game update, mod
deployment, and launcher self-update remain distinct operations with distinct
progress and failure states.

The macOS launcher proves that this concept is technically real: its
[`ActionView`](../../macos-launcher/src/ActionView.swift) exposes game-update
and launch actions, while [`XsollaLib`](../../macos-launcher/src/XsollaLib.swift)
checks installed/latest versions and executes the Xsolla download, extract,
patch, delete, and version plan.

That implementation is evidence, not a Windows port contract. Before Windows
adopts direct game updating, a dedicated design must validate the current
Windows Xsolla protocol, artifact integrity, interruption recovery,
installation locking, required-update policy, game/mod compatibility, repair,
and rollback behavior. Authentication and credential storage remain outside
the community launcher; first-time sign-in or expired-session recovery may
still hand off to the official launcher.

Until those gates are satisfied, the first-production-release boundary above
remains authoritative: base-game update and authentication use the supported
official path even when the player elects to launch an already healthy client
directly.

## Supported environment

- Windows 10 and Windows 11, x64.
- Per-user installation without elevation where the STFC installation is
  user-writable.
- The standard native Windows STFC client.

Wine/Linux is not a v1 launcher target. Existing manual Wine installation
remains supported by the mod and documentation.

An installation requiring elevation must be reported explicitly. The launcher
must not silently restart elevated or broaden permissions.

## Discovery contract

Discovery produces a collection of candidates, not a single guessed string.
Each candidate contains:

```text
rootPath
primeExecutablePath
officialLauncherPath?
source
confidence
isWritable
gameVersion?
validationErrors[]
```

Candidate sources, in descending priority:

1. A previously user-confirmed path.
2. Configuration written by the official STFC launcher.
3. Known per-user and machine-wide installation locations.
4. Running STFC or official-launcher process metadata.
5. An explicit folder selected by the user.

A valid game root must contain `prime.exe`. A mod deployment target is the same
directory as `prime.exe`, never the official launcher’s own directory.

No filesystem-wide recursive search is allowed. Discovery must be bounded,
cancellable, and testable with synthetic filesystem fixtures.

## Deployment contract

The Windows mod payload is `version.dll`. The launcher may also manage files
explicitly added to `docs/GAME_DIRECTORY_FILE_ALLOWLIST.md`; it must not treat
the game directory as launcher-owned.

Before modifying the game directory:

1. Resolve and display the exact target.
2. Confirm `prime.exe` belongs to that target.
3. Confirm STFC is not running.
4. Verify the downloaded artifact against release metadata and SHA-256.
5. Stage the complete replacement on the same volume.
6. Preserve a recoverable backup of an existing managed file.

Commit uses same-volume replacement. If commit fails, restore the previous
managed state and leave the installed-version record unchanged.

Installation success means:

- target `version.dll` exists;
- its SHA-256 equals the selected release asset;
- its embedded mod version is readable and expected;
- no staging files remain;
- rollback metadata describes the prior state, if any.

Uninstall removes only launcher-managed files and preserves configuration,
logs, game files, and unknown files. Config removal is a separate explicit
action.

## Release and update contract

Release discovery uses GitHub releases from `Guffawaffle/stfc-mod`. The
launcher consumes a machine-readable manifest published with each release.

The canonical schema, artifact kinds, channel mapping, authenticity boundary,
withdrawal behavior, and producer/consumer validation rules are defined in
`docs/windows-launcher/RELEASE_MANIFEST.md`.

Schema v1 shape:

```json
{
  "schemaVersion": 1,
  "releaseVersion": "2.1.0-guffa.6",
  "tag": "v2.1.0-guffa.6",
  "channel": "stable",
  "releaseState": "active",
  "minimumLauncherVersion": "0.1.0",
  "source": {
    "repository": "Guffawaffle/stfc-mod",
    "targetCommit": "<40 lowercase hex characters>"
  },
  "manifestAuthenticity": {
    "scheme": "none"
  },
  "artifacts": [
    {
      "id": "windows-mod-dll-x64",
      "kind": "windows-mod",
      "platform": "windows",
      "architecture": "x64",
      "fileName": "version.dll",
      "mediaType": "application/vnd.microsoft.portable-executable",
      "sha256": "<64 lowercase hex characters>",
      "size": 123,
      "authenticity": {
        "scheme": "authenticode",
        "scope": "artifact"
      }
    }
  ]
}
```

Rules:

- Stable is the default channel; prerelease selection is explicit.
- Redirects are allowed only over HTTPS.
- HTTP success, declared size, and SHA-256 are mandatory.
- A download is untrusted until verification succeeds.
- Unknown manifest schema versions fail closed with an actionable message.
- Downgrade requires explicit confirmation.
- Withdrawn releases are not newly offered.
- Checksums are artifact integrity metadata, not manifest authentication.
- Network failure must not prevent launching an already healthy installation.

Self-update must use a separate bootstrapper or replace-on-exit helper. The
running launcher never overwrites its own executable in place.

## Configuration contract

`community_patch_settings.toml` remains the user-authoritative file. The mod
runtime remains the parser of record.

The GUI editor operates against a generated, versioned configuration schema
rather than duplicating defaults and descriptions by hand in C#.

The settings experience is unified even when the schema delegates value
handling to scalar, keybinding, and notification-policy adapters. Search,
categories, changed-state, validation, and persistence behavior remain
consistent across those control types.

The schema must describe:

- canonical key and section;
- value type and constraints;
- runtime default;
- user-facing title, description, and risk/support tier;
- platform availability;
- restart/reload behavior;
- deprecated aliases and migration notes.

GUI saves are sparse: write user intent, not every resolved default.

Save behavior:

1. Parse the current file.
2. Refuse destructive rewrite when unsupported syntax cannot be preserved.
3. Write a sibling temporary file.
4. Parse and validate the temporary result.
5. Back up the prior file.
6. Atomically replace the destination.

The UI must provide:

- search and category navigation;
- changed-from-default indicators;
- validation before save;
- restore-default/remove-override actions;
- raw-file open;
- effective-value and restart-required indicators.

Unknown keys and comments must survive normal edits.

### Release source boundary

The preferred release source (`guffawaffle` parity+ or `netniv` upstream) is
launcher state, not a mod TOML setting. It selects the release manifest,
artifact trust policy, update stream, migration guidance, and matching
configuration schema/capability adapter for future explicit checks. It is not
installed-artifact provenance and never attributes an unknown/custom DLL.

Selecting a preference changes only launcher state and takes effect after a
Mod Bridge restart. Switching the installed mod to another provider/runtime is
a separate, game-closed migration transaction. The launcher previews installed
artifact and configuration compatibility, requires explicit confirmation and
a protected exact-byte TOML backup when configuration exists, and retains
artifact/preference rollback guarantees. Automatic update never crosses release
sources silently, and custom/developer DLLs remain runnable and untouched until
the player explicitly chooses replacement. The authoritative state/action,
backup/restore, retention, privacy, and transaction rules are in the
[mod source-selection lifecycle](MOD_DEPLOYMENT.md#mod-source-selection-lifecycle).

TOML remains the runtime and interchange boundary for NetniV compatibility and
safe source switching. A future Guffawaffle-only profile store may be richer,
but it must compile/export deterministic sparse TOML while the C++ runtime
consumes TOML.

## Launch contract

Launch is a persisted launcher-owned selection between `Open Scopely launcher`
and `Launch prime.exe`; it is never a mod-TOML setting. New, migrated, or
unknown preference state defaults to Scopely, preserving the earlier behavior.

Both actions acquire the same operation lease as mod mutation and revalidate
after acquisition. Scopely requires only the exact supported official launcher
and remains independently available when the game is running or the game root
is missing. Its lease lasts until the exact newly started or safely discovered
existing launcher process exits. Direct launch requires a valid selected game
root, a healthy local mod deployment, and no running STFC process; its lease is
released after successful process creation.

Each target publishes structured availability, reason, and next-action data.
The UI must not derive recovery behavior from target or display-name strings.
Starting a new process is a changed action result; safely reusing an already
running Scopely process is an explicit no-change result. Reuse still invokes
the supported executable to surface its UI, disposes that activation handle,
and retains the exact pre-existing process as the lifetime boundary.

The launcher reports distinct states:

```text
not-installed
game-ready-mod-missing
mod-ready
update-available
repair-required
game-running
operation-in-progress
offline-ready
blocked
```

## Diagnostics and privacy contract

Health checks cover:

- game discovery and write access;
- installed `version.dll` identity, version, and checksum;
- official launcher availability;
- game/mod version compatibility where known;
- config parse status;
- recent mod log presence;
- incomplete update or rollback state.

A diagnostic export is previewed before creation and excludes:

- authentication tokens and cookies;
- arbitrary environment variables;
- unrelated files;
- full user-profile paths where a stable redaction is sufficient;
- config values classified as secrets or private endpoints.

Diagnostics must be useful offline and must not upload automatically.

## UI contract

The accepted visual direction is a modern, compact Windows application with
System, Light, and Dark themes. LCARS is no longer a product requirement.

The home surface is outcome-oriented and shows only actionable game, mod, and
operation state. Settings uses a separate, larger workspace with category
navigation and search. Internal health dimensions, discovery provenance, and
filesystem paths remain in structured logs and explicit redacted diagnostics
rather than the normal launcher surface.

See [the UX direction](UX_DIRECTION.md) for the home/settings information
architecture, notification-scale behavior, and directional mockup.

Required accessibility behavior:

- keyboard access to every action;
- visible focus;
- screen-reader names and operation status;
- Windows text scaling and 100%/150%/200% display scaling;
- reduced-motion support;
- sufficient contrast;
- progress and errors expressed in text.

Destructive or privilege-requiring operations show the exact target and effect.

## Persistence contract

Launcher-owned state lives under a per-user application-data directory, not in
the game directory, except for allowlisted deployed artifacts.

State includes:

- confirmed installation ID and path;
- selected release channel;
- installed artifact identity and checksum;
- transaction journal;
- rollback metadata;
- launcher preferences.

Launcher preferences include the selected launch target. The default remains
the official Scopely launcher; choosing direct `prime.exe` is explicit and is
retained even while temporarily unavailable.

Paths are stored as Windows-native absolute paths. Logs and state must tolerate
non-ASCII usernames and installation directories.

## Transaction state machine

Every mutating operation uses a persisted journal:

```text
planned -> downloading -> verified -> staged -> committing -> committed
                                      |              |
                                      v              v
                                    failed       rolling-back -> rolled-back
```

On startup, an incomplete transaction is detected before offering another
mutation. Recovery is deterministic and idempotent.

Only one mutation may hold the launcher operation lock. Read-only health checks
may continue when they do not race with the active transaction.

## Testing contract

The core must be UI-independent and exercised with fake filesystem, process,
network, release, and clock implementations.

Blocking tests include:

- discovery precedence and invalid candidates;
- path traversal and wrong-directory rejection;
- hash/size/HTTP failure;
- install, update, downgrade, uninstall, and rollback;
- interruption at each transaction boundary;
- game-running mutation denial;
- config round-trip with comments and unknown keys;
- offline launch of a healthy installation;
- redaction of diagnostic fixtures;
- release-channel and withdrawn-release behavior.

CI must build and test the launcher on Windows. Packaging tests inspect the
published archive/installer and verify its manifest and checksums.

Manual release smoke tests cover clean install, existing manual mod install,
upgrade, game update followed by repair, rollback, offline launch, high-DPI,
and non-ASCII paths.

## Explicit non-goals for the first production release

- Reimplementing Xsolla game patching.
- Modifying game files other than allowlisted community-mod artifacts.
- Runtime hook configuration reload.
- Wine/Linux launcher support.
- Multi-account orchestration.
- Automatic diagnostic upload.
- Silent elevation.
- Background Windows service.

## Delivery gates

The project advances through these gates:

1. Architecture spike accepted.
2. Core contracts and test fixtures accepted.
3. Read-only discovery/health MVP accepted.
4. Transactional mod install/update/rollback accepted.
5. Config editor accepted.
6. Launch and official-updater handoff accepted.
7. Diagnostics and accessibility accepted.
8. Packaging, self-update, and release operations accepted.

No phase may weaken the proxy DLL’s existing build or manual installation path.
