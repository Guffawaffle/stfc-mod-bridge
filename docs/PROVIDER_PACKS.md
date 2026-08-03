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
The resolved channel object—not the provider's default—is carried through
startup into repository/manifest discovery and the coordinator's exact channel
argument.

An unknown, withdrawn, or malformed persisted selection starts a restricted
recovery shell instead of terminating the launcher. Provider-bound mod and
Settings actions are disabled, the resolution reason is visible, launcher
self-update remains independent, and Source remains available so a known
provider can be selected.

Changing preferred source and switching the installed mod are different
operations under the
[mod source-selection lifecycle](windows-launcher/MOD_DEPLOYMENT.md#mod-source-selection-lifecycle):

- **Select source** atomically changes only `provider-selection.json`. It
  compares capabilities, requires explicit stable-ID confirmation when risk is
  present, never claims the installed artifact came from the target, performs
  no network discovery, and takes effect after restarting Mod Bridge.
- **Switch installed mod** is a separately confirmed, game-closed migration
  built from an explicit Check-for-updates observation. It requires the
  protected TOML-backup and cross-domain transaction contract before changing
  artifact lineage or installed attribution.

The existing `LauncherProviderSourceSwitchService` and selector are prototype
scaffolding, not the accepted production switch implementation. They currently
copy plaintext TOML beneath `provider-switch-backups`, expose the resulting
path, and lack protected metadata, retention, restore, a common mutation lease,
and a recovery journal. Follow-up work must split preference persistence from
artifact migration and replace that copy path with the reviewed protected
backup store. The active TOML must remain byte-untouched unless the player later
chooses an explicit restore; automatic source migration or normalization is
not accepted.

Staged Settings edits block switch review. This avoids discarding an in-memory
workspace whose catalog belongs to the current provider.

## Authority boundary

The launcher may bundle last-known-good packs for offline startup, but each mod
repository remains authoritative for production runtime truth. Provider packs
can select and authenticate mod artifacts only. Launcher self-update uses the
standalone repository, manifest name, and publisher authority declared by
`LauncherSelfUpdateAuthority`; provider data cannot redefine any of them.

## Maintaining packs

Pack changes require:

1. a schema-compatible JSON change or a new schema version;
2. source evidence for every claim changed to `supported`;
3. loader and neutral-fixture tests;
4. source-switch corpus tests proving comments and unknown TOML survive
   byte-for-byte;
5. a review of mod trust, withdrawal, sparse-TOML, backup, and rollback risks.
