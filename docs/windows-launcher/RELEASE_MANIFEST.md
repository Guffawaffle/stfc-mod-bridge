# STFC Mod Bridge release manifest

Tagged releases in `Guffawaffle/stfc-mod-bridge` publish
`stfc-mod-bridge-release-manifest.json`. This repository is the only Mod Bridge
self-update authority. Distribution provider packs describe mod artifacts and
cannot supply or redirect the Mod Bridge repository, manifest name, publisher,
or update channel.

## Producer contract

`scripts/generate-launcher-release-manifest.ps1` derives the release version
and channel from the immutable tag, records the exact tagged commit, and hashes
the already signed artifacts. It emits exactly two stable artifact identities:

| ID | Role | User-facing? |
|---|---|---|
| `windows-mod-bridge-setup-x64` | Signed per-user setup executable | Yes; the only install asset |
| `windows-mod-bridge-archive-x64` | ZIP containing signed Mod Bridge and its replace-on-exit helper | No; machine-consumed self-update input |

The manifest itself is also machine-consumed metadata. The package inspection
gate checks that the setup directory contains only
`STFCModBridge.Setup.exe`, that every required input is a PE, and
that the update archive has exactly one root launcher and updater executable.
The protected tag workflow repeats the inspection while requiring the reviewed
Authenticode publisher.

After those checks, the protected job generates GitHub/Sigstore provenance for
the final setup, update archive, manifest, launcher, and updater bytes. It
publishes the signed bundle as
`stfc-mod-bridge-release-attestation.json`. The separate publication job
reverifies every subject against the exact repository, release workflow, tag,
and source commit before creating the release. See
[`ARTIFACT_ATTESTATIONS.md`](ARTIFACT_ATTESTATIONS.md).

## Consumer and authority boundary

Mod Bridge uses `GitHubLauncherReleaseClient`, which is separate from the
provider-bound mod discovery client. It accepts only HTTPS GitHub releases in
`Guffawaffle/stfc-mod-bridge`, derives immutable asset URLs from the exact tag
and basename, requires the standalone manifest name, validates the closed v1
schema, and selects exactly one supported update archive. A Mod Bridge-only
manifest does not need or accept an implied mod authority.

The archive size and SHA-256 must match before extraction. Extraction is
bounded and rejects traversal, links, duplicate identities, and unsafe paths.
Both extracted PEs must pass Authenticode for the Mod Bridge-owned publisher, and
the signed application's embedded source revision must match the manifest's tagged
commit.

## Replay and withdrawal policy

Stable and preview are distinct; the normal application requests stable. A
candidate must numerically advance the running Mod Bridge version. Equal or lower
versions fail closed before download, which prevents ordinary downgrade and
replay after an update.

An affected release is withdrawn only after a higher signed replacement is
published. The release and tag are then removed and the action is committed to
`docs/release-withdrawals/release-withdrawals.jsonl`. Immutable historical
manifests are never rewritten. Healthy installed payloads remain usable
offline, and withdrawal never deletes local files.

Schema v1 still declares `manifestAuthenticity.scheme: none`. The producer now
publishes independently verifiable attestation evidence for the manifest and
final binaries, while Authenticode independently authenticates executable
publisher/integrity. The running Bridge does not yet consume that attestation
as release-selection authority, so this is not represented as a completed
authenticated-manifest design. Native verification and replay/rotation policy
remain issue #71.

## Failure behavior

- Unknown schema, tag form, channel, repository, artifact identity, platform,
  architecture, or authenticity declaration fails closed.
- Drafts, the opposite channel, missing manifests, withdrawn manifests, and
  non-advancing versions are ineligible.
- HTTP, size, hash, extraction, source-identity, or Authenticode failure leaves
  the installed program directory untouched.
- Update discovery is explicit and must never block launching an already
  healthy local installation.
