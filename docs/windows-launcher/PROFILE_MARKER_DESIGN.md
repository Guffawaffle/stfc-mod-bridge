# Per-install profile marker design

Status: Windows implementation candidate. The mod science branch reads the
marker and owns enrollment; Bridge can create a marker for a new profile or
adopt an already marked install. Runtime qualification is still in progress.

The goal is for an opted-in Windows game installation to select its account
when `prime.exe` is launched directly, without requiring a per-launch batch
argument or a separate Windows user. An install without a marker or enrollment
receipt retains today's behavior, including existing batch arguments. The
marker contains an opaque profile ID, not a game username, password, token,
or browser cookie.

## Small on-disk contract

Place `stfc_community_mod.profile` beside `prime.exe` and `version.dll`. Its
entire UTF-8 content is one line:

```text
v1:josep
```

`v1:` is a format tag. The remainder is a stable profile ID of 1-32 lowercase
ASCII letters, digits, `-`, or `_`, excluding reserved Windows device names.
Require exactly one line, allowing a final CRLF or LF, but no BOM, other
whitespace, comments, or additional fields. An unknown format or malformed ID
stops the game before login. A human-facing profile name is stored by Bridge,
not in this file.

The DLL must resolve the marker relative to the running `prime.exe`, never the
working directory. Reading the marker and establishing whether isolation is
required must happen before the game's authentication state is read. The
current Windows mod loads through `version.dll` and installs its profile hooks
around `il2cpp_init`. Resolve the selected ID before `File::Init()` chooses
mod file paths and before `Config::Get()` parses TOML; then install the identity
hooks before login-state reads. The selector must activate fail-closed behavior
when an early hook cannot be installed. It must not do substantial file I/O
from `DllMain` under the Windows loader lock.

## Enrollment and missing-marker safety

The marker alone cannot distinguish an ordinary unmarked install from a child
whose marker was lost. A separate, versioned per-install enrollment receipt
under `%LOCALAPPDATA%\STFC Community Mod\ProfileBindingsV2\` records the
canonical game-install path and expected profile ID. Its filename is derived
from a stable hash of that path. This is a small runtime contract, separate
from Bridge's UI profile registry; a bad receipt affects only its install.

The mod returns to the ordinary unmarked path before taking the per-install
launch lock when both marker and receipt are absent. If either exists, it takes
that lock and re-reads both files before enrollment or isolation. A new
enrollment writes a pending receipt, commits an encrypted per-ID
`player_prefs.bin`, then writes the completed receipt. A pending enrollment
can resume after an interrupted first launch. A completed receipt requires
an existing valid bin; a missing bin stops launch rather than creating an
empty account state.

| Marker | Receipt for this install | Behavior with a loaded profile-capable DLL |
|---|---|---|
| Absent | Absent | Existing default or batch behavior; no new interruption. |
| Valid | Absent | Start enrollment and create the pending receipt before preference writes. |
| Valid | Pending | Resume enrollment under the same ID and install path. |
| Valid | Matching | Isolate under that profile ID. |
| Absent or invalid | Present | Stop before login; do not fall back to Default. |
| Valid | Mismatched or invalid | Stop before login. |

Bridge provisions a new marker only while the target game is stopped, after
checking the profile-capable DLL and rechecking process state under its shared
operation lock. It does not change a marker while that game is running. The mod
writes pending and completed receipts and the encrypted preference bin on first
launch. Bridge reads, but does not rewrite, a hand-created marker when adopting
an install. If Bridge writes a marker but cannot save its UI entry, the marker
remains safe to adopt on a retry. No marker and no receipt remains the ordinary
mod path.
Complete loss of *both* marker and receipt cannot be distinguished from an
ordinary install; this is a practical accidental-loss guard, not a guarantee
against deletion of all state.

The current mod candidate rejects another install claiming the same profile
ID. A copied or moved install needs a deliberate future rebind flow; copying
its marker alone does not create a new account. A new account requires a new
ID.
Removing a Bridge UI profile does not silently delete game files, marker, or
receipt; disenrollment is a separate explicit operation.

## Selector and file effects

- A valid marker is the sole profile selector. The earlier science
  `STFC_MOD_ISOLATED_PROFILE` environment selector was removed. A malformed
  marker or mismatched completed receipt stops before login.
- An unmarked, unenrolled install retains its ordinary launch path. A marker
  without a profile-capable DLL cannot prove isolation. Bridge checks the
  marker and the DLL's contract export before launch and checks again under
  its launch lease. That static check is a gate, not proof that runtime hooks
  succeeded.
- A selected ID namespaces the mod's encrypted Unity `PlayerPrefs` store and
  isolated sign-in browser, and chooses the per-ID mod TOML, log, vars, and
  battle files. A new ID starts as a fresh local preference state and needs
  account sign-in. Default remains unmarked.
- `-ccm` is a config-file path, not an identity selector. For a marked game,
  the mod accepts only the derived `stfc-mod/<id>/<id>.toml` path. Bridge
  passes that path explicitly for a named launch. Unmarked legacy `-ccm`
  behavior remains unchanged.
- Bridge passes `-logFile` to place a named launch's Unity `Player.log` under
  `%LOCALAPPDATA%\STFC Community Mod\Profiles\<id>\`. A direct bare launch
  may still use Unity's ordinary log path. The game's native `Screenmanager`
  display settings remain outside profile isolation by user decision.

Bridge-generated and adopted IDs now follow the mod's lowercase ID rules.
Names may change; IDs and enrolled game folders do not. Moving an enrolled
install needs a separate rebind flow because the mod receipt includes the
canonical installation path.

## Required validation before use

- Default, new child, and adopted child launches; bare `prime.exe`, Bridge,
  and existing batch routes; guest and linked-account restarts.
- Missing marker, malformed marker, missing or corrupted receipt, path
  mismatch, missing established preference bin, and unavailable profile hook
  must fail before shared login state is touched.
- A hand-created or Bridge-created marker must enroll durably on first
  launch. A copied install claiming the same ID must stop until a deliberate
  rebind or new-profile operation is designed.
- Bridge must block marker provisioning while the target game is running or
  process attribution is uncertain, including when the game was started through
  an alias of that folder. Adoption and metadata changes must not rewrite an
  existing marker. A marked folder remains ineligible for Default after its
  Bridge metadata is removed.
- Update a child through the official launcher, then verify both profile
  bindings and mod/client compatibility before allowing the next launch.
- Confirm path identity under case differences, junctions, and moved installs;
  refuse ambiguous aliases instead of matching by folder name alone.

This marker is an accidental-cross-account safety contract, not a defense
against someone who can deliberately edit the local mod and enrollment files.
The current runtime is Windows-only; macOS requires its own hook and storage
implementation and client-specific validation.
