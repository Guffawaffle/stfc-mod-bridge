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
4. Record `presentationSettingRemovals` for reviewed entries absent or no longer
   directly editable in that exact revision. The loader rejects every other
   stale, unknown, duplicate, hidden, or missing presentation path.
5. Bump the catalog revision and keep every materialized directly editable
   setting covered exactly once.
6. Preserve the exact-applicability, semantic-layout, placeholder-copy,
   accessibility, family, projection, and packaged UI Automation tests.

Stable `1.1.6.0` at `e80a303a9949c89100b6e59b8a5e5cc2271e7144`
currently materializes 204 previously reviewed catalog settings and 155
directly editable rows. Its newly introduced ship-scale, upgrade-confirmation,
instant-warp, and HUD-mode settings remain unknown and byte-preserved until
their typed presentation review lands in issue #217. The older stable `1.1.4`
and retained dev `1.1.5.1` reviews remain available only for their exact release
identities. The historical stable `1.1.4` certification is normalization-only:
it can classify an older exact ownership receipt for a safe update, but it is
not selected as the current installable release.
