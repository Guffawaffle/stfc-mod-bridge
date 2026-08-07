# Release-selection authentication

Status: implementation complete through issue #97. Production authorization
remains deliberately fail-closed until issue #30 qualifies the protected-tag
evidence, package, offline/root-recovery, and failure-survival gates.

Issue #94 implements the non-authorizing verifier groundwork: a Go 1.26.5
helper locked to `sigstore-go` v1.3.0 and a 71-module
compiled checksum inventory, the embedded public-good root at Bridge trust
epoch 1, bounded local request and receipt contracts, a strict .NET process and
receipt boundary, and a captured real-release rejection fixture. Issue #95 adds
the deterministic schema-v2 producer, strict authenticated parser, freshness
and withdrawal policy, and atomic per-channel monotonic state. Issue #96 adds
bounded authenticated standalone discovery, derived evidence URLs, independent
digest binding, monotonic acceptance, and a truthful structured receipt. It
also removes the legacy unauthenticated standalone client from application
composition. Issue #97 packages the signed helper, embeds its final SHA-256 in
the launcher, binds the external updater to plan schema v2, and reruns the
already-installed helper immediately before replacement. The standalone update
command remains disabled pending #30; it never falls back to schema v1.

## Decision

Authenticate the exact release-manifest bytes with the GitHub/Sigstore build
provenance that the tag-release workflow is configured to produce. Verification
uses a small Mod Bridge-owned helper built from the official `sigstore-go`
library at a reviewed, locked version. The helper is an internal component of
the signed Mod Bridge package; players do not install or invoke GitHub CLI,
Cosign, Go, or any other external tool.

The .NET consumer treats the helper as a narrow cryptographic boundary. It
passes bounded local files and a closed pre-parse policy document, receives a
bounded JSON receipt, independently checks the manifest digest, then parses the
authenticated manifest and applies the remaining release policy. A successful
Sigstore signature without every applicable Mod Bridge policy match is
rejected.

Primary references:

- GitHub artifact-attestation overview and subject-digest contract:
  <https://docs.github.com/actions/security-for-github-actions/using-artifact-attestations/using-artifact-attestations-to-establish-provenance-for-builds>
- GitHub offline verification and captured trusted-root contract:
  <https://docs.github.com/actions/security-for-github-actions/using-artifact-attestations/verifying-attestations-offline>
- Sigstore bundle format:
  <https://docs.sigstore.dev/about/bundle/>
- `sigstore-go`, the official production-stable integration library:
  <https://github.com/sigstore/sigstore-go>

## Alternatives considered

### GitHub/Sigstore evidence with a bundled official verifier — selected

Benefits:

- reuses the exact final-byte provenance defined by #69's producer contract;
- binds ephemeral signing identity to repository, workflow, ref, commit, event,
  and GitHub-hosted runner claims;
- includes transparency-log and signed-time evidence;
- supports offline verification against a captured trusted root;
- requires no long-lived release-signing secret and no single human cosigner;
- preserves the project's one user-facing installer contract.

Costs:

- adds a reviewed Go toolchain and locked module graph to release engineering;
- adds one internal executable to the signed archive and installed program;
- requires explicit trust-root lifecycle and helper-integrity handling;
- GitHub, Fulcio, Rekor, and the selected workflow remain trust assumptions.

### Detached manifest signature with an embedded public key — rejected for v1

This can be technically sound using a standard signature format, but it creates
a second signing authority beside Authenticode and GitHub attestations. The
project would need to provision, protect, rotate, recover, and socially govern
another long-lived key. Storing it in repository secrets would let a workflow
compromise use it; requiring one maintainer to sign would centralize release
sovereignty. A managed/HSM key reduces extraction risk but not governance or
workflow-authorization risk. The additional authority is not justified while
the tag-release workflow is already configured to emit suitable keyless
provenance.

### Native .NET Sigstore package — not selected

No official Sigstore .NET client is currently listed by the Sigstore project.
The available managed implementation is young and would add a comparatively
large, security-critical third-party trust boundary. Pinning its package would
prevent an unreviewed upgrade, but would not create the maintenance and review
history needed for this release. Revisit if Sigstore adopts an official .NET
client with conformance and security-process coverage.

### Shell out to GitHub CLI or Cosign — rejected

Those tools are appropriate for independent verification and CI, but requiring
a player-installed executable creates a mutable external runtime dependency.
Bundling an entire general-purpose CLI would be materially larger than a closed
helper and expose unnecessary argument and feature surface.

## Threat model

The attacker may control release-list and asset HTTP responses, mutable GitHub
release metadata, redirects, DNS below TLS, a local cache, or an old valid
release bundle. The attacker may substitute a valid attestation from another
repository, workflow, ref, commit, runner class, subject, channel, or artifact.
They may interrupt the update between discovery and commit.

The mechanism must reject:

- changed manifest or artifact bytes;
- an otherwise valid bundle for another repository or repository numeric ID;
- another workflow path, ref, event, or self-hosted runner;
- a statement whose subject set omits or ambiguously duplicates the manifest;
- a statement whose source commit, tag, channel, or schema disagrees with the
  manifest or selected GitHub release;
- expired policy evidence, a version below the installed/accepted floor, and a
  digest present in authenticated withdrawal policy;
- a trust root older than the locally accepted root version;
- malformed, oversized, deeply nested, multiply encoded, or trailing input;
- evidence that changes between review, download, staging, and commit.

Protected-tag enforcement, protected-main ancestry, and protected-environment
approval are producer controls specified by #70 and qualified through #30. The
attestation proves the ref, source digest, and workflow identity; it does not
prove that administrators configured those GitHub controls correctly.

Out of scope: authentication does not prove that reviewed source is safe,
bug-free, or faithfully understood. A compromised protected repository and
workflow can produce authenticated malicious bytes. Authenticode and Sigstore
are independent evidence layers, not code review.

## Closed policy

The pre-parse verifier policy is compiled into Mod Bridge and serialized to the
helper as a closed schema. It contains exact values, never regular expressions
supplied by downloaded content:

| Field | Required value |
|---|---|
| repository | `Guffawaffle/stfc-mod-bridge` |
| repository ID | `1320037274` (`R_kgDOTq4rmg`) |
| owner ID | `105761663` |
| workflow | `.github/workflows/release.yml` |
| OIDC issuer | `https://token.actions.githubusercontent.com` |
| source ref | exact candidate `refs/tags/<tag>` |
| event | `push` |
| runner environment | `github-hosted` |
| predicate | SLSA provenance v1 / GitHub Actions workflow build type |
| subject | exactly one matching manifest name and SHA-256 |

GitHub's protected environment name and numeric workflow ID are not claims in
the current attestation contract. Workflow ID `325816686` and environment
`windows-release` remain documented external-control evidence, never
cryptographic receipt fields.

The receipt repeats the resolved values plus the attested source commit,
bundle digest, manifest digest, Rekor integrated time, Bridge trust epoch,
trusted-root document digest, Fulcio identity, Rekor log ID and threshold,
verification mode, and every check performed. The .NET side compares pre-parse
fields to its original request using ordinal equality and fixed-time digest
comparison. It then parses the authenticated manifest and requires
`source.targetCommit` to equal the receipt's attested commit. Unknown receipt
properties or enum values fail closed.

Repository/workflow/ref/runner checks belong to Sigstore policy.
Schema/channel/version/size/artifact-role checks belong to authenticated
manifest policy. Authenticode, executable identity, and extraction allowlists
belong to execution policy. No layer treats another layer's success as its own.

## Evidence transport and order

An explicit update check may perform network access. Passive Home, Settings,
Data Sync, and Diagnostics refresh never do.

For each bounded candidate release:

1. Read only the tag, draft/prerelease state, and exact expected asset names
   from the GitHub release response. Treat all of it as untrusted discovery
   input.
2. Require the discovered tag to pass the compiled canonical tag grammar, then
   derive canonical HTTPS asset URLs from the compiled repository, exact tag,
   and fixed manifest and release-selection-bundle basenames. Never follow a
   supplied arbitrary URL.
3. Download the bounded raw manifest and manifest-only attestation bundle
   without parsing or acting on manifest fields. #69's existing multi-subject
   attestation step remains broad release evidence; #71 adds a separate
   manifest-only bundle so release-selection cardinality is unambiguous.
4. Verify the raw manifest bytes and pre-parse closed policy through the helper.
5. Independently hash those same bytes, validate the closed manifest schema,
   and require its source commit to equal the attested receipt commit.
6. Apply authenticated-manifest policy and select only an active, advancing,
   non-withdrawn candidate.
7. Download and verify the selected artifact using the authenticated size and
   digest, then apply existing Authenticode, embedded-provenance, extraction,
   path, and transactional checks.
8. Write update-plan schema v2 with evidence paths and expected digests for the
   manifest, bundle, receipt, trusted root, archive, helper, and extracted
   files. Before the parent exits, the updater parses and validates the plan and
   retains those expectations in memory. Immediately before commit it rehashes
   every entry against the retained values and reruns the already-installed
   verifier over the manifest, bundle, and approved embedded root. The mutable
   plan and receipt never authenticate themselves. Any difference abandons
   staging and recovery preserves the prior installation.

Failure never alters or disables a healthy installed artifact. It reports that
no authenticated update could be established.

## Freshness, replay, freeze, and withdrawal

Manifest schema v2 adds authenticated release sequence, issued time, expiry,
and withdrawal entries. Times use whole-second UTC RFC 3339 and are checked against
the local clock plus verified Rekor integrated time. `issuedAt` must be no later
than integrated time plus allowed clock skew, and the interval from `issuedAt`
to integrated time must not exceed a compiled maximum signing delay. Expiry is
bounded from Rekor integrated time, not from an unauthenticated transport
timestamp.

The frozen v1 cadence is:

- ten minutes of allowed clock skew;
- one hour maximum signing delay;
- 45 days maximum stable-manifest validity;
- 14 days maximum preview-manifest validity;
- 24 hours of tolerated local-clock rollback before new acceptance fails
  closed.

The positive GitHub workflow run number is the release sequence. It is stable
across a rerun of the same workflow run and monotonically advances across later
tag runs. Sequence alone never authorizes a downgrade: semantic version, tag,
source commit, manifest/bundle digests, and the channel floor are checked
independently.

Mod Bridge persists, per channel:

- highest accepted release sequence and semantic version;
- accepted manifest and bundle digests;
- source commit and tag;
- Bridge trust epoch and exact root-document digest;
- first/last observed UTC time and verification mode.

A candidate below either the installed version or persisted floor is rejected.
An explicit update fails safely when the local clock is materially earlier than
the persisted last-observed time. Freshness uses an effective time no earlier
than that persisted floor; a successful verification advances the observation
to the greatest of the prior observation, local UTC, and verified Rekor time.
Expiry detects a frozen old response but implies that release maintainers must
publish fresh authenticated metadata before the maximum validity window closes.
Until a maintained refresh workflow exists, expiry is fail-closed for new
updates while the installed version remains usable.

Withdrawal is additive and authenticated. A higher valid manifest may deny a
prior release sequence, manifest digest, or artifact digest; a replacement is
recommended but not required during emergency containment. Historical
manifests remain immutable. This release withdrawal is distinct from Sigstore
trust-root/log-key rotation and from deletion of GitHub's copy of an
attestation. A captured valid public-transparency bundle does not become
cryptographically invalid merely because its GitHub API record is deleted.
Offline clients cannot learn later withdrawals; the receipt must say so plainly.

## Trusted-root lifecycle

The v1 package embeds a reviewed Sigstore trusted-root snapshot and its digest.
Mod Bridge assigns that public-good root material a monotonic Bridge trust epoch
and records the exact document digest, Fulcio identity, accepted Rekor log IDs,
and required log threshold. GitHub-private trust material is not imported for
this public repository. The helper receives only that local root and has no
network capability. Downloaded manifests, bundles, GitHub responses, and raw
`gh attestation trusted-root` output cannot nominate or advance trust roots. A
future online TUF rotation design must verify the complete TUF metadata chain
and return an authenticated monotonic Bridge trust epoch; merely downloading a
newer `TrustedRoot` document is not sufficient.

Root rotation must overlap: a qualified Bridge release ships the new root while
the old root can still authenticate that Bridge release. A client that misses
the overlap uses a separately downloaded, independently verified installer;
it never disables verification or accepts an untrusted replacement. Emergency
root or workflow compromise blocks automatic update authorization. Production
authorization for #71 depends on the completed and rehearsed #74 runbook, which
must cover containment and release withdrawal, denylisting compromised roots
or workflows, clean-root bootstrap through an independently verified installer,
user communication, evidence preservation, and recovery validation.

## Offline behavior

Offline verification is an explicit support/import operation over user-selected
local manifest and bundle evidence; normal update discovery is not implied to
work offline. Verification uses the installed embedded root. If support tooling
also accepts a root file for inspection, it must exactly match an approved
Bridge trust epoch and digest before it can authorize anything; arbitrary root
enrollment is prohibited. Offline evidence must pass the same closed policy and
local monotonic floors. The receipt records capture/verification time and states
that online freshness, new withdrawal, and new trust-root/log-key evidence were
not checked. Offline mode does not silently fall back to a reviewed hash for Mod
Bridge itself.

NetniV mod artifacts remain on their separately reviewed exact-hash provider
contract until upstream publishes compatible authenticated evidence.

## Implementation gates

- Pin Go and `sigstore-go` by exact version/checksum; commit `go.sum` and include
  the helper in dependency, SBOM, license, vulnerability, and Defender gates.
- Give the helper no network capability and no support for a downloaded trust
  root in the v1 automatic-update path.
- Bound every input/output size, execution time, path, JSON depth, and process
  exit; use anonymous temporary directories with restrictive ACLs.
- Hash and Authenticode-verify the already-installed helper before invocation.
  Bootstrap is an ordered cryptographic pairing: build and sign the helper,
  embed that final helper digest into the launcher, then build and sign the
  launcher and outer signed MSIX. A clean install therefore starts with an
  Authenticode-protected launcher carrying the exact expected helper digest; it
  does not need a prior receipt. During self-update, the running launcher passes
  its embedded current-helper digest to the updater, while the authenticated
  candidate manifest establishes the next helper/launcher pair and the updater
  verifies that the candidate launcher embeds the same helper digest. The
  candidate helper cannot authenticate its own update. The helper is signed by
  the same release job as the launcher and updater.
- Extend packaging, extraction, Defender, SBOM/notices, Authenticode signing,
  release-manifest `signedFiles`, and package-boundary tests for the one exact
  helper identity.
- Add official Sigstore conformance fixtures plus Mod Bridge policy fixtures
  for valid, substituted, wrong repository/workflow/ref/commit/runner,
  expired, replayed, downgraded, withdrawn, offline, compromised/revoked root,
  and root-rotation states.
- Fuzz the helper request/receipt parsers and the .NET receipt parser.
- Keep the current schema-v1 `scheme: none` path ineligible for authenticated
  Bridge self-update. There is no permissive migration because Bridge has not
  shipped publicly.
- Add update-plan schema v2 and crash/recovery fixtures proving every staged
  evidence digest is rechecked and the closed verification policy is rerun by
  the external updater before directory swap. Mutate both evidence and the plan
  in adversarial fixtures.

## Review gate

Before implementation is allowed to authorize a download, reviewers must
approve:

1. the exact repository/owner numeric IDs and workflow policy;
2. the selected Go and `sigstore-go` versions and dependency audit;
3. the manifest-v2 expiry/withdrawal cadence;
4. root bootstrap and rotation fixtures;
5. a real protected-tag bundle verified by both the helper and GitHub CLI;
6. packaged updater failure tests proving the existing installation survives.
