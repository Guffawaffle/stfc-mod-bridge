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
| Artifact policy | Required hash and trust evidence kind |
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
| Release discovery | Repository release manifest constrained by the exact launcher-reviewed current release; exact reviewed fallback if the manifest asset is absent | Exact launcher-reviewed GitHub release asset |
| Windows artifact trust | SHA-256 plus exact Authenticode subject and durable Artifact Signing identity EKU; fallback ZIP/DLL hashes are also pinned | Temporary launcher-reviewed ZIP and DLL SHA-256 allowlist |
| Runtime manifest | Bundled verified fixture | Unknown |
| Configuration schema | Bundled verified fixture | Unknown; settings editing disabled |
| Withdrawal | Signed release-manifest policy | Unknown |
| Migration compatibility | Same-provider TOML preservation | Cross-provider compatibility unknown |

NetniV is the default source for a new launcher-owned selection. Existing
explicit selections are preserved. Until NetniV publishes the open provenance
contract, stable install/update is authorized only when GitHub's current latest
release and downloaded ZIP exactly match the manually reviewed entry in
`providers/reviewed-windows-releases.v1.json`. The archive must contain exactly
one root `version.dll`; both archive and DLL size/SHA-256 plus the DLL version
are checked before the atomic deployment transaction commits. A newer or
changed release fails closed until a maintainer reviews and updates the entry.

The current Guffawaffle stable release, `v2.1.0-guffa.9`, publishes the release
manifest, signed DLL, runtime manifest, and compatibility ZIP. The release
manifest remains integrity/discovery metadata with
`manifestAuthenticity.scheme: none`; it does not authorize newer bytes by
itself. Mod Bridge therefore binds its repository, tag, source commit, ZIP,
DLL, and runtime-manifest bytes to the exact reviewed certification and still
requires the normal Authenticode publisher policy. It falls back to the
reviewed ZIP only if the release-manifest asset is absent. An invalid or
tampered manifest never falls back.

An optional `runtimeManifest` member in a launcher-bundled reviewed release
certification authorizes one exact `stfc-runtime-manifest.json` companion by
file name, size, and SHA-256. When present in the compatibility ZIP, the archive
must contain exactly the certified root DLL and that certified root runtime
manifest; a missing, changed, duplicate, nested, or additional entry fails
closed. The separately downloaded runtime-manifest asset must match the same
certification. These checks compose with the DLL, version, repository, tag,
source-commit, provider, channel, and runtime-distribution binding. The mod's
schema-v1 release manifest cannot activate runtime capabilities on its own. A
new producer release therefore remains unavailable until its exact DLL/JSON
pair and container are deliberately reviewed and the bundled certification is
updated.

`providers/known-windows-artifacts.v1.json` separately recognizes reviewed
stable and dev DLL hashes for local provenance display. The dev entry is
recognition-only because GitHub Actions artifacts expire and are not a durable
anonymous install source. Guffawaffle owns refreshing these temporary entries.
The upstream provenance contract will supersede this shim; no publisher,
configuration schema, runtime manifest, withdrawal policy, or migration
compatibility is inferred from the allowlist.

Manual observation refreshed 2026-08-13:

| Track | Immutable source | Download/container SHA-256 | `version.dll` SHA-256 |
|---|---|---|---|
| Guffawaffle stable 2.1.0-guffa.9 | signed merge commit `5b5919cfb59dbe736be775adf7076b8f525bc067` | two-file release ZIP `4D978DDE0F855C6DCD894BECB34D4BE690F4CF00918398BB237B4AFD2C78E9D4` | `FF1DE2F6BD17E54760C75F7E94CA3FA6F01A380AD6C03DDFD98C0AF84910B80A` |
| NetniV stable 1.1.4 | tag commit `d912611fa1eca49fc54f363bdf8377dfebf8def0` | release ZIP `EDC67ED72E4C942B08AB81D92D23B416F80E250CE5DB151FC4B7781C174D468C` | `020C975FD2391DF1814897B9D5F03A55443F99367EA6ACC4065AF7E240D9547A` |
| NetniV dev 1.1.5.1 | successful Actions run `30677057536` at `238004460c4bb93aa717e47c41089fe8b71c4cf9` | expiring Actions artifact `7DD716E85643F489A74E463A4A3B8604087338D3ED46E79F24F2FD439FA74732` | `6B0555C7052E3857B7441A6BE931AC0A21830F57886DC14AB9F2C69D3D9973EE` |

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

- **Select source**, when no DLL is installed, changes the provider preference
  and provider-scoped TOML profile without downloading or claiming an artifact.
  It compares capabilities, requires explicit stable-ID confirmation, and
  recomposes the provider-bound workspace inside the current process.
- **Switch installed mod** is a separately confirmed, game-closed migration
  built from fresh target release discovery. `LauncherProviderAtomicSwitchCoordinator`
  stages the target artifact and uses the deployment transaction's exact
  installation lease and rollback copy while `LauncherProviderSourceSwitchService`
  captures/restores protected provider TOML and commits stable provider IDs.
  The outer recovery journal reaches `Completed` before the prior managed DLL
  rollback copy is released. An interruption therefore resumes as one rollback
  of DLL, installed state, selection, and exact TOML bytes.
  After commit, Mod Bridge replaces the provider session in-process; the same
  window can immediately inspect the new source or switch back.

Provider history is DPAPI `CurrentUser` protected, DACL restricted, verified by
identity and SHA-256, and retained as the newest five records per provider and
validated installation. No plaintext backup path or payload is exposed in the
switch UI, logs, or diagnostics.

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
