# Release Security Operations

This runbook separates controls enforced by this repository from GitHub and
Azure controls that a repository checkout cannot inspect or change. Record the
external-control evidence on the release epic before approving a production
tag. Do not treat this document as evidence that a setting is enabled.

Release users and independent reviewers follow
[`INDEPENDENT_VERIFICATION.md`](INDEPENDENT_VERIFICATION.md). Suspected
authority compromise follows
[`COMPROMISE_RESPONSE.md`](COMPROMISE_RESPONSE.md); containment never waits for
a replacement release.

## Repository-enforced release boundary

The tag workflow accepts only `vX.Y.Z` and `vX.Y.Z-rc.N`, resolves the tag to a
commit, and fails unless that commit is reachable from `origin/main`. It uses
the fixed `windows-2022` runner generation and records the hosted image details
in the Actions log; the weekly image revision is still a mutable external
input. All Actions are pinned to full commit IDs and checkout neither persists
credentials nor initializes submodules.

GitHub also enforces full-SHA Action references at the repository boundary.
The Actions permissions API was read back on 2026-08-13 after enabling the
control and reported:

```json
{
  "enabled": true,
  "allowed_actions": "all",
  "sha_pinning_required": true
}
```

The `allowed_actions` value is intentionally unchanged. Action allowlisting is
a separate control and must not be enabled until every required Azure, Google,
and GitHub Action remains permitted. Recheck this mutable control-plane state
before a production tag; the historical readback above is not proof of its
future value.

The .NET SDK is pinned without roll-forward. Every project has a NuGet lock
file with package content hashes, and CI and release restore in locked mode.
The Microsoft SBOM tool is repository-pinned. Before any signing authority is
available, the build job:

1. restores and tests the locked dependency graph;
2. builds the unsigned payload with analyzers and warnings-as-errors;
3. queries NuGet's vulnerability database for direct and transitive packages;
4. fails if a Git submodule is introduced without a new reviewed policy;
5. scans the rebuilt inner payload before launcher/updater signing with the
   runner's enabled Microsoft Defender engine and reports its signature version;
   and
6. regenerates SPDX 2.2 payload and verifier SBOMs from the exact final signed
   PE bytes.

Both final-byte SBOMs are included in the final attestation, reverified before
draft staging, and staged with the machine-consumed release inputs. Each checks
that its SHA-256 file records match the final PE bytes. Within the tag workflow, only
the protected signing job receives `id-token: write`; the draft-staging job
alone receives `contents: write`. A separate post-publication GCS job receives
OIDC plus read-only GitHub permissions and no signing or GitHub publication
authority. The signing/attestation order remains defined in
[Windows Release Signing](CODE_SIGNING.md).

## Draft qualification and one-way publication

The tag workflow always creates a GitHub **draft** release. A successful
workflow proves that the tagged source passed the producer gates and that the
staged subjects were signed, attested, transferred, and reverified. It does not
approve those subjects for testers and does not make the draft discoverable by
the update client.

Before publication, the maintainer must:

1. download the exact draft assets and retain the workflow URL, environment
approval, runner image, Defender versions, manifest, SBOMs, hashes, and
   attestation verification output;
2. exercise and record the release epic's applicable canary matrix against those
   staged bytes;
3. leave a candidate with failed producer/security evidence or unacceptable
   residual risk as a draft and record the rejection; never publish it merely
   to obtain a public download URL; and
4. choose and record one truthful, immutable publication classification:
   - after the required canary matrix is complete, use the exact phrase
     **Closed-alpha approved**; or
   - after an explicit maintainer decision to permit broader voluntary testing
     with incomplete canary coverage, use the exact phrase **Public canary —
     qualification is still in progress** and enumerate every open check.

For example, after preparing and reviewing a final notes file:

```powershell
gh release edit v0.1.0-rc.4 `
  --repo Guffawaffle/stfc-mod-bridge `
  --notes-file ./release-notes.md `
  --draft=false `
  --prerelease
```

Confirm the release is still a draft immediately before this command. Release
immutability takes effect when it is published, so the notes and asset set must
already be final. The release notes must call the build a prerelease, identify
`STFCModBridge.appinstaller` as the user-facing installation entry point,
describe the MSIX, ZIP, manifest, SBOMs, and attestation bundle as
machine-consumed inputs or a standalone fallback, link the
qualification evidence, state the chosen classification and open checks, and
retain the provenance-versus-safety limitation.

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
| Google Cloud Storage update feed | GitHub OIDC/WIF only; immutable-package create precondition; public byte/hash/MIME/range verification before channel advance | GCP WIF provider, bucket IAM, public endpoint, DNS/TLS, and service availability |

Adding a submodule, a download script, a package source, an Action, or another
network-acquired tool requires updating this inventory and the invariant tests
before it can enter the release path.

## Required GitHub evidence

Before a production tag, capture the current settings or API output for:

- `main` protection that blocks deletion and force-push, requires pull requests,
  requires signed commits, the named CI checks, and resolved conversations, and
  records whether independent review is enforced for CODEOWNERS-owned release
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

## Required Google Cloud evidence

The `windows-release` environment supplies the GCP project, bucket, workload
identity provider, service account, and public HTTPS base URI as protected
variables. Export and review the WIF provider attribute condition and bucket
IAM. The GitHub principal must be limited to this repository's protected
`windows-release` environment, and the service account must have object access
only on the update bucket. No service-account JSON key belongs in GitHub.

The bucket serves public objects over HTTPS with byte-range support. Versioned
`packages/v<version>/STFCModBridge.msix` objects are write-once by workflow
precondition; only `stable/STFCModBridge.appinstaller` and
`preview/STFCModBridge.appinstaller` move. Enable object versioning or retention
as a recovery control, and retain evidence of MIME types, cache controls, public
hash equality, and channel non-downgrade checks from the publication run.

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
