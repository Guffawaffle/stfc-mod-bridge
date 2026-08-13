# Windows Installation Discovery and Health

Status: current discovery and selection contract

The original WL-002 issue, branch, and base revision are historical delivery
evidence classified by [CURRENT_AUTHORITY.md](CURRENT_AUTHORITY.md). They are
not current routing instructions.

## Discovery boundary

Discovery evaluates only explicit candidate paths. It never recursively scans a
drive or guesses that an official-launcher directory is the game directory.

Evidence sources, from strongest to weakest, are:

1. a path the user previously confirmed;
2. the official launcher's exact `*.GAME_PATH` value in
   `%LOCALAPPDATA%\Star Trek Fleet Command\launcher_settings.ini`;
3. an explicit `STFC_GAME_DIRECTORY` process-environment override;
4. bounded conventional paths below Local AppData, Program Files, and
   `<system-drive>\Games`.

Duplicate paths are compared case-insensitively, merged, and retain every
evidence record. The strongest evidence determines the displayed confidence.

Manual selection evaluates exactly the chosen folder. It does not silently
append `default\game` or descend into children.

## Validation

A valid game target:

- is a valid, existing directory; and
- directly contains `prime.exe`.

A directory containing official-launcher markers or a `default` child but no
`prime.exe` is reported specifically as an official-launcher directory. Missing
and malformed paths remain distinct failure states.

Validation is read-only. It does not open, hash, execute, or modify
`prime.exe`.

## Confirmed selection

Only a valid user-selected folder is persisted. The launcher stores it at:

```text
%LOCALAPPDATA%\STFC Mod Bridge\install-selection.json
```

The document is versioned and written through a same-directory temporary file.
Every read revalidates the directory and `prime.exe`; a malformed document,
unsupported schema, missing directory, or removed executable fails closed and
asks for a new selection.

Selection is navigation, not ownership. Changing `install-selection.json`
changes only the installation Bridge is displaying and targeting. It never
moves, relabels, repairs, removes, or reassigns another installation's managed
receipt. Managed ownership is keyed independently by canonical game directory
as defined in [MOD_DEPLOYMENT.md](MOD_DEPLOYMENT.md).

The normal launcher UI reports whether the game folder is set and valid without
rendering its filesystem path. Candidate and per-user launcher paths are hidden
as well so that a streamed or shared launcher window does not disclose local
directory or account names. The selected path remains available internally for
validation, persistence, and choosing a replacement folder.

## Composable health

The launcher reports independent health dimensions instead of hiding one state
behind another:

- process safety: whether STFC is running;
- installation selection: missing, valid, unreadable, or no longer valid;
- discovery: how many bounded candidates currently validate.

For example, a confirmed installation can remain healthy while process safety
blocks future mutations because the game is running. The Home presentation is
a summary only; downstream mutation policy must evaluate the individual
dimensions.

## Home presentation

`LauncherHomePresentation` resolves the internal snapshot into the compact,
outcome-oriented Home contract without weakening the composable model.

The Home surface shows:

- stable product copy rather than duplicating row-level state;
- game-folder state and contextual Set/Confirm/Change action;
- explicit Running or Not running game-client state;
- Refresh, About, and Light/Dark theme actions.

`prime.exe` start and stop notifications are event-driven. An unprivileged
Windows shell window-created signal identifies a new game process, and the
tracked process's exit signal detects shutdown. Both trigger a fresh
authoritative `IGameProcessInspector` check, so the Home updates without WMI or
a timer. The manual Refresh action remains as a fallback when event
subscription is unavailable.

The authoritative check resolves each candidate process's executable path and
attributes it to the exact validated installation. A `prime.exe` running from
another installation may coexist and does not block the selected target. If a
`prime.exe` path cannot be inspected safely, mutations fail closed.

Candidate counts, provenance, storage ownership, filesystem paths, and raw
health dimensions are intentionally absent. They remain internal inputs for
future structured logs and the explicit redacted diagnostic surface in
`WL-008`.

## Safety invariants

- No recursive drive scan.
- No game-directory write.
- No silent folder confirmation.
- No launcher-root-to-game-root coercion.
- Cancellation is observed between providers and bounded candidates.
- Invalid persisted state never falls back to an unverified enabled target.
- Canonical installation paths are compared case-insensitively on Windows;
  one canonical installation cannot appear twice in managed state.
