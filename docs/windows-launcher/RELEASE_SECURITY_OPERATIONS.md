# Release Security Operations

This runbook separates controls enforced by this repository from GitHub and
Azure controls that a repository checkout cannot inspect or change. Record the
external-control evidence on the release epic before approving a production
tag. Do not treat this document as evidence that a setting is enabled.

## Repository-enforced release boundary

The tag workflow accepts only `vX.Y.Z` and `vX.Y.Z-rc.N`, resolves the tag to a
commit, and fails unless that commit is reachable from `origin/main`. It uses
the fixed `windows-2022` runner generation and records the hosted image details
in the Actions log; the weekly image revision is still a mutable external
input. All Actions are pinned to full commit IDs and checkout neither persists
credentials nor initializes submodules.

The .NET SDK is pinned without roll-forward. Every project has a NuGet lock
file with package content hashes, and CI and release restore in locked mode.
The Microsoft SBOM tool is repository-pinned. Before any signing authority is
available, the build job:

1. restores and tests the locked dependency graph;
2. builds the unsigned payload with analyzers and warnings-as-errors;
3. queries NuGet's vulnerability database for direct and transitive packages;
4. fails if a Git submodule is introduced without a new reviewed policy;
5. scans the unsigned payload with the runner's enabled Microsoft Defender
   engine and reports its signature version; and
6. generates an SPDX 2.2 SBOM for the unsigned payload.

The SBOM crosses the same artifact boundary as the unsigned payload, is
included in the final attestation, is reverified before draft staging, and is
staged with the machine-consumed release inputs. The protected signing job
alone receives `id-token: write`; the draft-staging job alone receives
`contents: write`. The signing/attestation order remains defined in
[Windows Release Signing](CODE_SIGNING.md).

## Draft qualification and one-way publication

The tag workflow always creates a GitHub **draft** release. A successful
workflow proves that the tagged source passed the producer gates and that the
staged subjects were signed, attested, transferred, and reverified. It does not
approve those subjects for testers and does not make the draft discoverable by
the update client.

Before publication, the maintainer must:

1. download the exact draft assets and retain the workflow URL, environment
   approval, runner image, Defender versions, manifest, SBOM, hashes, and
   attestation verification output;
2. complete the release epic's canary matrix against those staged bytes;
3. leave a failed candidate as a draft and record the rejection; never publish
   it merely to obtain a public download URL; and
4. replace the qualification warning with final release notes containing the
   exact phrase **Closed-alpha approved**, then publish the prerelease once.

For example, after preparing and reviewing a final notes file:

```powershell
gh release edit v0.1.0-rc.4 `
  --repo Guffawaffle/stfc-mod-bridge `
  --notes-file ./approved-release-notes.md `
  --draft=false `
  --prerelease
```

Confirm the release is still a draft immediately before this command. Release
immutability takes effect when it is published, so the notes and asset set must
already be final. The release notes must call the build a prerelease, identify
`STFCModBridge.Setup.exe` as the only user-facing download, describe the ZIP,
manifest, SBOM, and attestation bundle as machine-consumed inputs, link the
qualification evidence, and retain the provenance-versus-safety limitation.

## Reviewed network and tool inputs

| Input | Repository control | Remaining external control |
|---|---|---|
| GitHub Actions | Full commit pins with reviewed major-version comments | GitHub hosts the Action and runner service |
| Windows runner | `windows-2022`, no self-hosted signing | Exact image revision updates over time; retain `ImageOS` and `ImageVersion` from the run log |
| .NET SDK | Exact `global.json` version, roll-forward disabled | Microsoft-hosted SDK download during runner setup |
| NuGet packages | Locked versions and content hashes | NuGet.org availability and live vulnerability metadata |
| SBOM generator | Exact local-tool package version | NuGet.org supplies the content-addressed package |
| Microsoft Defender | Gate requires enabled engine and signatures; version logged | Microsoft supplies current signatures on the hosted image |
| Azure Artifact Signing | OIDC only; no PFX/client secret; action commit pinned | Azure endpoint, profile, role assignment, certificate, and timestamp service |
| GitHub attestations/releases | Exact subjects verified after job transfer | GitHub OIDC, transparency log, attestation, CLI, and release services |

Adding a submodule, a download script, a package source, an Action, or another
network-acquired tool requires updating this inventory and the invariant tests
before it can enter the release path.

## Required GitHub evidence

Before a production tag, capture the current settings or API output for:

- a `main` ruleset that blocks deletion and force-push, requires pull requests,
  requires the named CI checks, and requires review for CODEOWNERS-owned release
  files;
- a `v*` tag ruleset that restricts tag creation and blocks tag update/deletion;
- immutable releases enabled for the repository, including a negative check
  that an existing release asset cannot be replaced;
- Actions fork and approval policy showing that untrusted pull-request code
  cannot enter the `windows-release` environment;
- the `windows-release` environment's deployment branch/tag restriction,
  required reviewer, and self-review prevention where the GitHub plan supports
  it; and
- the people or teams allowed to merge workflow/CODEOWNERS changes, administer
  rulesets, approve the environment, and publish or delete releases.

`CODEOWNERS` identifies `Guffawaffle` for workflow, SDK, dependency-lock, SBOM
tool, and release-gate changes. That is review routing, not an independent
approval by itself. A one-maintainer configuration cannot provide separation
of duties; record that residual risk explicitly until a second trusted
maintainer is assigned.

## Required Azure evidence

Export and review the Microsoft Entra federated credential and Azure role
assignments. The credential must use GitHub's issuer and audience and the exact
immutable-ID subject documented in [Windows Release Signing](CODE_SIGNING.md).
The application must have only `Artifact Signing Certificate Profile Signer`
at the `stfc-sidecar-public` profile scope. Record every principal able to
change the federated credential, signing account, certificate profile, or role
assignment. Remove or document any broader subscription/resource-group role.

## Production-tag acceptance record

Attach these items to the release epic:

- protected-main, protected-tag, immutable-release, environment, fork-policy,
  and governance evidence;
- Entra federated-credential and Azure least-privilege exports;
- the successful tag workflow URL and protected-environment approval record;
- runner image and Defender signature versions from the build log;
- downloaded release SBOM and attestation bundle verification output; and
- clean-machine install, update, rollback, repair, and uninstall receipts; and
- the final reviewed release notes and immutable publication receipt.

Repository tests prove workflow shape and ordering. They do not prove that an
administrator has enabled the external controls above.
