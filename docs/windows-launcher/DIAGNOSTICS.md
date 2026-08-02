# Windows Launcher Diagnostics and Repair

Status: WL-008 redacted preview/export and allowlisted recovery, repair, and removal UX are implemented; installed-client maintenance smoke remains pending.

## Health and next actions

The explicit Diagnostics dialog resolves game selection, local mutation
preconditions, game process state, official-launcher availability,
launcher-managed `version.dll` identity, transaction state, TOML structure,
and recent mod-log presence. Every attention or unavailable fact includes a
bounded next action. Normal Home remains path-free; the exact game target is
shown only in the destructive maintenance confirmation.

Repair reuses canonical release discovery and the full size, SHA-256,
Authenticode publisher, and embedded-version transaction checks. Recovery
rolls back only the paths recorded by a validated incomplete journal. Removal
accepts only a matching launcher-managed DLL and restores the originally
adopted manual artifact after any number of managed updates. Unknown or
externally changed artifacts are never guessed at or deleted.

## Preview and export

Diagnostics are generated locally and shown before copy or export. Export
writes exactly the previewed UTF-8 JSON through an atomic same-directory
replacement. There is no network or automatic-upload path.

The document contains bounded health facts and at most 64 KiB / 200 lines from
the end of `community_patch.log`. It never includes TOML values, arbitrary
environment variables, authentication stores, or unrelated files. The
redactor replaces the selected game root and user profile, keyed token/secret/
cookie values, bearer authorization, and every HTTP(S) endpoint.

## Evidence

Fixture tests prove token, bearer value, private path, private endpoint, and
private TOML value exclusion; bounded local fact generation; and exact export.
Deployment tests prove explicit repair, operation locking, allowlist-only
uninstall, adopted-artifact restoration across managed updates, and failure
rollback. Packaged UI Automation opens the preview, reads its health facts,
asserts that raw profile/game paths are absent, closes it, and verifies that
the active TOML remains byte-for-byte unchanged.
