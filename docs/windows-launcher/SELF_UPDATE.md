# Windows Launcher Self-Update

Status: WL-009 verified replace-on-exit implementation and packaging are complete; signed packaged upgrade/rollback smoke remains a final release gate.

Launcher updates use the canonical stable release manifest and immutable GitHub
asset URL. The archive response must match manifest status, declared/actual
size, and SHA-256. Extraction rejects traversal, duplicate, link, excessive
entry-count, and expanded-size payloads. Both the launcher and the updater
helper must pass Authenticode for Joseph Gustavson, and the launcher's embedded
source revision must exactly match `source.targetCommit`.

The running executable is never overwritten. The launcher stages the verified
archive under per-user state, copies the signed helper outside both old and new
program directories, writes a file-hashed plan, and exits. The helper waits for
that exact process, re-verifies every staged file, moves the old per-user
program directory to transaction backup, and moves the stage into place. The
new launcher acknowledges only after WPF activation. Missing acknowledgement
or early exit removes the failed payload, verifies/restores the prior payload,
and restarts it when available. No elevation is requested.

Stable is explicit and offline use is unaffected: update discovery is
user-initiated from Diagnostics and has no bearing on local game launch.
