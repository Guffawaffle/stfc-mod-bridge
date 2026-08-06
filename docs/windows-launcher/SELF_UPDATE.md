# Windows Launcher Self-Update

Status: the replace-on-exit implementation remains the signed standalone ZIP
fallback. MSIX installs use Windows App Installer and never run this replacement
path.

Packaged processes detect their Windows package identity and direct users to
Windows Installed Apps. The signed App Installer descriptor checks its channel
on launch, and Windows owns download, version ordering, replacement, rollback,
and package uninstall. The remainder of this document applies only to a copy
launched from the standalone ZIP.

Launcher updates use the standalone repository's canonical stable release
manifest and immutable GitHub asset URL. Provider packs cannot alter this
authority. The archive response must match manifest status, declared/actual
size, and SHA-256. Extraction rejects traversal, duplicate, link, excessive
entry-count, expanded-size payloads, and every PE-header-bearing archive member
outside the exact launcher/updater allowlist. Both the launcher and the updater
helper must pass Authenticode for Joseph Gustavson, and the launcher's embedded
source revision must exactly match `source.targetCommit`. Discovery evaluates
all matching release manifests and selects the highest active eligible channel
version; API order, a lower release, or a withdrawn release cannot select or
block another eligible release.

The running executable is never overwritten. The launcher stages the verified
archive under per-user state, copies the signed helper outside both old and new
program directories, writes a file-hashed plan, and exits. The helper acquires
the launcher's cross-process operation lease before waiting for that exact
process and holds it through re-verification, replacement, startup
acknowledgement, and rollback. It then moves the old per-user
program directory to transaction backup, and moves the stage into place. The
new launcher acknowledges only after WPF activation. Missing acknowledgement
or early exit removes the failed payload, verifies/restores the prior payload,
and restarts it when available. No elevation is requested.

The update plan and backup live under the persistent state root, never inside
the replaced program directory. The helper validates exact state/program paths
and recorded hashes, and it restores the verified prior payload when launch
acknowledgement fails. Independent mod/configuration operation journals are not
moved or deleted by standalone self-update. App Installer and MSIX uninstall do
not consume or remove those external state records.

Stable is explicit and offline use is unaffected: update discovery is
user-initiated from Diagnostics and has no bearing on local game launch.

The discovery client requires a candidate version to advance the running
launcher, preventing ordinary replay/downgrade. Emergency containment freezes
publication and adds a reviewed entry to
`docs/release-withdrawals/release-withdrawals.jsonl` without waiting for a
replacement. Preserve immutable release evidence; a higher independently
verified replacement is the normal recovery path. Runtime enforcement of
authenticated withdrawal policy remains issue #71. See
[`COMPROMISE_RESPONSE.md`](COMPROMISE_RESPONSE.md).
Manifest v1 is not detached-signed, so repository-control compromise remains a
documented residual rather than being mislabeled as solved.
