# Google Cloud update hosting

STFC Mod Bridge uses a public Google Cloud Storage bucket as a static App
Installer feed. It does not require a VM, a web application, or a persistent
cloud credential. GitHub exchanges the protected `windows-release` environment's
OIDC token for a short-lived Google Cloud credential through Workload Identity
Federation (WIF).

## Object contract

The public HTTPS base URI maps these object names without rewriting:

```text
packages/v<semantic-version>/STFCModBridge.msix
preview/STFCModBridge.appinstaller
stable/STFCModBridge.appinstaller
```

Versioned MSIX paths are immutable. Publication creates the package with a
zero-generation precondition and refuses success if existing bytes have a
different SHA-256. Only a channel's small App Installer descriptor moves. The
workflow refuses to replace a descriptor with a lower numeric MSIX version.

The public endpoint must preserve these response contracts:

- MSIX: `Content-Type: application/msix`, HTTP byte ranges, immutable caching;
- descriptor: `Content-Type: application/appinstaller`, revalidation/no-cache;
- all objects: HTTPS and public anonymous reads.

`scripts/publish-appinstaller-gcs.ps1` checks the public MSIX headers, a
1,024-byte range, the complete public SHA-256, and the final descriptor bytes
before reporting success.

## Protected GitHub values

Add these environment variables to the existing `windows-release` environment:

| Variable | Value |
|---|---|
| `GCP_PROJECT_ID` | Project containing the bucket and WIF provider |
| `GCP_UPDATE_BUCKET` | Bucket name only, without `gs://` |
| `GCP_UPDATE_SERVICE_ACCOUNT` | Service-account email used by WIF |
| `GCP_WORKLOAD_IDENTITY_PROVIDER` | Full provider resource name beginning with `projects/` |
| `MOD_BRIDGE_UPDATE_BASE_URI` | Public HTTPS origin/path that maps to the bucket root |

These are identifiers, not secrets. Keep the environment's required reviewer,
deployment restrictions, and self-review policy enabled because approval gates
both Artifact Signing and update-channel mutation.

## Least-privilege GCP shape

Use a dedicated service account. Bind GitHub's WIF principal only for
`Guffawaffle/stfc-mod-bridge` and its `windows-release` environment; do not grant
an entire workload identity pool access and do not download a service-account
JSON key.

At bucket scope, the service account needs:

- object creation for immutable versioned packages;
- object read/list for idempotence checks;
- object replacement only for the exact `stable/` and `preview/` descriptor
  prefixes.

A practical least-privilege design combines `roles/storage.objectCreator` for
the bucket with a conditional object-user binding limited to
`stable/STFCModBridge.appinstaller` and
`preview/STFCModBridge.appinstaller`. Public `allUsers` object-viewer access is
separate and read-only. If organization policy prevents public buckets, place a
public HTTPS load balancer/CDN in front while preserving paths, MIME types, and
range responses.

Enable uniform bucket-level access and object versioning or a retention policy.
The workflow's generation precondition is the primary write-once guard;
versioning/retention is recovery and administrative evidence, not a substitute
for protected GitHub/GCP identities.

## Publication order

The tag workflow builds and signs the inner launcher, builds and signs the MSIX,
inspects both layers, attests the exact release subjects, and stages a draft
GitHub release. A maintainer qualifies and publishes that immutable release.
Only then does `.github/workflows/publish-update-channel.yml`:

1. download the published MSIX and descriptor;
2. verify their tag-workflow attestations against the exact tag commit;
3. obtain a short-lived GCP credential through WIF;
4. create or verify the immutable package object;
5. verify the bytes through the public endpoint; and
6. advance and re-read the channel descriptor.

Release, attestation, immutable-package, and downgrade checks all pass before
the channel object is replaced. The final public byte check necessarily occurs
after that replacement. If it fails, the workflow remains red and the channel
must not be treated as qualified, although the uploaded descriptor may already
be publicly visible. Bucket object versioning preserves the prior generation
for operator recovery. A GitHub release may be visible briefly while this
post-publication check runs, so release notes direct testers to wait for the
update-hosting workflow to succeed before using the App Installer entry point.

## Trust boundary

The MSIX and its inner executable are Authenticode-signed by the reviewed
publisher, and Windows enforces package integrity. GitHub attestation binds the
released bytes to the repository, workflow, ref, and commit. GCS supplies the
availability and channel pointer. None of those claims proves that the source or
dependencies are free of malicious behavior.
