# Configuration workspace

The br-14 implementation in `bridge-engine::configuration` supplies an actor-local
Settings and Data Sync workspace. The engine uses portable contract DTOs and
native owner ports; it has no frontend, Tauri, webview or game dependency. This
document describes implementation and source validation. It does not declare
br-14 acceptance or native configuration qualification.

Opening a document captures its exact installation/profile, document ID, schema,
revision and physical baseline. A missing document is a virtual empty baseline.
Open, stage, protected capture, preparation and Discard do not persist the
configuration document.
Settings and Data Sync share the captured draft. The host must compose target
navigation and close decisions so Save succeeds before changing target, Discard
only releases draft intent, and Stay preserves the original target.

`bridge-app::configuration::ConfigurationServices` composes one privately owned
workspace with the operation ports inside the engine. `ApplicationServices`
exposes typed Read, History, Open, Stage, Discard and protected-entry methods;
the encoded dispatcher supplies their reply evidence and the single kernel
event stream. The engine validates the fixed service epoch and freezes the
implemented-command inventory at construction. Protected entry is advertised
only when the injected entry port declares availability. Save and Restore remain
unavailable until their opaque preparation and retained worker custody are
adopted; delegating other operation ports does not qualify configuration writes.

The generic operation kernel now moves an unconstrained opaque custody token
from capture to preparation to one admitted worker, and reconstructs separate
recovery-only custody from durable identities after restart. The composed
service delegates the same token directly; it creates no second workspace or
side map. This is a prerequisite foundation. Save/Restore capture, acquisition,
revalidation, advancement and recovery still refuse UnsupportedCapability until
their concrete configuration writer adopts retained begin and completion paths.

Before publishing a document observation, draft or protected transfer, the
service preflights the complete reply and every consecutive `DraftChanged`
event against the codec and host observation budget. A refused preflight keeps
the prior workspace and cursor. Fresh Open and Stage emit draft changes; exact
lost-ack Stage replay emits no duplicate event. Discard removes the draft and
returns its exact receipt without inventing a successor.

`get_draft` is an immutable lookup by host epoch and draft ID. It returns the
actual clean, dirty, invalid or stale generation, or Missing for a same-host
absent ID, without native owner I/O or identity allocation. Foreign hosts refuse
before lookup. Its cursor is a per-draft read watermark and cannot advance the
client's global event cursor past intervening operation events. Reconciliation
adopts an actual same-document snapshot, or the exact clean successor proven by
a completed Save's full capture and configuration receipt, while the renderer's
captured target, draft and local input custody still match. Missing, mismatched or changed
local custody preserves intent with a conflict; it cannot silently reopen or
retarget a draft.

A changed Save receipt must retain the captured document ID, physical target and
schema, prove the candidate digest, and account for the exact prior baseline
backup, with a fresh document revision and replacement file identity. Its clean draft advances one revision and clears only that capture's
edits, validation and apply metadata. NoChange cleans a captured dirty draft at
the same baseline and candidate digest; an already clean empty draft needs no revision bump. Reads
which arrive before matching completion evidence defer without changing the
draft watermark or global cursor. Later reads still consume intervening operation
events. NoChange cleanup also waits for completion evidence. An explicitly
requested empty staging successor may reconcile a lost acknowledgement at the
captured old baseline without claiming a completed Save. Newer unsynchronized edits, unfinished public input and synchronous
custody changes preserve local intent. Exact completion may retire synchronized
protected references; it never invents transfers or revives a discarded draft.

At the same host, queued navigation waits for the exact cleaned draft as well as
the completed operation. A refused or delayed draft query retains the review and
submission; retry observes rather than preparing or committing another Save.
Durable completion remains observable after host replacement, but old draft and
protected custody cannot be adopted into the new host. Restore marks matching
drafts Stale at their old binding and preserves edits for explicit recovery.

A correlated read or consecutive event can mark a clean/dirty draft Stale at
the same revision while preserving its complete captured document, schema,
edits, apply timing and protected references. Other same-revision rewrites,
unwatermarked stale assertions and reverse transitions at that revision refuse. After a read's
synchronous observation publication, reconciliation also rechecks the current
authoritative stream and exact stored draft: a newer event, tombstone or
invalidation cannot authorize adoption of the superseded read.

Staging replaces the complete edit set at an exact draft generation. The actual
Rust contract codec checks its result, including the complete encoded envelope
size, before the workspace advances revision or protected custody. An exact
uncertain-delivery retry returns the original acknowledgement and successor;
changed retries against the previous generation refuse. Public invalid intent
remains visible in the draft; sensitive plaintext supplied as a public edit
refuses atomically. Native/headless `SensitiveEntry` supplies protected bytes.
The renderer receives opaque private/secret references only. Closed, explicit
transfers rebind captured references to the successor generation, including one
transfer for repeated use. Foreign, forged, discarded and wrong-host references
do not acquire custody. Protected bytes remain actor-local and are cleared on
drop; native entry protection is the adapter's responsibility.

Identity allocation is fallible. A native entropy refusal publishes no draft,
protected reference or change acknowledgement and returns a closed failure.
IDs already issued during an incomplete operation remain burned; refusal does
not restore collision eligibility or trigger a retry. The application provider
uses one fresh sixteen-byte native entropy draw for each requested ID and the
canonical contract constructor. Entropy refusal and invalid constructor output
remain distinct failures.

`SchemaSource` supplies adopted producer encodings, aliases and precedence,
platform policy, Sync projection and mutations, migration, owned paths and
complete candidate validation. A UI field list cannot establish this policy.
Preparation writes sparse selected intent, proves the complete semantic tree
changed only in the allowed way, preserves semantically equal source spelling,
and validates the complete candidate. Removing an override is explicit.
Missing/no-change Save creates no empty document, backup or stage. Unsupported
schema or preservation syntax refuses rather than rewriting unknown content.
The real `CanonicalToml` implementation calls the canonical `bridge-toml`
`TomlClient`; it introduces no competing parser or filesystem writer.

Preparation owns no writer reservation. `DocumentOwner` must acquire canonical
document, installation and profile custody all-or-none, then revalidate exact
physical/revision identity under the retained lease. A losing writer performs no
stage, backup or journal work. Staging newer intent before acquisition or before
durable begin invalidates an old plan. Restore also revalidates the exact
retained backup subject under the lease; a preparation-time digest is no lock.
`revalidate_configuration` checks the captured draft, physical baseline,
retained Restore backup and adopted destination schema under the same held
lease without acquiring again. `begin_configuration` borrows a retained
`Option<PreparedConfiguration>` slot through every check and native begin.
Errors preserve the exact candidate allocation; success takes it once into
the transaction. An empty slot refuses before owner entry. A native-begin
error does not prove no effect or authorize retry: unresolved disposition
retains candidate and lease for deterministic recovery using the durable
recovery binding. These portable seams do not enable dispatcher Save/Restore.
Replacement requires a fresh file identity, the reviewed candidate digest and
an exact prior-byte backup for an existing baseline. Meaningful first Save uses
create-new semantics. Terminal replay performs no additional writes or revision
bumps. Native completion remains retained in the transaction and does not
publish local drafts or clear protected references. Repeated advancement returns
that exact outcome without invoking the writer again. The workspace prepares
every local successor and committed-document read, then invokes the kernel's
durable commit callback before infallible publication. Callback or read refusal
preserves local intent and the same candidate/lease; retries rebuild projections
against current local edits. An exact matching Save installs a clean successor baseline once; newer
local edits remain stale and available for an explicit recovery choice.

The composition layer must still implement configuration `OperationPorts`
preparation and worker custody. It must persist
executing admission and the owner's exact recovery binding before invoking
`begin_configuration`, and retain the actual lease through advancement and a
verified terminal or safe recovery boundary. Observation loss or caller
cancellation cannot release a writing lease. Precommit cancellation may report
no effect; a late request cannot undo a committed result or interrupt a
synchronous native ABI call. Ambiguous promotion retains recovery-required
custody. Normal host exit must honor the kernel's close obligations.

The kernel prevalidates the terminal operation, snapshot budgets and every
consecutive OperationChanged/DraftChanged envelope before the terminal append.
Each operation and draft event must also fit inside its actual single-event
replay reply. A near-limit raw event cannot strand the replay cursor after
completion. Refusal occurs before terminal append and local publication, with
the native result and lease retained for retry. A retained event that cannot
fit the first replay reply requires resnapshot instead of an empty stalled read.

Local publication follows a successful append; event emission then uses only
prevalidated envelopes. Pending local completion keeps the worker even when
the native owner reached a safe boundary. Unknown append disposition poisons
the host without publishing a local successor. Repeated commit callbacks,
post-commit refusal or incomplete local publication poison and retain custody.
The composed operation port forwards these hooks; concrete configuration
custody variants remain prerequisites to Save/Restore availability. Receipt-bound
frontend reconciliation does not enable the native configuration writer.

Restart recovery consumes identity-only `RecoveryConfiguration` captured from
the durable operation, including baseline, destination schema and candidate
digest. It reconciles exact deterministic owner subjects under newly acquired
custody. It does not recreate protected values lost with the previous host,
overwrite a foreign destination or claim that execution survived process death.
Old host drafts and uncommitted plans are not recovered as current edit custody.

Run the portable source validation from the canonical checkout:

```powershell
node scripts/next/configuration-workspace.mjs
```

The command accepts no overrides. It uses the tracked Node and Rust releases,
the scoped Rust child context, locked/offline dependencies and the actual native
Windows x64 or Apple Silicon target. It checks engine and application formatting
and strict all-target Clippy, then selects all four configuration test
executables, both configuration-service/operation integration executables and the
application-provider library test executable from their
current invocations' Cargo JSON compiler artifacts. It verifies owning manifest
and source, physical confinement to the owning native target directory, native
executable architecture and byte hashes. It inventories and executes every
required test, refusing missing, duplicate, ignored or filtered tests. Current
inventories cover 19 draft, 11 semantics, 24 transaction, 4 recovery, 12 service,
18 configuration-operation and 16 application-provider tests, for 104 tests
across seven executables. Configuration-operation controls compose the actual
kernel and FileJournal with synthetic owners: read-only capture, exact retained
leases, WAL before begin, stale/losing admission, cancellation, uncertain begin,
terminal retry, protected non-disclosure and identity-only restart. Service
controls use the real encoded dispatcher and original-thread host handle, check
immutable getter correlation, command availability, shared event sequencing,
protected non-disclosure and refusal before workspace publication. Provider
controls cover one draw, refusal, closed
constructor errors and absence of retry/cache; the supported-host test also
calls the actual current-host clock and entropy providers. Passing on one host
does not establish provider execution on the other host.

Fresh draft synchronization always advances one revision, including unchanged
edits after Stay or failed preparation. Exact lost-ack replay returns its original
successor. Saved private handles distinguish scalar fields, Sync endpoints and
Sync proxies by destination and never change their bound payload.

Preservation checks include semantic table paths as well as values. Removal and
renaming require explicit ownership of every consumed and relocated table/key.
Set requires new parent tables. Unknown and initially empty tables remain intact;
only explicitly owned ancestors emptied by removal/movement may be pruned.
ABI v1 does not expose explicit-header metadata, so this consumer check permits
owned ancestor pruning without claiming independent header-preservation proof.
Inline aggregate transformations remain unsupported when strict value proof
cannot establish preservation.

Receipts under ignored `artifacts/next/configuration-workspace/<invocation>/`
retain command status, stdout/stderr and log hashes; source/directory inventories
before and after; actual Node, shim and Rust toolchain payload hashes; the Cargo
artifact records; executable hashes; and discovered/executed tests. A failed
command retains its observation and a failed receipt. Stable disk/hash checks
are observations, not native exclusions or protection against transient changes.

These tests use synthetic `DocumentOwner`, `SchemaSource`, `SensitiveEntry` and
`TomlPreparation` ports. They exercise the real workspace, transaction model and
Rust contract codec, including acknowledgement preflight, using synthetic
source bytes and fixtures. Their preservation examples do not qualify genuine
native TOML syntax breadth or operating-system persistence. The installed
application, actual game, account/profile stores and native configuration files
are not part of this suite.

The remaining integration and acceptance seams are:

- Invoke the real canonical TOML module with verified producer/source/binary
  pairing, and qualify the preservation matrix, including BOM, Unicode, quoted
  paths, interleaved sections, arrays and table migrations.
- Adopt actual producer `SchemaSource` policy and runtime/schema compatibility;
  the synthetic schema is not product authority.
- Implement ordinary-user physical `DocumentOwner` and native `SensitiveEntry`
  adapters with private staging, exclusion, flush, backup, create-new, atomic
  replacement and exact Restore/recovery custody on both required platforms.
- Connect the portable opaque `OperationPorts` custody to qualified native
  document owners. The existing `ConfigurationServices` workspace now supplies
  Save/Restore capture, all-or-none owner acquisition, revalidation, retained
  worker custody, terminal publication and identity-only recovery. An uncertain
  begin keeps its candidate slot and enters recovery without invoking begin
  again. This composition introduces no second workspace or serialized private
  candidate. `DocumentOwner::operations_available()` defaults to false; synthetic
  test owners opt in explicitly. Production owners must retain the default until
  their actual writer/recovery composition passes the required host qualification.
- Retain prerequisite native receipts, independent candidate review and the
  package qualification evidence required by the operating contract.

A passing portable receipt observes the configuration read/stage dispatcher and
sets `sourceOperationPortsAdopted` for the composed synthetic-owner controls. It
keeps `br14Accepted`, native TOML,
producer policy, physical owner, `OperationPorts` adoption, native runtime and
release qualification false. Root owns suite registry and campaign admission;
this script does not create or modify either.

The registered `configuration-workspace` suite supplies source observations.
Package selection refuses with `PACKAGE_INTEGRATION_UNQUALIFIED` while the
required native integration is incomplete. Explicit suite selection still
requires the declared prerequisite receipts; it cannot substitute a local
native probe for the br-05 two-host matrix.

The source criteria are BR14-01 (exact sparse/no-change preparation), BR14-02
(complete candidate preservation and producer-policy boundaries), BR14-03
(captured draft/protected-reference transfer and retry), BR14-04 (physical
revision/backup revalidation before durable begin), BR14-05 (cancellation,
terminal reconciliation and retained draft), and BR14-06 (identity-only restart
recovery and fail-closed executable/evidence inventory), and BR14-07 (one
engine-owned service workspace, current-draft correlation, shared event stream
and reply/event preflight before publication). Their portable tests
leave the native integration boundaries above unqualified.
