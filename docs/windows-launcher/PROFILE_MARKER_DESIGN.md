# Per-install profile marker design

Status: design proposal for enrollment and recovery. A Windows mod science
branch now reads and enrolls markers; Bridge can adopt and launch an already
marked install, but does not create markers or receipts.

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

| Marker | Receipt for this install | Behavior with a loaded profile-capable DLL |
|---|---|---|
| Absent | Absent | Existing default or batch behavior; no new interruption. |
| Valid | Absent | Atomically enroll this hand-created marker before login, then isolate. Fail if enrollment cannot be saved. |
| Valid | Matching | Isolate under that profile ID. |
| Absent or invalid | Present | Stop before login; do not fall back to Default. |
| Valid | Mismatched or invalid | Stop before login. |

The future Bridge enrollment flow must write and verify both files before a
new profile is offered for launch. The mod can enroll a valid hand-created
marker on its first launch.
Both writers need the same path canonicalization, file contract, and
cross-process lock; interrupted writes must leave a state that stops rather
than routes to Default. No marker and no receipt remains the ordinary mod
path, including the existing `STFC_MOD_ISOLATED_PROFILE` environment selector.
Complete loss of *both* marker and receipt cannot be distinguished from an
ordinary install; this is a practical accidental-loss guard, not a guarantee
against deletion of all state.

The same profile ID may be deliberately bound to more than one install, such
as a copied game folder. Both installations then use the same account state
and browser profile; simultaneous login can trigger the game's normal
single-session Retry behavior. Bridge should make this reuse visible, not
silently claim a new account was created. A new account requires a new ID.
Removing a Bridge UI profile does not silently delete game files, marker, or
receipt; disenrollment is a separate explicit operation.

## Selector precedence and effects

- An enrolled install uses the receipt and marker ID. If a launch-time
  `STFC_MOD_ISOLATED_PROFILE` value is also present, it must match exactly or
  launch stops. The environment variable is useful for existing batch users
  and for installations that intentionally share game files.
- A non-enrolled, unmarked install preserves today's behavior. A present but
  malformed marker never falls back to Default. If the mod DLL is absent,
  a bare `prime.exe` uses ordinary unmodded behavior; the marker is inert by
  player choice. A DLL that is present but fails to load may instead prevent
  startup. Neither case proves profile isolation. Bridge must verify the DLL
  before a Bridge-managed profile launch and report startup failures clearly.
- A valid selected ID namespaces the current `PlayerPrefs` login state and
  selects the isolated sign-in browser profile. It should also select the
  profile's mod TOML, native log, vars, and other mod-owned per-profile files
  without relying on `-ccm`. A newly created ID starts as a fresh device and
  requires sign-in; adding a marker to the existing Default install is not an
  implicit migration of its login. Leave Default unmarked unless a deliberate
  conversion is designed.
- The marker/environment mismatch rule is firm. `-ccm` is a mod-file path,
  not an identity selector, but an enrolled launch must not silently load
  another profile's TOML or sync settings. Whether marker plus `-ccm` accepts
  only the derived profile path or permits validated custom paths remains an
  explicit compatibility decision. Without a marker, legacy `-ccm` behavior
  remains unchanged.
- Unity's `Player.log` is separate from the mod's native log. Its path may be
  fixed before the DLL can read the marker. Bridge can supply `-logFile` when
  it starts the game. Bare marker launches may still share Unity's default
  `Player.log`; separate Unity logs are parked, not part of the marker gate.

Bridge-generated and adopted IDs now follow the mod's lowercase ID rules.
Names may change; IDs do not.

## Required validation before use

- Default, new child, and adopted child launches; bare `prime.exe`, Bridge,
  and existing batch routes; guest and linked-account restarts.
- Missing marker, malformed marker, missing or corrupted receipt, path
  mismatch, environment mismatch, and unavailable profile hook
  must fail before shared login state is touched.
- A hand-created marker must enroll durably on first launch; copied installs
  with the same ID must be identified as the same account, not a new one.
- Update a child through the official launcher, then verify both profile
  bindings and mod/client compatibility before allowing the next launch.
- Confirm path identity under case differences, junctions, and moved installs;
  refuse ambiguous aliases instead of matching by folder name alone.

This marker is an accidental-cross-account safety contract, not a defense
against someone who can deliberately edit the local mod and enrollment files.
