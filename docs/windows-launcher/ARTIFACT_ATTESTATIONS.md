# STFC Mod Bridge release attestations

## Purpose

Every protected STFC Mod Bridge tag build publishes a GitHub/Sigstore build-provenance
attestation for the exact final release bytes. The attestation lets an independent
verifier bind an artifact digest to this repository, the protected release workflow,
the tag ref, and the tagged source commit.

Attestation is one evidence layer. It does not establish that source code or
dependencies are free from vulnerabilities or malicious behavior. Authenticode,
release authorization, local SHA-256 identity, update freshness, and revocation
freshness remain separate claims.

## Attested subjects

The protected `windows-release` job attests these files only after Authenticode
signing, final packaging, package inspection, and release-manifest generation:

- `STFCModBridge.exe`;
- `STFCModBridge.Updater.exe`;
- `STFCModBridge.Setup.exe`;
- `stfc-mod-bridge-win-x64.zip`;
- `stfc-mod-bridge-release-manifest.json`.

The workflow copies the signed bundle to
`stfc-mod-bridge-release-attestation.json`. The setup remains the only
user-facing install artifact. The ZIP, manifest, and attestation bundle are
machine-consumed trust/update evidence.

The publication job downloads the final subjects and refuses to create the
GitHub release unless every subject verifies against:

- repository `Guffawaffle/stfc-mod-bridge`;
- signer workflow `.github/workflows/release.yml`;
- the exact release tag ref;
- the exact tagged source commit;
- a GitHub-hosted runner identity.

## Independent online verification

Download the release setup, machine-consumed attestation bundle, and any other
subject to inspect. Substitute the release tag and its full 40-character commit:

```powershell
$releaseTag = "v0.1.0"
$sourceCommit = "0000000000000000000000000000000000000000"
$repository = "Guffawaffle/stfc-mod-bridge"
$workflow = "$repository/.github/workflows/release.yml"
$bundle = ".\stfc-mod-bridge-release-attestation.json"

gh attestation verify .\STFCModBridge.Setup.exe `
  --repo $repository `
  --signer-workflow $workflow `
  --source-ref "refs/tags/$releaseTag" `
  --source-digest $sourceCommit `
  --deny-self-hosted-runners `
  --bundle $bundle
```

The ZIP and manifest use the same command with their respective file paths.
The inner launcher and updater can be extracted from the ZIP and verified
against the same bundle.

Changing one byte, selecting another repository/workflow/commit/ref, or using
evidence from a self-hosted runner must fail verification.

## Offline verification

Before entering the offline environment, obtain a current Sigstore trusted-root
snapshot:

```powershell
gh attestation trusted-root > trusted_root.jsonl
```

Move the subject, the published attestation bundle, the trusted-root snapshot,
and a compatible GitHub CLI into the offline environment. Run the online
command above with this additional argument:

```text
--custom-trusted-root .\trusted_root.jsonl
```

Offline verification proves the captured evidence against the captured trust
root. It cannot discover a later revocation or trust-root change. The evidence
receipt must therefore state when the root snapshot was obtained and that
online freshness/revocation was not checked.

## Consumer boundary

Publishing an attestation does not by itself authorize a Bridge self-update.
Schema v1 continues to declare `manifestAuthenticity.scheme: none`, and the
running Bridge continues to enforce the existing repository, path, size,
SHA-256, Authenticode, version, provenance-consistency, and transactional
replacement checks.

[`RELEASE_SELECTION_AUTHENTICATION.md`](RELEASE_SELECTION_AUTHENTICATION.md)
records issue #71's proposed consumer direction, including trust-root rotation,
expiry, replay, withdrawal, and offline policy. Until that verifier and schema
land together, attestations are independently verifiable producer evidence,
not a silently activated updater authority.
