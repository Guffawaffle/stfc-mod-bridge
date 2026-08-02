# Windows Launcher Sync Target Persistence

This document records the LS-004 persistence boundary for the launcher sync campaign. The implementation maps the
dependency-free desired topology to sparse TOML operations and delegates the only filesystem replacement to the
existing `AtomicTomlStore`.

## Pipeline

1. `SyncTopologyTomlAdapter` reads conservative TOML assignments into the desired domain without modifying bytes.
2. `SyncTopologyPersistencePlanner` compares the baseline and desired topology and produces bounded semantic
   mutations. Mutation display text contains paths and secret-state markers, never rendered token values.
3. `SyncTopologyPersistencePlan.Apply` re-loads the sparse document before every mutation and fails closed on the first
   unsupported or conflicting construct.
4. `SyncTopologyPersistenceWorkspace` submits the transformed bytes and exact baseline bytes to `AtomicTomlStore`.
5. The atomic store rechecks the destination, writes durably to a sibling temporary file, creates a backup, and replaces
   the destination. A stale or disappearing destination is a conflict and preserves the external file.

No sync change bypasses this boundary or writes one field at a time to the live file.

## Mapping

- Global values persist under `[sync]`.
- External targets persist under `[sync.targets.<name>]` and newly added or kind-changed targets receive explicit
  `mode = "legacy"` or `mode = "majel"`.
- Local Sidecar persists only under `[sidecar.sync]`.
- An inherited target override removes the target-local assignment.
- Explicit false and explicit empty proxy remain assignments.
- Unchanged tokens produce no mutation. Replacement and clearing are secret-bearing internal mutations whose display
  form is redacted.
- External targets cannot be persisted as disabled because the native schema has no canonical enabled field. The user
  must keep the target enabled or remove it.

## Structural operations

The sparse editor supports whole-table removal and rename for bare-key tables. Rename includes descendant tables and
preserves body bytes, comments, whitespace, line endings, BOM, ordering, and unknown fields. Removal deletes the owned
table and its descendants while retaining unrelated tables.

Both operations reject:

- a destination table or dotted-assignment collision;
- a source represented only through dotted assignments;
- duplicate tables or malformed source documents;
- unsupported quoted table paths and array-of-table syntax.

Declared target tables are inventoried independently of their assignments. An empty target table is therefore surfaced
as an invalid, incomplete target instead of disappearing from the launcher model.

## Kind changes and migration

Changing an external target kind requires one explicit policy:

- `PreserveCompatibleOverrides` keeps compatible target-local overrides.
- `ResetOverrides` requires the desired target to have no explicit overrides and persists their removal.

Legacy root `[sync].url` and `[sync].token` load as a virtual `default` target. An unchanged virtual target never rewrites
the source. Editing or renaming it is blocked until migration is explicitly confirmed; confirmed migration clears the
legacy root credentials and creates `[sync.targets.default]` atomically.

## Security and display

- `SyncSecret`, `SyncOverride<T>`, `SyncResolvedValue<T>`, and `SyncResolvedTarget` have redacted display forms.
- Resolved summaries expose only whether credentials are configured.
- Proxy values remain available to the editor but are absent from domain and plan `ToString()` output.
- Endpoint URLs containing embedded user information are rejected; credentials belong in the opaque token field.
- Invalid-value diagnostics name the canonical path and line, never the rendered value.
- Mutation factories accepting rendered values are assembly-internal.

## Workspace behavior

Staging changes does not touch disk or the startup runtime snapshot, and a semantic no-op does not create pending work.
Discard restores the baseline topology. Successful commit advances the baseline only after atomic replacement. Conflict
leaves the desired draft pending, marks the workspace stale, and preserves the external file without creating a backup;
staging or discarding cannot falsely clear that stale state.
