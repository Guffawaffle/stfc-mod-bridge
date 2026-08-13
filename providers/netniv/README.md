# NetniV provider evidence

`configuration-schema-set.v1.json` is a launcher-reviewed compatibility
adapter, not a schema published by NetniV and not a projection of the
Guffawaffle catalog. It materializes only an exact provider, track, release,
and full source-commit tuple listed in `revisions`; any adjacent identity fails
closed.

Runtime facts are reviewed from the matching NetniV `defaultconfig.h`,
`config.cc`, key mapping, README, and example configuration. The compact shared
settings and revision deltas own types, defaults, aliases, feature gates,
visibility, and runtime status.

The `presentation` profile is a separate catalog-owned layer. Labels, help,
groups, units, and hotkey families are derived from the exact upstream example
configuration's sections, subgroup markers, and player comments, with small
reviewed copy overrides where the source is ambiguous, stale, or only repeats
a default binding. Presentation may explain runtime facts but must not redefine
them. The profile selects the shared semantic Settings layout independently of
NetniV's currently unknown runtime-manifest capability.

When reviewing a new NetniV release:

1. Add a revision only after recording the exact release and source commit.
2. Reconcile runtime settings and deltas against that commit's source files.
3. Reconcile player-facing presentation against that commit's example/config
   documentation.
4. Bump the catalog revision and keep every directly editable shared setting
   covered exactly once. Unknown, duplicate, hidden, and missing presentation
   paths are rejected by the loader.
5. Preserve the exact-applicability, semantic-layout, placeholder-copy,
   accessibility, family, projection, and packaged UI Automation tests.

Stable `1.1.4` at `d912611fa1eca49fc54f363bdf8377dfebf8def0`
currently materializes 203 catalog settings and 155 directly editable rows.
The retained dev review is `1.1.5.1` at
`238004460c4bb93aa717e47c41089fe8b71c4cf9`.
