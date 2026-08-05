# Windows Release Signing

## Decision

Windows release tags use Azure Artifact Signing with the existing
`stfcsidecarsign` account and `stfc-sidecar-public` Public Trust certificate
profile. The expected publisher is `Joseph Gustavson`.

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
- Publication job permission: `contents: write` without `id-token`

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
- `STFCModBridge.Updater.exe`
- `STFCModBridge.Setup.exe`

Future executable release components must be added to an explicit signing
allowlist before release. Package inspection identifies PE files by their
actual `MZ`/`PE\0\0` headers rather than file extension, so a DLL or renamed PE
cannot evade that allowlist.

The release order is:

```text
locked restore/test -> unsigned build -> vulnerability/malware gates -> SPDX SBOM
      -> approve protected environment -> OIDC login -> sign inner executables
      -> verify -> package -> embed package -> sign setup -> verify -> hash
      -> attest final subjects -> transfer -> reverify attestations -> publish
```

Packaging and checksums must occur after signing because Authenticode modifies
the PE file. The setup must be built only after the signed launcher ZIP exists,
because that exact ZIP is embedded in its PE; the setup is then signed as the
outermost artifact. The workflow verifies every executable with
`Get-AuthenticodeSignature`, including valid status and the expected
certificate subject, before manifest generation or release publication.

The same protected job grants `attestations: write` only alongside its existing
environment-scoped OIDC authority. It generates GitHub/Sigstore provenance for
the final setup, archive, manifest, launcher, and updater bytes using the
official attestation Action pinned to a reviewed commit. The publication job
has no OIDC/signing authority and refuses to publish transferred bytes that do
not verify against the release bundle, repository, workflow, tag, and commit.

## Release manifest boundary

Authenticode proves the publisher and integrity of each Windows PE file. It
does not authenticate the JSON release manifest that selects versions, URLs,
channels, hashes, or withdrawal state. Native verification of the existing
GitHub/Sigstore producer evidence, with replay and withdrawal policy, remains a
separate release-control deliverable.

Until that consumer is implemented and qualified, a manifest checksum must not
be described as a manifest signature. Schema v1 therefore declares
`manifestAuthenticity.scheme: none` while recording independent Authenticode
expectations for each PE artifact or signed archive member. The complete
producer and consumer contract is in
`docs/windows-launcher/RELEASE_MANIFEST.md`; the proposed consumer design is in
`docs/windows-launcher/RELEASE_SELECTION_AUTHENTICATION.md`; producer
attestation evidence is documented in
`docs/windows-launcher/ARTIFACT_ATTESTATIONS.md`. Native authenticated
release-selection consumption remains issue #71.

The workflow pins every external action to a reviewed commit and passes tag,
commit, and repository contexts into PowerShell through environment variables.
The workflow contains no PFX or client secret. A successful protected tag run,
package inspection, and manual clean-machine install/update/rollback/uninstall
receipt are still required evidence; this document does not claim that those
external gates have occurred.

The complete repository/external-control inventory and the evidence required
for a production tag are in
[`RELEASE_SECURITY_OPERATIONS.md`](RELEASE_SECURITY_OPERATIONS.md).
