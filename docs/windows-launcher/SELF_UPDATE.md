# Windows Launcher Self-Update

Status: WL-009 verified replace-on-exit implementation and packaging are complete; signed packaged upgrade/rollback smoke remains a final release gate.

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
the replaced program directory. If the helper is interrupted before it can
acknowledge or roll back, the next signed setup run validates the plan's exact
state/program paths and every recorded backup hash before removing any current
payload, restores the verified previous payload, and only then begins the new
setup transaction. Recovery preflights every abandoned plan and backup before
the first delete and refuses ambiguous multiple-backup state instead of
choosing an order. Setup must acquire the same operation lease before recovery,
so it rejects a concurrent updater before touching the current payload.
Independent mod/configuration operation journals are not
moved or deleted by launcher setup or self-update.

Stable is explicit and offline use is unaffected: update discovery is
user-initiated from Diagnostics and has no bearing on local game launch.

The discovery client requires a candidate version to advance the running
launcher, preventing ordinary replay/downgrade. Release withdrawal requires a
higher signed replacement, removal of the affected release and tag, and a
reviewed entry in `docs/release-withdrawals/release-withdrawals.jsonl`.
Manifest v1 is not detached-signed, so repository-control compromise remains a
documented residual rather than being mislabeled as solved.
