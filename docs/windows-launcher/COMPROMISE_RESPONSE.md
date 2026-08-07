# STFC Mod Bridge compromise response

This runbook is for suspected compromise of a release artifact, Azure signing
authority, GitHub repository/workflow, or Sigstore trust material. It is a human
and operator procedure. It does not claim that the current Bridge client has an
authenticated online withdrawal feed; that enforcement remains issue #71.

## First response: preserve, freeze, classify

1. Stop new release approvals, signing-environment approvals, and App Installer
   channel advancement. Do not delete local or hosted evidence.
2. Record UTC detection time, reporter, affected tags/digests/channels,
   workflow runs, environment approvals, Azure profile/certificate identifiers,
   GCP object generations, and observed client behavior.
3. Copy release assets, manifests, both attestation bundles, SBOMs, workflow
   logs, transparency evidence, trusted-root snapshots, signatures, and hashes
   into access-controlled incident storage.
4. Classify the suspected authority before choosing containment:
   - one artifact/digest or release;
   - Azure signing profile, certificate, identity, or role assignment;
   - GitHub repository, tag, workflow, environment, token, or maintainer;
   - Bridge trust epoch/root, Fulcio identity, Rekor/log material, or hosted
     attestation availability.
5. Open a private GitHub security advisory or the current private incident
   channel. Do not put exploit details, credentials, or private evidence in a
   public issue.

Emergency containment does not wait for a higher replacement. A clean higher
version is the preferred recovery path, not a prerequisite to freezing the
channel, revoking access, or warning users.

## Containment by authority

### Artifact or release digest

- stop App Installer channel advancement and mark the affected release as not
  approved in the incident record and public advisory;
- retain immutable release/tag/evidence history where repository controls allow;
- record the exact affected digests and prevent the update selector from
  authorizing them when #71's authenticated withdrawal mechanism is available;
- do not imply that deleting a GitHub release or bundle revokes copies already
  captured in transparency logs or on user machines.

### Azure Artifact Signing

- remove or disable the affected signing profile/identity access using the
  current Azure portal or current official Azure Artifact Signing incident
  procedure;
- review Entra federated credentials, role assignments, profile audit logs,
  certificate status, timestamp evidence, and every signature produced during
  the exposure window;
- do not paste or improvise CLI commands until their current extension/API
  behavior has been verified against official Microsoft documentation;
- rotate or replace authority only after preserving identifiers and evidence
  needed to distinguish old and clean signatures.

### GitHub repository or workflow

- freeze merges, tag creation, environment approvals, and release publication;
- remove compromised credentials or principals and review rulesets,
  CODEOWNERS, Actions permissions, OIDC claims, environment history, audit log,
  tags, releases, workflow revisions, and unexpected attestations;
- assume repository-origin attestations made through affected workflow code or
  authority may be untrustworthy until the reviewed clean boundary is restored.

### Sigstore root, Fulcio identity, or Rekor/log evidence

- distinguish root/log-key compromise from deletion or unavailability of
  GitHub's hosted copy of an otherwise valid bundle;
- deny the affected trust epoch/root/log identity in #71 policy when available;
- preserve old bundles and root snapshots for forensics without continuing to
  authorize them;
- publish the new trust epoch with a documented overlap window. A client that
  missed the overlap must recover through a separately downloaded and
  independently verified installer rather than silently accepting a new root.

## Recovery and replacement

1. Establish a clean repository/workflow/signing boundary and document who
   reviewed it. A single-maintainer recovery explicitly records the lack of
   separation of duties.
2. Build a numerically higher signed release from reviewed protected `main`.
3. Verify it independently with
   [`INDEPENDENT_VERIFICATION.md`](INDEPENDENT_VERIFICATION.md), including
   negative wrong-authority and changed-byte checks.
4. Publish authenticated withdrawal/denylist information when #71 supports it.
   Until then, public advisories and channel freeze are communication controls,
   not automatic offline enforcement.
5. Advance App Installer channels only after #30 qualification and incident
   owner approval. Never use a mutable channel pointer as proof of authority.
6. Provide users exact affected versions/digests, what evidence remains valid,
   whether uninstall is needed, how to obtain the clean installer separately,
   and how to verify recovery.

Healthy local installations remain usable only if the incident assessment says
their exact bytes are unaffected. Do not automatically delete user data, the
installed community mod, configuration, or forensic evidence.

## Offline and delayed clients

Offline clients cannot learn later withdrawal, revocation, denylist, root, or
channel changes. A previously valid captured bundle remains cryptographically
valid against its captured trusted root even after an incident. State that
limitation plainly. Recovery requires reconnecting to an authenticated current
policy source or using a separately obtained installer verified out of band.

## Closure evidence

The incident owner records:

- scope, timeline, affected authorities and exact digests;
- access removals, profile/certificate disposition, and ruleset/environment
  repairs;
- preserved evidence locations and hashes;
- clean-root/bootstrap decision and overlap window;
- replacement release and independent verification receipts;
- user communications and recovery validation;
- unresolved trust assumptions and follow-up owners.

Rehearse this runbook before the first production authorization and at least
once per year, and again after a release-authority redesign. #30 must record one
compromised/revoked-root tabletop and one missed-overlap recovery through the
independent installer path before #74 closes.
