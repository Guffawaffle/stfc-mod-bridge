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
outside the exact launcher/release-verifier/updater allowlist. All three PEs
must pass Authenticode for Joseph Gustavson. The launcher's embedded source
revision must exactly match `source.targetCommit`, and its embedded verifier
SHA-256 must match the adjacent helper bytes. Discovery evaluates
all matching release manifests and selects the highest active eligible channel
version; API order, a lower release, or a withdrawn release cannot select or
block another eligible release.

Every self-update executable handoff—the ordinary updater runner, recovery
runner, newly installed launcher, and restored launcher—carries its exact size
and SHA-256 from authenticated preparation or the protected recovery journal.
Immediately before process creation, the caller rehashes and rechecks
Authenticode while a deny-write/deny-delete file handle remains open through
direct process creation. A verified executable pathname therefore cannot be
substituted across the verify/execute boundary.

The running executable is never overwritten. The launcher stages the verified
archive and exact manifest, bundle, receipt, and approved trust root under
per-user state. Plan schema v2 binds their paths, sizes, and SHA-256 values plus
the archive, current and candidate launcher/verifier pairs, candidate and runner
updaters, and complete old/new file inventories. The updater parses and retains
that closed plan before the parent exits. After the exact parent exits and
immediately before replacement, it rehashes every bound input, rechecks
Authenticode and both launcher/verifier pairings, and reruns the already-installed
verifier with the embedded approved root. The candidate helper and mutable plan
never authenticate their own replacement.

The updater acquires the launcher's cross-process operation lease and holds it
through re-verification, replacement, startup acknowledgement, and rollback. It
copies and verifies the old payload into transaction backup, writes a
current-user DPAPI-protected recovery journal that independently binds the
complete backup inventory and signed launcher/verifier pair, and only then
begins replacement. Candidate files are committed through adjacent durable
temporaries with the launcher replaced last. A complete launcher therefore
remains at the stable shortcut path across every individual file boundary; the
two-directory rename gap is not used. The new launcher acknowledges only after
WPF activation. Missing acknowledgement or early exit revalidates the protected
journal, backup inventory, Authenticode identities, and launcher/verifier
pairing before restoring the prior payload and restarting it. No elevation is
requested.

After a successful startup acknowledgement, the updater verifies the installed
payload again and durably writes a second current-user DPAPI-protected completion
journal before deleting any backup file. That terminal journal binds the exact
recovery journal and complete installed inventory. If backup cleanup is
interrupted, the next startup validates the acknowledged installation and
discards cleanup residue instead of misclassifying the partial backup as a
rollback candidate. An invalid acknowledged installation fails closed without
silently restoring stale bytes.

The update plan, protected recovery journal, external updater, and backup live
under the persistent state root, never inside the replaced program directory.
Normal startup first attempts the same cross-process operation lease; it skips
recovery and exits without opening a competing old launcher while an updater
owns that lease. An abandoned protected journal is
inspected without mutating the installation, then handed to the external
updater while the launcher exits. The updater checks the exact protected-journal
hash again before restoring. A backup without that independently protected
journal fails closed for manual recovery instead of trusting mutable plan hashes.
Independent mod/configuration operation journals are not
moved or deleted by standalone self-update. App Installer and MSIX uninstall do
not consume or remove those external state records.

Stable is explicit and offline use is unaffected: update discovery is
user-initiated from Diagnostics and has no bearing on local game launch. The
authenticated standalone command remains deliberately disabled in application
composition until issue #30 completes protected-release qualification.

The discovery client requires a candidate version to advance the running
launcher, preventing ordinary replay/downgrade. Emergency containment freezes
publication and adds a reviewed entry to
`docs/release-withdrawals/release-withdrawals.jsonl` without waiting for a
replacement. Preserve immutable release evidence; a higher independently
verified replacement is the normal recovery path. See
[`COMPROMISE_RESPONSE.md`](COMPROMISE_RESPONSE.md).
Manifest v1 is not detached-signed, so repository-control compromise remains a
documented residual rather than being mislabeled as solved.
