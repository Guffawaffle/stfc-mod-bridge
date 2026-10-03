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
| Release discovery | Repository release manifest plus exact reviewed fallback if the manifest asset is absent | Latest stable release from the configured NetniV GitHub repository; exact recorded tag for repair |
| Windows artifact trust | SHA-256 plus exact Authenticode subject and durable Artifact Signing identity EKU; fallback ZIP/DLL hashes are also pinned | GitHub repository-release authority plus retained exact release/asset, ZIP, DLL, and embedded-version identities |
| Runtime manifest | Bundled verified fixture | Unknown |
| Configuration schema | Bundled verified fixture | Exact reviewed release/source catalogs; unreviewed identities remain unavailable |
| Withdrawal | Repository-reported release state; no authenticated withdrawal contract | Unknown |
| Migration compatibility | Same-provider TOML preservation | Cross-provider compatibility unknown |

NetniV is the default source for a new launcher-owned selection. Existing
explicit selections are preserved. NetniV stable uses the
`github-repository-release` provider policy: the GitHub HTTPS API must identify
`netniV/stfc-mod`, repository ID `693298224`, and owner ID `9052188`. An explicit
release check selects the latest non-draft, non-prerelease release and exactly
one uploaded `stfc-community-mod.zip` at its canonical release URL. The asset
must have a valid SHA-256 digest and bounded size. Bridge downloads and verifies
the archive, requires exactly one bounded root `version.dll`, resolves the tag
to its source commit, and checks that the DLL's Windows version equals the
canonical four-part release tag.

The prepared operation retains release ID, asset ID, tag, source commit, URL,
archive size/digest, and DLL size/digest/version. Execution may download those
exact retained bytes; it does not select `latest` again. A changed release or
asset observation fails closed rather than replacing an earlier preparation.
The installed receipt retains that repository-release observation and the
provider/channel/runtime release floor. Exact-tag repair queries the recorded
release and must reproduce the recorded DLL and, when present, the full retained
release observation. It never substitutes the latest release.

This policy trusts NetniV's configured GitHub repository and release-asset
control. Attestations are optional additional provenance, not an admission
prerequisite; this route does not claim verified attestation or Authenticode
evidence. Routine newer stable releases do not require a Bridge certification
catalog update. Bundled exact NetniV certifications remain historical identity
and release-order evidence for matching older receipts; they do not select or
authorize the current release. Runtime-manifest capability, configuration
catalog applicability, migration compatibility, and authenticated withdrawal
remain separate contracts.

Guffawaffle stable releases publish a release manifest, signed DLL, runtime
manifest, and compatibility ZIP. The release
manifest remains integrity/discovery metadata with
`manifestAuthenticity.scheme: none`; it does not authorize newer bytes by
itself. Mod Bridge permits a newly published DLL only after its live bytes pass
the exact Authenticode publisher and durable Artifact Signing identity policy;
routine signed releases do not require a Bridge catalog update. The reviewed
entry remains the narrow missing-manifest fallback and the authority for its
exact runtime-manifest pair. An invalid or tampered manifest never falls back.
The manifest's active/withdrawn state is a repository assertion, not an
authenticated withdrawal instruction. Ordinary update preparation therefore
also retains the highest signed ProductVersion and exact accepted DLL digest
recorded for each managed
installation and stable provider/channel/runtime tuple, including across a
switch to another provider and back. Channels retain independent floors rather
than being compared across channel identities. Both preparation and the locked
deployment boundary reject an older advertised or stale-prepared release. An
equal release order must match both the canonical signed tag and retained
digest, so mutable repository metadata cannot silently authorize a same-tag
rebuild. An intentional downgrade requires a separate explicit recovery design; Remove or
Stop managing explicitly ends that installation's retained release history.

An optional `runtimeManifest` member in a launcher-bundled reviewed release
certification authorizes one exact `stfc-runtime-manifest.json` companion by
file name, size, and SHA-256. When present in the compatibility ZIP, the archive
must contain exactly the certified root DLL and that certified root runtime
manifest; a missing, changed, duplicate, nested, or additional entry fails
closed. The separately downloaded runtime-manifest asset must match the same
certification. These checks compose with the DLL, version, repository, tag,
source-commit, provider, channel, and runtime-distribution binding. The mod's
schema-v1 release manifest cannot activate runtime capabilities on its own. A
newer signed DLL can therefore be installed without its unsigned runtime
manifest becoming operational; runtime capability activation remains limited
to an exact launcher-reviewed DLL/JSON pair.

`providers/known-windows-artifacts.v1.json` separately recognizes reviewed
stable and dev DLL hashes for local provenance display. The dev entry is
recognition-only because GitHub Actions artifacts expire and are not a durable
anonymous install source. Updating this snapshot requires review; discovering
or installing a new repository release does not add it to the snapshot. These
historical observations do not grant publisher-signature, configuration,
runtime-manifest, withdrawal, or migration evidence. NetniV's current install
authority is the repository-release policy above, independent of this catalog.

Manual observation refreshed 2026-08-20:

| Track | Reviewed source reference | Download/container SHA-256 | `version.dll` SHA-256 |
|---|---|---|---|
| Guffawaffle stable 2.1.0-guffa.9 | signed merge commit `5b5919cfb59dbe736be775adf7076b8f525bc067` | two-file release ZIP `4D978DDE0F855C6DCD894BECB34D4BE690F4CF00918398BB237B4AFD2C78E9D4` | `FF1DE2F6BD17E54760C75F7E94CA3FA6F01A380AD6C03DDFD98C0AF84910B80A` |
| NetniV stable 1.1.6.0 | unsigned tag commit `e80a303a9949c89100b6e59b8a5e5cc2271e7144`; GitHub release metadata observed mutable | release ZIP `9FDEA8CF4DD25D90A58EEE82952627D97A1B409B899F246C7594D5DC367D20B9` | `6B4C201D70AF8A00380AF3C07211051C571256640621063FC219A66785BFE4D9` |
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

The installed provider and preferred source are independently captured. A
reviewed switch can replace changed bytes at an installation with an existing
managed receipt without first relabeling or discarding that receipt. Review
binds the complete prior receipt, the current DLL identity and metadata, and
the current optional runtime-manifest identity. The current custom bytes
become the new adoption backup and uninstall restore target. An older adoption
backup remains separately recorded in the same atomic registry update.
Rollback restores the current custom bytes, original receipt, source preference,
and exact TOML, and removes only the detached record created by this switch.

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
