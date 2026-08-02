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
  `repo:Guffawaffle/stfc-mod:environment:windows-release`
- GitHub job permissions: `contents: read` and `id-token: write`

Pull requests and ordinary branch builds remain unsigned. Only a tag build can
enter the signing job, and the protected environment requires approval before
GitHub issues the environment-scoped OIDC token.

## Signed artifacts

The tag workflow signs and verifies:

- `version.dll`
- `STFCCommunityMod.Launcher.exe`
- `STFCCommunityMod.Launcher.Updater.exe`
- `STFCCommunityMod.Launcher.Setup.exe`

Future executable release components must be added to an explicit signing
allowlist before release.

The release order is:

```text
build -> approve protected environment -> OIDC login -> sign inner executables
      -> verify -> package -> embed package -> sign setup -> verify -> hash -> publish
```

Packaging and checksums must occur after signing because Authenticode modifies
the PE file. The setup must be built only after the signed launcher ZIP exists,
because that exact ZIP is embedded in its PE; the setup is then signed as the
outermost artifact. The workflow verifies the executables with
`Get-AuthenticodeSignature` (and the inner release files with `signtool verify
/pa /v`), including the expected certificate subject.

## Release manifest boundary

Authenticode proves the publisher and integrity of each Windows PE file. It
does not authenticate the JSON release manifest that selects versions, URLs,
channels, hashes, or withdrawal state. A detached manifest-signature design
with replay protection remains a separate release-control deliverable.

Until that design is accepted, a manifest checksum must not be described as a
manifest signature. Schema v1 therefore declares
`manifestAuthenticity.scheme: none` while recording independent Authenticode
expectations for each PE artifact or signed archive member. The complete
producer and consumer contract is in
`docs/windows-launcher/RELEASE_MANIFEST.md`.

## Rollback and credential retirement

The previous sidecar client-secret identity remains available only as a
temporary rollback path while the OIDC migration is validated. It is not used
by this repository.

After both repositories complete a successful protected OIDC signing run:

1. Remove the sidecar `AZURE_CLIENT_SECRET` GitHub environment secret.
2. Delete the corresponding Entra application password credential.
3. Replace the sidecar's signing-account-scoped RBAC assignment with a
   certificate-profile-scoped assignment.
4. Re-run signed artifact verification and record the workflow runs.

Do not retire the rollback credential before both signed runs succeed.
