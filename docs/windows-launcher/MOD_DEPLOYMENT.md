# Windows Launcher Mod Deployment

Status: WL-004 transaction core and Home install/update/repair confirmation are implemented; recovery/uninstall are
available through Diagnostics with exact-target confirmation. The issue #45 source-lifecycle and protected TOML-backup
contract below is proposed for review; its orchestration, storage, restore surface, and selector polish are not yet
production-ready. Installed-client mutation smoke remains in progress.

## Ownership boundary

Mod Bridge manages only `version.dll` plus its transaction-scoped stage and
rollback names listed in
[`GAME_DIRECTORY_FILE_ALLOWLIST.md`](../GAME_DIRECTORY_FILE_ALLOWLIST.md).
It never treats the selected game directory as Mod Bridge-owned. Existing
manual `version.dll` files are compared only after the user chooses **Check for
updates**. A separately confirmed replacement preserves the prior bytes under
Mod Bridge-owned rollback state.

## Mod source-selection lifecycle

This is the authoritative lifecycle contract for issue #45. Provider-pack capability data remains authoritative for
what a source supports; this document owns how preferred source, installed artifact, configuration safety, and user
actions compose. UI wording must be derived from these states after this contract is accepted.

### Independent state axes

| Axis | Durable meaning | Authority |
|---|---|---|
| Preferred update source | The `{ providerId, releaseChannelId }` the player chose for future release checks and migration previews. | `provider-selection.json`; never mod TOML and never evidence about the installed DLL. |
| Installed artifact ownership | No artifact, externally installed, verified Mod Bridge-managed, managed bytes changed, or recovery required. | Live `version.dll`, the deployment journal, and installed-artifact state. |
| Installed provenance | Exact managed attribution, exact reviewed hash, self-declared runtime lineage, malformed/unknown identity, or unavailable evidence. | Bounded passive binary inspection; never inferred from preferred source or display name. |
| Latest known release | Absent, current, update available, withdrawn, or unavailable, with observation time and exact provider/channel/runtime/artifact-hash binding. | A bounded observation created only by explicit **Check for updates**. |
| Runtime state | Game closed or running; the DLL on disk is not proof of the image already loaded by the process. | Process/runtime health evidence. |

The axes do not collapse into one `current source` value. In particular:

- selecting a source changes preference only; it does not adopt, rename, rewrite, or attest the installed DLL;
- a managed record remains attributed to the provider/channel/runtime that supplied its verified bytes even when the
  preferred source changes;
- an unmarked, unknown-hash, or developer DLL is an external/custom artifact and remains runnable when the ordinary
  bounded file-safety checks pass; it is not unhealthy merely because Mod Bridge cannot authenticate it;
- if a formerly managed DLL changes, Mod Bridge suspends its managed-integrity claim and must not overwrite or delete
  the new bytes automatically. A future surface may let the player explicitly keep it as custom or replace it through
  a confirmed repair/switch transaction;
- passive Home, Settings, Diagnostics, startup, and source selection perform no release-network discovery.

Artifact download is naturally a network operation after the player confirms an install/update/switch/repair prepared
from reviewed release evidence. It is distinct from querying which release is latest. **Check for updates** is the only
latest-release discovery entry point and must produce an identity-bound, expiring observation; execution must not
silently query `latest` again.

### User-visible state projection

| Installed state | Preferred relationship | Primary presentation | Default next action |
|---|---|---|---|
| No DLL | Any resolved preference | Community mod not installed | Check for updates, then Install a prepared release |
| External/custom DLL | Unknown or matches preference only by inspected evidence | Manual or custom installation detected; runnable, not managed | Check for updates; replacement is a confirmed switch, never an automatic update |
| Verified managed DLL | Same provider/channel as preference | Installed provider, channel, and version | Check for updates |
| Verified managed DLL | Different from preference | Installed from A; future checks prefer B | Review switch to B; do not relabel the installed DLL |
| Managed record but live bytes changed | Any | Installed file changed; Mod Bridge management is suspended | Keep custom or explicitly Repair/Replace; never silently overwrite |
| Incomplete journal or unsafe/unreadable state | Any | Recovery required or state unavailable | Recover/Diagnostics; block mutation and direct launch when safety cannot be established |

### State/action matrix

`Fresh observation` below means a release observation bound to the exact preferred provider/channel/runtime and the
current installed hash (or explicit no-artifact state), created by **Check for updates** and still within its configured
age limit.

| Action | Valid start and prerequisites | Confirmation and TOML backup | Running game and restart | Transaction, rollback, and result |
|---|---|---|---|---|
| **Select source** | Initial/default/invalid preference, or any installation whose preferred source should change. Target stable IDs must resolve in the catalog. No release observation is required. | Confirm the target provider/channel and capability losses. No TOML backup: this action changes only launcher preference. Staged Settings/Data Sync edits must be saved or discarded first. | Allowed while the game runs because no game file changes. The running game is unaffected. Restart Mod Bridge before target-owned catalogs or services are composed. | Atomically replace only preference state. On failure, retain/restore the previous bytes. Installed ownership/provenance and TOML remain unchanged. The existing UI currently calls this a switch; that wording is not accepted. |
| **Install** | No DLL, valid writable game target, resolved preferred source, and a fresh prepared release observation. | Confirm exact game target, provider/channel, version, hash evidence, and effect. If an active TOML already exists, offer a protected backup; no backup is needed when no TOML exists. Install never rewrites TOML. | Block while STFC runs. A launcher restart is needed only if preference/catalog composition changed; launch the game after commit to load the new DLL. | Existing verified deployment journal. Failure restores no-artifact state. Result is managed state attributed to the artifact source; preference is not rewritten as a side effect. |
| **Update** | Verified managed DLL, same provider/channel/runtime as the fresh observation, and a newer explicitly prepared release. Downgrades require separate explicit confirmation. | Confirm from/to versions and artifact evidence. Protected TOML backup is optional for an ordinary same-lineage update and mandatory if catalog migration evidence says configuration compatibility is unknown or lossy. | Block while STFC runs; after commit the next game launch loads the update. | Replace through the deployment journal and preserve managed installed state on failure. An external/custom DLL is never routed through Update. |
| **Switch installed mod** | Existing managed or external DLL and a fresh target-provider observation. Provider/runtime differs, or the player explicitly replaces custom bytes with the preferred provider. Compatibility preview and exact target artifact must be available. | Typed target-ID confirmation. Protected TOML backup is mandatory when the active TOML exists; an explicit no-file record satisfies the gate when it does not. Existing DLL bytes are also preserved by deployment. TOML is not migrated or normalized automatically. | Block while STFC runs. Commit requires Mod Bridge restart before target catalogs/actions are used; the next game launch loads the target DLL. | One durable source-transition journal coordinates config backup, artifact deployment, installed attribution, and preference commit. Commit preference last. Any failure compensates to the prior artifact, installed record, and preference; the protected config backup remains. |
| **Repair** | A managed record exists but the target is missing/changed, and the exact same provider/channel/runtime/version artifact remains independently verifiable. Repair cannot mean `install latest`. | Confirm that current bytes will be replaced and preserve changed bytes for rollback. TOML backup is not required because exact managed lineage is restored and TOML is untouched. If exact bytes are unavailable, fail closed and offer Check for updates as a separate update/switch decision. | Block while STFC runs; next game launch loads repaired bytes. | Existing repair journal. Restore the previous live bytes and installed state on failure. Result retains the original managed attribution. |
| **Remove** | Verified managed DLL matching installed state. A changed/custom DLL is not deleted. | Confirm exact target and whether an originally adopted DLL will be restored. TOML is always preserved. A protected TOML backup is mandatory if removal restores an adopted artifact with different or unknown lineage; otherwise it is optional. | Block while STFC runs; next game launch reflects removal/restoration. No source-preference change or launcher restart is implied. | Existing uninstall journal removes a fresh managed DLL or restores the original adopted bytes. Failure restores the managed DLL/state. Result is no artifact or external/custom; preferred source remains. |
| **Check for updates** | Resolved preferred provider/channel and safe local evidence capture. | Explicit user action; no mutation and no TOML backup. Show which source will be queried. | May run while STFC runs, but any resulting mutation remains disabled until it closes. No restart. | Store only bounded, expiring observation metadata keyed to preference and current artifact evidence. Failure leaves local runnable/managed state truthful and offline-capable. |

An Install surface may combine the explicit check and review into a guided flow, but it must label the network step and
receive affirmative user input; opening the selector or displaying Home must never trigger it. Whether v1 uses a
separate Check button or a labeled Install wizard step is a presentation decision still requiring Guff/Lex acceptance.

## Protected TOML backup contract

The v1 configuration set is exactly the selected game root's `community_patch_settings.toml`. It contains scalar,
hotkey, notification, and Data Sync overrides. No glob, sibling TOML, log, cache, credential file, or provider-defined
path is implied. A future provider may add a file only through reviewed catalog metadata, containment validation, and a
new contract/schema version. A missing file is recorded as absent and does not create an empty substitute.

### Capture and metadata

For every included file, capture one bounded exact-byte snapshot without parsing or reserialization. Preserve UTF-8
BOM, line endings, whitespace, comments, unknown keys, ordering, sparse omissions, and invalid-but-runnable syntax.
Read/hash/copy must close the preview-to-commit race: compare the source length and SHA-256 before capture, verify the
encrypted payload after capture, and recheck the source immediately before the destructive commit. A mismatch cancels
the operation and deletes the incomplete backup envelope.

The backup manifest is schema-versioned and contains only non-secret recovery metadata:

- backup and source-transition transaction IDs;
- action kind and UTC capture timestamp;
- stable game-installation identity and the file role, not a raw profile/game path;
- original byte length, uppercase SHA-256, and original last-write timestamp in UTC;
- previous installed provider/channel/runtime IDs when established, otherwise explicit `external`/`unknown`;
- target provider/channel/runtime IDs;
- payload-protection scheme and ciphertext length/SHA-256;
- committed, rollback-required, restored, or deleted lifecycle state.

The absolute restore path and exact TOML bytes belong inside the protected payload, not the clear manifest. File names,
provider IDs, sizes, timestamps, and hashes are data, never display-name routing. Hashes are integrity evidence, not a
claim that the configuration is non-secret.

### Protected storage and retention

Store envelopes beneath the per-user Mod Bridge state root at
`configuration-backups/<installation-id>/<provider-id>/<backup-id>/`, never beside the game and never in the ordinary `.bak` path
used for one-document editor commits. Encrypt payloads with Windows DPAPI `CurrentUser` scope and apply an explicit
DACL limited to the current user and `SYSTEM`; failure to establish either protection fails the mandatory-backup gate.
No plaintext temporary copy may survive the operation. The UI must explain that backups are local to the same Windows
profile and are not portable exports.

V1 retains the newest five verified backups per stable provider ID and game-installation identity. Retention is
count-based only: there is no age grooming, and one provider can never prune another provider's history. Never prune an
envelope referenced by an incomplete/recovery-required transaction. Prune only after the replacement backup is
protected and byte/hash verified. Users may explicitly delete a completed backup; uninstalling the mod does not delete
provider history, while the launcher's separately confirmed **Remove state** operation does.

### Explicit restore

Manual restore is a Diagnostics/recovery operation. A reviewed installed-provider switch may separately propose the
latest verified target-provider backup and restore it inside the coordinated switch transaction; a first-time target
with no history preserves the active TOML rather than inventing a migration. Manual restore requires:

1. the same Windows user can decrypt the envelope and all manifest/ciphertext hashes validate;
2. exact game-installation and destination-file confirmation;
3. STFC closed, no staged Settings/Data Sync edits, and the common mutation lease;
4. a fresh mandatory pre-restore backup of the current destination when it exists;
5. a warning and second confirmation if current destination bytes differ from the state observed when restore began;
6. same-volume staged write, exact-byte/SHA-256 verification, atomic replacement, restoration of the recorded
   last-write timestamp, and post-commit verification;
7. a durable restored marker; the source backup remains until normal retention or explicit deletion.

Failure restores the pre-restore bytes and timestamp. If compensation cannot complete, persist recovery-required state
and block further mutation. Restoration never changes preferred source or installed DLL provenance and does not claim
the restored TOML is compatible with the active runtime.

### Secret, log, and support boundary

TOML may contain tokens and private endpoints. Therefore backup payloads, decrypted bytes, absolute paths, backup IDs,
full hashes, and exception data containing them must never enter logs, telemetry, crash attachments, clipboard text,
diagnostic preview/export, or support bundles. Diagnostics may report only aggregate backup count, age band, protection
health, and whether recovery is required. Export collectors must deny the backup root and envelope extensions before
redaction; redaction remains defense in depth, not the primary control. The current prototype's display of a plaintext
backup path is specifically outside this proposed contract.

### Accepted v1 decisions

The safety invariants above are requirements. V1 uses these accepted choices:

1. reserve **Select source** for preference-only state and **Switch installed
   mod** for artifact-lineage migration, replacing the prototype's overloaded
   **Switch source** wording;
2. when live bytes differ from managed state, suspend management and preserve
   custom/dev runnability rather than treating unknown bytes as automatically
   corrupt; destructive Repair/Replace remains explicit;
3. allow the read-only explicit Check-for-updates operation while STFC runs,
   while keeping every mutation blocked until it closes;
4. use the mandatory/optional TOML-backup gates in the action matrix,
   especially mandatory capture for cross-lineage switch and adopted-artifact
   restoration;
5. use non-portable DPAPI `CurrentUser` plus an explicit DACL for v1 rather
   than designing a password/export key flow; and
6. retain the newest five verified backups per provider and installation with no age-based grooming.

The Install presentation may use a separate Check button or an explicitly
labeled network step inside a wizard; either choice must preserve the same
no-passive-discovery service contract.

## Source-transition transaction and ownership

Artifact replacement, preference persistence, and protected backup cannot be one filesystem atomic operation.
`LauncherProviderAtomicSwitchCoordinator` uses a dedicated cross-process
provider-switch admission lease, then enters the deployment service's exact
installation-scoped mutation lease before any artifact commit. It journals
compensating steps in this order:

```text
prepared -> artifact-committing -> configuration-committed -> completed
                    |                       |
                    v                       v
              rolling-back -> rolled-back / recovery-required
```

An incomplete source transition blocks another switch and is projected as a
Recovery action. Rollback restores the prior artifact, installed-state bytes,
preference, and exact prior TOML. The protected source backup is retained as
evidence/recovery material. A selection made while no DLL is installed uses the
smaller preference/TOML transaction, performs no release discovery or download,
and does not create the outer artifact journal.

Implementation ownership is intentionally bounded:

| Owner | Follow-up responsibility |
|---|---|
| Provider catalog/resolution | Stable source/channel/runtime IDs, migration compatibility and capability loss; no display-name checks and no installed-provenance claims. |
| `LauncherProviderSelection` | Stable preference load/save, compatibility preview, protected source capture, target-history restore, and exact compensation. |
| Core source-transition coordinator | Prepare immutable identity-bound operations; coordinate deployment, installed state, preference, TOML, rollback, recovery, and restart result. |
| Deployment transaction | Keep allowlisted `version.dll` staging, verification, installed attribution, and exact rollback copy alive through the coordinated commit participant. |
| Protected configuration-backup store | Exact-byte capture, DPAPI/DACL protection, manifest validation, five-record provider/install retention, restore, and no plaintext residue. |
| Binary provenance/local health | Project installed and preferred sources independently; keep custom/dev DLLs runnable; suspend rather than transfer ownership when managed bytes change. |
| Diagnostics/export | Metadata-only backup health plus explicit restore/delete workflows; hard exclusion of payloads, paths, IDs, and hashes from preview/export/support evidence. |
| Home/source UI | Project the accepted matrix, immediate-effect labels, current-installed versus preferred-source text, confirmations, running-game/restart status, and one border per surface. No lifecycle decisions in WPF. |

Regression coverage includes passive no-network source selection; exact-byte
TOML capture/restore; provider/install retention; stale revisions;
game-running denial; provider/channel/runtime binding; coordinated rollback;
startup recovery after DLL and configuration commit; and a live
Guffawaffle → NetniV → Guffawaffle round trip. Follow-up coverage remains for
custom/dev adoption, every process-termination phase, broader secret/export
audits, and exhaustive contention between provider switching and independent
Settings/Data Sync writers in another Mod Bridge process.

## Verified transaction

`ModDeploymentService` is UI-independent and enforces this order:

1. validate an exact game directory containing `prime.exe`;
2. reject mutation while the game is running or another process holds the
   operation lock;
3. fail closed on an incomplete or unreadable persisted journal;
4. require HTTPS plus bounded artifact metadata;
5. verify HTTP status, declared length when present, actual length, and
   SHA-256 before writing beside the game;
6. write and re-verify a same-volume stage file, then require Windows
   `WinVerifyTrust` and the configured Authenticode publisher identity;
7. journal `Committing`, preserve any existing artifact, and replace the
   target;
8. re-verify target size, SHA-256, and the expected embedded numeric file
   version;
9. persist managed ownership before journaling `Committed`.

The expected embedded version is deliberately separate from the release tag.
For example, release `2.1.0-guffa.8` carries numeric Windows file version
`2.1.0.8`; release discovery must perform that explicit mapping instead of
comparing the descriptive tag directly to `FileVersionInfo`.

`WindowsReleaseManifestParser` and `WindowsReleaseSelectionPolicy` provide that
bridge from the published release contract. They reject unknown schema fields,
withdrawn or wrong-channel releases, unexpected repositories, incompatible
minimum launcher versions, ambiguous artifacts, unsafe filenames, unsupported
authenticity declarations, and release versions that cannot map to an embedded
numeric version.

The HTTP downloader refuses to buffer more than 128 MiB. Unknown journal
schemas, corrupt state, invalid metadata, non-HTTPS artifact URLs, wrong
targets, and externally changed managed DLLs fail closed.

## Recovery and uninstall

Every phase is persisted through an atomic state-file replacement. An
incomplete transaction blocks new mutations. `RecoverAsync` is deterministic
and idempotently restores the preserved artifact or removes a partially
committed fresh install, restores the previous installed-state record, and
removes transaction-scoped files.

Uninstall verifies that the live DLL still matches Mod Bridge-managed state. It
then uses the same operation lock and journal boundary. A fresh managed DLL is
removed; an explicitly adopted prior DLL is restored. Configuration, logs,
runtime snapshots, and unrelated game files are untouched. If the managed DLL
changed outside Mod Bridge, uninstall refuses to guess ownership or delete
it.

Managed updates retain the original adopted artifact identity rather than
turning the immediately previous managed release into the uninstall target.
Explicit repair may replace a missing or changed Mod Bridge-managed DLL only
after the same release verification and transaction checks; the changed bytes
remain available for rollback until repair commits.

## Launcher-local health contract

Home and Diagnostics consume the same composable `LauncherHealthSnapshot`.
The installation inspector distinguishes no target, invalid target, missing,
manual/unmanaged, verified managed, externally changed, recovery-required,
and unreadable state. DLL presence alone is never reported as healthy managed.

New deployments require and persist stable provider, release-channel, and
runtime-distribution IDs beside the verified version and SHA-256. Unattributed
managed state is invalid in this unreleased greenfield application; manual DLLs
remain separate evidence and are never attributed from the current selection.

Every present DLL is inspected through the bounded, non-executing contract in
[`MOD_BINARY_PROVENANCE.md`](MOD_BINARY_PROVENANCE.md). Exact reviewed hashes,
self-declared lineage, unmarked custom builds, malformed identity, and
unavailable metadata remain distinct evidence states.

Update availability is a separate, time-bounded observation. It is accepted
only when the observation matches the installed artifact hash plus the
selected provider, channel, and runtime-distribution identities. Missing,
stale, or mismatched observations resolve to `Unknown` and do not override a
verified local installation, so an offline launcher can still report truthful
local readiness.

Game compatibility, runtime activation, and native-hook support are distinct
dimensions behind an evidence-source contract. The v1 source reports live
states as explicit `Unknown`; it does not infer loaded or healthy hooks from a
DLL, process, or log. The resolver can project authoritative `Healthy`,
`Degraded`, or `Incompatible` evidence when a future identity-bound native or
provider contract supplies it. Live dimensions are `NotApplicable` while the
game is closed.

## Automated evidence

The core test suite covers:

- invalid game targets and game-running denial before download;
- explicit, separately confirmed replacement of a pre-existing DLL;
- non-200 HTTP, declared-size, actual-size, and SHA-256 rejection;
- bounded HTTP reads;
- mandatory Authenticode rejection before commit;
- embedded-version mismatch rollback;
- injected failure after every persisted deployment boundary;
- concurrent mutation denial;
- corrupt persisted state;
- required attributed deployment persistence and rejection of unattributed managed state;
- missing, manual, verified, damaged, incompatible, update-available, running
  healthy, running degraded, and explicit-unknown local-health states;
- provider/channel/runtime identity and update-observation freshness;
- provider-unavailable offline health that preserves local readiness;
- startup recovery from an interrupted commit;
- allowlist-only uninstall, adopted-artifact restoration, external-change
  refusal, and uninstall rollback.

The packaged Home smoke confirms that mod state and its action are accessible,
while deliberately stopping before confirmation. Installed-client mutation
smoke remains deferred until a release publishes the canonical Windows
manifest and artifact consumed by discovery. Unit tests use isolated synthetic
game/state directories and do not modify the real STFC installation.
