# Distribution provider packs

## Decision

The launcher is one Windows product. Guffawaffle and NetniV are runtime/mod
distributions selected through data, not separately compiled launcher flavors.
Provider identity and release channel are separate stable IDs. Display names
are presentation only and never select behavior.

The checked-in v1 contract consists of:

- `providers/provider-pack.schema.v1.json`, the portable JSON Schema;
- `providers/bundled-provider-catalog.v1.json`, the bounded pack index;
- one independently versioned JSON pack beneath each provider directory;
- compatibility corpus files beneath `providers/compatibility`;
- the strict `LauncherDistributionProviderCatalogLoader`, which rejects
  unsupported schemas, unknown properties, duplicate IDs, invalid GitHub
  coordinates, path-shaped asset names, and supported claims without the
  required evidence.

A resolved pack supplies:

| Concept | Purpose |
|---|---|
| Stable provider ID | Persistence, migrations, and capability lookup |
| Display name and description | User-facing source selection only |
| Stable channel IDs and repositories | Bounded artifact discovery |
| Runtime distribution/resource identity | Positive runtime detection |
| Configuration schema resource | Defaults, validation, and presentation |
| Capability status | `supported`, `unsupported`, or explicit `unknown` |
| Artifact policy | Required hash and publisher evidence |
| Withdrawal policy | How a previously offered artifact is revoked |
| Migration policy | Format, unknown-TOML preservation, compatibility edges |

Missing capability IDs resolve to `unknown`. `unknown` never means false and
never means probably supported: the corresponding install, edit, or migration
path fails closed and the source-switch preview names the unknown evidence.

## Current capability matrix

This matrix documents the current bundled evidence, not an aspiration.

| Capability | Guffawaffle | NetniV |
|---|---|---|
| Stable provider ID | `guffawaffle` | `netniv` |
| Stable channel | `stable` | `stable` |
| Release repository | `Guffawaffle/stfc-mod` | `netniV/stfc-mod` |
| Release discovery | Signed launcher release manifest | GitHub asset name known; verified discovery contract unknown |
| Windows artifact trust | SHA-256 plus Authenticode publisher | Unknown; install/update disabled |
| Runtime manifest | Bundled verified fixture | Unknown |
| Configuration schema | Bundled verified fixture | Unknown; settings editing disabled |
| Withdrawal | Signed release-manifest policy | Unknown |
| Migration compatibility | Same-provider TOML preservation | Cross-provider compatibility unknown |

The NetniV pack intentionally records the currently observable repository and
Windows ZIP asset without inventing a publisher, generated configuration
schema, runtime manifest, hash contract, or withdrawal mechanism. Publishing
those contracts upstream can turn individual fields from `unknown` into
`supported` without adding a NetniV-specific WPF branch.

## Launcher-owned source selection

The selected `{ providerId, releaseChannelId }` is stored in
`provider-selection.json` under launcher state. It is never written to
`community_patch_settings.toml`. Startup resolves the persisted stable IDs;
an unknown provider or channel does not silently fall back to the default.

Changing source is a small transaction:

1. Resolve both providers and compare every contract capability.
2. Report supported-to-unsupported loss and every unknown target capability.
3. Hash the selected TOML and require a separate confirmation action.
4. Verify the hash is still current and copy the exact bytes to
   `provider-switch-backups/<transaction>.toml`.
5. Atomically replace only `provider-selection.json`.
6. If the state write reports failure, restore the previous effective
   selection. The active TOML is never normalized or rewritten.
7. Require a launcher restart before the newly selected provider composes mod
   discovery, artifact trust, runtime activation, or configuration editing.

Staged Settings edits block switch review. This avoids discarding an in-memory
workspace whose catalog belongs to the current provider.

## Authority boundary

The launcher may bundle last-known-good packs for offline startup, but each mod
repository remains authoritative for production runtime truth. Provider packs
can select and authenticate mod artifacts only. Launcher self-update uses the
repository and publisher authority declared by `LauncherSelfUpdateAuthority`;
provider data cannot redefine either. Issue #4 owns moving that
launcher-controlled feed to standalone launcher releases.

## Maintaining packs

Pack changes require:

1. a schema-compatible JSON change or a new schema version;
2. source evidence for every claim changed to `supported`;
3. loader and neutral-fixture tests;
4. source-switch corpus tests proving comments and unknown TOML survive
   byte-for-byte;
5. a review of mod trust, withdrawal, sparse-TOML, backup, and rollback risks.
