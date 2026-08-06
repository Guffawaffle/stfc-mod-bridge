# STFC Mod Bridge release manifest

Tagged releases in `Guffawaffle/stfc-mod-bridge` publish
`stfc-mod-bridge-release-manifest.json`. This repository is the only Mod Bridge
self-update authority. Distribution provider packs describe mod artifacts and
cannot supply or redirect the Mod Bridge repository, manifest name, publisher,
or update channel.

## Producer contract

`scripts/generate-launcher-release-manifest.ps1` derives the release version
and channel from the immutable tag, records the exact tagged commit, and hashes
the already signed artifacts. Schema v2 also receives the positive GitHub
workflow run number as its monotonic release sequence and one explicit
whole-second UTC issue time. Given those inputs, artifact bytes, and the reviewed
withdrawal ledger, generation is deterministic. It emits exactly two stable
artifact identities:

| ID | Role | User-facing? |
|---|---|---|
| `windows-mod-bridge-archive-x64` | ZIP containing signed Mod Bridge and its replace-on-exit helper | No; machine-consumed self-update input |
| `windows-mod-bridge-msix-x64` | Signed Windows package containing the signed launcher | No; installed through the App Installer descriptor |

The authenticated v2 metadata contract is:

| Field | Contract |
|---|---|
| `schemaVersion` | Exact integer `2` |
| `releaseSequence` | Positive GitHub workflow run number; compared monotonically per channel |
| `issuedAt` / `expiresAt` | Whole-second UTC RFC 3339; stable is valid for at most 45 days and preview for at most 14 days |
| `manifestAuthenticity.scheme` | Exact `github-sigstore-build-provenance-v1` |
| `withdrawals` | Canonically ordered additive selectors for a release sequence, manifest SHA-256, or artifact SHA-256 |

The consumer allows ten minutes of clock skew, at most one hour between
`issuedAt` and the verified Rekor integrated time, and treats a local clock more
than 24 hours behind its persisted observation floor as a material rollback.

The manifest itself is also machine-consumed metadata. The package inspection
gate checks that the signed MSIX has the reviewed identity, publisher,
full-trust desktop declarations, content-integrity enforcement, and exactly one
inner PE. It also checks that the fallback archive has exactly one root launcher
and updater executable.
The protected tag workflow repeats the inspection while requiring the reviewed
Authenticode publisher.

After those checks, the protected job generates GitHub/Sigstore provenance for
the final MSIX, App Installer descriptor, update archive, manifest, SBOM,
launcher, and updater bytes. It
publishes the signed bundle as
`stfc-mod-bridge-release-attestation.json`. The separate publication job
reverifies every subject against the exact repository, release workflow, tag,
and source commit before creating the release. See
[`ARTIFACT_ATTESTATIONS.md`](ARTIFACT_ATTESTATIONS.md).

The producer also publishes the fixed-name
`stfc-mod-bridge-release-selection-attestation.json`. That bundle is generated
by a separate attestation action invocation over the manifest path alone. Before
draft staging, the workflow requires exactly one verified attestation result and
exactly one statement subject whose basename and SHA-256 match the transferred
manifest. The broad bundle remains the independent evidence for all final
release subjects; the manifest-only bundle is the unambiguous input reserved for
the future #71 consumer policy.

## Consumer and authority boundary

Mod Bridge's standalone path uses
`AuthenticatedGitHubLauncherReleaseClient`, separate from provider-bound mod
discovery. It treats tag/draft/prerelease/asset-name data only as discovery,
derives fixed HTTPS manifest and bundle URLs, invokes a prevalidated installed
verifier, independently rehashes the same files, and applies v2 policy before
projecting an archive selection. The legacy unauthenticated standalone client
has been removed. A Mod Bridge-only manifest does not need or accept an implied
mod authority.

The archive size and SHA-256 must match before extraction. Extraction is
bounded and rejects traversal, links, duplicate identities, and unsafe paths.
Both extracted PEs must pass Authenticode for the Mod Bridge-owned publisher, and
the signed application's embedded source revision must match the manifest's tagged
commit.

## Replay and withdrawal policy

Stable and preview have independent monotonic floors. A v2 candidate must
advance the installed semantic version and may not fall below either the highest
accepted sequence or semantic version for its channel. Reobserving the exact
same sequence is allowed only when the tag, source commit, manifest/bundle
digests, trust epoch/root digest, and complete withdrawal set are identical.
Any rebinding fails closed.

Accepted floors are stored under the external Mod Bridge state root in
`authenticated-release-state.v1.json`. Updates take an exclusive state lock,
write and flush a bounded temporary document, and atomically replace the prior
file while retaining `authenticated-release-state.v1.previous.json` as recovery
evidence. A malformed or missing primary state never silently enrolls the older
backup or resets a floor; new update authorization fails closed while the
installed application remains usable.

Emergency containment freezes publication immediately and records the affected
identity in `docs/release-withdrawals/release-withdrawals.jsonl`; it does not
wait for a replacement. Every later manifest for that channel must retain each
prior withdrawal selector. A higher independently verified manifest publishes
the additive denial to online clients; offline clients cannot learn it until
they receive newer authenticated evidence. Healthy installed payloads remain
usable, and withdrawal never deletes local files. See
[`COMPROMISE_RESPONSE.md`](COMPROMISE_RESPONSE.md) for the operator procedure.

Schema v1 declares `manifestAuthenticity.scheme: none` and remains ineligible
for authenticated self-update. Schema v2 declares the exact GitHub/Sigstore
scheme and is produced before its manifest-only attestation. Issue #96 adds the
authenticated discovery/evidence/state consumer and removes the legacy
standalone v1 client from application composition. Authorization remains
unavailable in integration builds until issue #97 packages the signed,
digest-paired verifier and implements final external-updater revalidation.

## Failure behavior

- Unknown schema, tag form, channel, repository, artifact identity, platform,
  architecture, or authenticity declaration fails closed.
- Drafts, the opposite channel, missing manifests, withdrawn manifests, and
  non-advancing versions are ineligible.
- HTTP, size, hash, extraction, source-identity, or Authenticode failure leaves
  the installed program directory untouched.
- Update discovery is explicit and must never block launching an already
  healthy local installation.
