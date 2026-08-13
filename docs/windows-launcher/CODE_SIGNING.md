# Windows Release Signing

## Decision

Windows release tags use Azure Artifact Signing with the existing
`stfcsidecarsign` account and `stfc-sidecar-public` Public Trust certificate
profile. The reviewed publisher identity is:

- exact subject DN:
  `CN=Joseph Gustavson, O=Joseph Gustavson, L=Dousman, S=Wisconsin, C=US, PostalCode=53118`;
- Artifact Signing durable identity EKU:
  `1.3.6.1.4.1.311.97.664386437.910814316.510550690.722133748`;
- code-signing EKU: `1.3.6.1.5.5.7.3.3`.

The leaf certificate serial number, public key, and thumbprint are deliberately
not pinned. Azure renews Artifact Signing certificates daily and makes them
valid for only 72 hours. Microsoft identifies the subscriber-specific
`1.3.6.1.4.1.311.97.*` EKU as the durable identity value intended to survive
that leaf rotation. A future legitimate subject-DN or identity-validation
change therefore requires a reviewed policy update; it must not be accepted by
weakening the comparison to a display name.

The certificate profile is shared with the STFC Community Mod Companion so the
Windows launcher, proxy DLL, and companion present one verified publisher. The
launcher repository has its own Microsoft Entra application and GitHub OIDC
federated credential. Its `Artifact Signing Certificate Profile Signer` role is
scoped directly to `stfc-sidecar-public`.

No PFX, private key, or Azure client secret belongs in this repository or its
GitHub configuration.

## Protected release boundary

The GitHub environment is `windows-release`.

- Required reviewer: `Guffawaffle`
- Allowed branch for manual release work: `main`
- Allowed release tags: `v*`
- OIDC subject:
  `repo:Guffawaffle@105761663/stfc-mod-bridge@1320037274:environment:windows-release`
- Signing job permission: `id-token: write` and `attestations: write` with
  `contents: read`
- Draft-staging job permission: `contents: write` without `id-token`
- Post-publication GCS job permission: `id-token: write`, `attestations: read`,
  and `contents: read`, without GitHub contents-write authority

GitHub's issued subject includes the immutable owner and repository IDs. The
values above are the exact subject presented by the protected workflow and the
canonical prefix returned by the repository OIDC endpoint. Entra must match
that complete subject; the shorter human-readable repository coordinate is not
an equivalent federated credential.

Pull requests and ordinary branch builds remain unsigned. Only a tag build can
enter the signing job, and the protected environment requires approval before
GitHub issues the environment-scoped OIDC token.

## Signed artifacts

The standalone tag workflow signs and verifies:

- `STFCModBridge.exe`
- `STFCModBridge.ReleaseVerifier.exe`
- `STFCModBridge.Updater.exe`
- `STFCModBridge.msix` (after its inner launcher and verifier are signed)

Future executable release components must be added to an explicit signing
allowlist before release. Package inspection identifies PE files by their
actual `MZ`/`PE\0\0` headers rather than file extension, so a DLL or renamed PE
cannot evade that allowlist.

The release order is:

```text
locked restore/test -> unsigned build -> approve protected environment -> OIDC
      -> sign verifier -> embed final verifier SHA-256 -> rebuild launcher/updater
      -> regenerate verifier SBOM -> vulnerability/malware gates -> sign launcher/updater
      -> generate final payload SBOM
      -> package MSIX -> sign MSIX -> verify package, pairing, and inner signatures
      -> hash -> attest final subjects -> transfer -> reverify attestations -> stage draft
      -> maintainer classifies and publishes immutable release
      -> validate classification -> reverify published subjects -> GCP publish
      -> verify final public descriptor
```

The verifier is signed first because Authenticode changes its bytes; the final
verifier SHA-256 is embedded in the rebuilt launcher and its SBOM is regenerated
from those final helper bytes before the launcher
and updater are signed. The payload SBOM is regenerated after those signatures
and checks its SHA-256 file entries against all three final inner PEs. Packaging
and release checksums occur only after those inner signatures. The MSIX is then
signed as the outermost artifact and
enforces package-content integrity. The workflow verifies all inner and package
signatures with
SignTool's Authenticode policy and separately requires the exact subject DN,
both reviewed EKUs, and a trusted timestamp before manifest generation or
release publication. It then runs the signed launcher, release verifier, and
standalone updater through the runtime verifier, which enumerates every
signature and applies the complete consumer policy. A mixed-publisher secondary
signature therefore fails the release before its manifest or attestations are
created.

## Consumer verification contract

`WindowsAuthenticodeVerifier` applies the Windows generic Authenticode policy
with whole-chain revocation checking. Its ordinary `Verify` entry point sets
`WTD_CACHE_ONLY_URL_RETRIEVAL`, so a background install, update, or diagnostic
check cannot initiate certificate or CRL retrieval. An online evaluation is a
separate explicit mode and may be called only from a user-authorized network
flow.

Cached success and online-permitted success both report revocation freshness as
**not established**. Permitting retrieval is not evidence that Windows fetched
fresh CRL/OCSP material, and the launcher does not claim otherwise. The result
records the evaluation mode and time, not a fabricated revocation-freshness
time.

The verifier enumerates the primary signature and every secondary signature by
using `WINTRUST_SIGNATURE_SETTINGS`. Every discovered signature must:

1. return exactly zero from `WinVerifyTrust`;
2. match the complete reviewed subject DN;
3. contain both the code-signing EKU and the subscriber's durable Artifact
   Signing identity EKU;
4. carry an RFC 3161 timestamp attribute with a Windows-verified signing time.

One rejected, mismatched, non-code-signing, legacy-timestamped, or untimestamped
signature fails the artifact closed. Signer evidence contains only the
signature index, policy booleans, timestamp kind/time, and a SHA-256 digest of
the subject identity. It does not copy artifact paths, certificate dumps,
chain URLs, tokens, or unexpected subject text into diagnostics.

The trust result remains intentionally separate from these other release axes:

| Axis | What it establishes | What it does not establish |
|---|---|---|
| Authenticode policy | Windows accepted the PE signature/chain for code signing | repository release authorization or software safety |
| Publisher policy | the full DN and durable Artifact Signing identity match the reviewed publisher | that the current leaf is permanently pinned |
| RFC 3161 timestamp | Windows can evaluate signing time beyond the short-lived leaf's validity | current revocation freshness |
| Reviewed SHA-256 | exact artifact identity selected by reviewed release metadata | publisher identity by itself |
| GitHub attestation | producer workflow provenance for the attested bytes | runtime authorization or vulnerability absence |
| Runtime state | what is installed/running now | any of the producer claims above |

### 2026-08-03 platform audit receipt

The published `v0.1.0-rc.3` setup was inspected on Windows with both the runtime
verifier and Windows SDK SignTool. The observed leaf had the exact subject and
both EKUs above, was valid from 2026-08-01 through 2026-08-04, and had one RFC
3161 timestamp at `2026-08-03T02:43:49Z`. `signtool verify /pa /all /v`
reported one successfully verified signature and no warnings or errors. This
receipt is manual platform evidence for that artifact only; it is not a claim
about later artifacts, current revocation data, dependencies, or software
safety.

Primary references:

- [WinVerifyTrust return-value contract](https://learn.microsoft.com/en-us/windows/win32/api/wintrust/nf-wintrust-winverifytrust)
- [WINTRUST_DATA revocation, cache-only, and state lifecycle](https://learn.microsoft.com/en-us/windows/win32/api/wintrust/ns-wintrust-wintrust_data)
- [WINTRUST_SIGNATURE_SETTINGS multi-signature enumeration](https://learn.microsoft.com/en-us/windows/win32/api/wintrust/ns-wintrust-wintrust_signature_settings)
- [Artifact Signing certificate rotation, durable identity EKU, and timestamp semantics](https://learn.microsoft.com/en-us/azure/artifact-signing/concept-certificate-management)
- [Microsoft Authenticode RFC 3161 guidance](https://learn.microsoft.com/en-us/windows/win32/seccrypto/time-stamping-authenticode-signatures)

The same protected job grants `attestations: write` only alongside its existing
environment-scoped OIDC authority. It generates GitHub/Sigstore provenance for
the final MSIX, App Installer descriptor, archive, manifest, SBOM, launcher,
and updater bytes using the
official attestation Action pinned to a reviewed commit. The draft-staging job
has no OIDC/signing authority and refuses to stage transferred bytes that do
not verify against the release bundle, repository, workflow, tag, and commit.
After a maintainer publishes the immutable GitHub release, a separately
protected keyless GCP job first validates the immutable release classification,
then repeats attestation verification before obtaining GCP credentials and
advancing the App Installer channel. The job verifies the public descriptor
after replacing the channel pointer; a failed final check requires recovery and
does not imply the prior pointer is still active.

## Release manifest boundary

Authenticode proves the publisher and integrity of each Windows PE file. It
does not authenticate the JSON release manifest that selects versions, URLs,
channels, hashes, or withdrawal state. Native verification of the existing
GitHub/Sigstore producer evidence, with replay and withdrawal policy, remains a
separate release-control deliverable.

Until that consumer is implemented and qualified, a manifest checksum must not
be described as a manifest signature. Legacy schema v1 declares
`manifestAuthenticity.scheme: none`. Bridge schema v2 declares the exact
GitHub/Sigstore provenance scheme but remains rejected by the unauthenticated
selection path until issue #96 verifies the manifest first. Both schemas record
independent Authenticode expectations for each PE artifact or signed archive
member. The complete
producer and consumer contract is in
`docs/windows-launcher/RELEASE_MANIFEST.md`; the proposed consumer design is in
`docs/windows-launcher/RELEASE_SELECTION_AUTHENTICATION.md`; producer
attestation evidence is documented in
`docs/windows-launcher/ARTIFACT_ATTESTATIONS.md`. Native authenticated
release-selection consumption remains issue #96 under the #71 parent.

The workflow pins every external action to a reviewed commit and passes tag,
commit, and repository contexts into PowerShell through environment variables.
The workflow contains no PFX or client secret. A successful protected tag run,
package inspection, and manual clean-machine install/update/rollback/uninstall
receipt are still required evidence; this document does not claim that those
external gates have occurred.

The complete repository/external-control inventory and the evidence required
for a production tag are in
[`RELEASE_SECURITY_OPERATIONS.md`](RELEASE_SECURITY_OPERATIONS.md).
