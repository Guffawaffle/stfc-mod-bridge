# Battle Bridge local IPC boundary

Status: authenticated named-pipe transport, lifecycle recovery, provider-session
projection, and signed-package qualification gate implemented but dormant;
operational activation remains blocked on accepted signed evidence and production
provisioning composition

Tracking issue: [#132](https://github.com/Guffawaffle/stfc-mod-bridge/issues/132)

## Boundary

Local component-to-component communication is authenticated IPC. Network
communication, when separately authorized, is HTTPS. The current product gives
neither Bridge nor Battle Bridge general outbound Internet-service authority.
Existing reviewed release and update downloads remain narrow launcher
infrastructure and do not grant that authority to a feature, module, or runtime.

The current Windows IPC direction is named pipes, not HTTP, HTTPS, WebSockets,
or a localhost TCP listener. The dormant #62 loopback HTTP proof is retained as
bounded implementation evidence only. Runtime capability evidence, product
policy, or player preference cannot start it.

This distinction is intentional:

```text
local machine

STFC mod -------- authenticated named pipe --------> Battle Bridge authority
Bridge ---------- authenticated named pipe --------> Battle Bridge authority
reviewed module -- authenticated named pipe --------> Battle Bridge authority

external

no Bridge or Battle Bridge product-feature route is currently authorized
```

Binary signing contributes to executable provenance, but there is no "signed
socket." The IPC owner must independently authenticate the connecting local
identity, negotiate an accepted protocol version, authorize the requested
operation for that identity's role, and fail closed.

## Roles and least authority

Participants are not interchangeable merely because they use the same
transport:

| Authenticated role | Candidate capability set |
|---|---|
| STFC mod runtime | Submit only the positively negotiated telemetry/event families for the current session |
| Bridge shell | Read bounded health, manage explicit player intent, and request reviewed lifecycle operations |
| Reviewed optional module | Only individually granted module capabilities; no inherited Bridge or runtime authority |

The eventual authorization table must be closed and versioned. Unknown caller
identity, unknown role, unknown operation, undeclared event family, protocol
version mismatch, stale credential, or capability loss rejects the request.
There is no generic command execution, arbitrary filesystem operation, or
forward proxy capability.

## Required activation proof

No operational local endpoint is enabled until one reviewed implementation
proves all of the following together:

- an exact per-user pipe name derived by the lifecycle owner without a second
  state-root resolver;
- an explicit pipe ACL for the intended Windows principal/package boundary;
- caller provenance and role binding, with signed binaries used only as one
  provenance input rather than the authorization decision by themselves;
- a bounded closed protocol with explicit version negotiation, message and
  concurrency limits, duplicate-field rejection, and whole-message atomicity;
- per-role, per-operation, and per-feature capability authorization;
- launcher-owned credential creation, DPAPI protection, rotation, revocation,
  and plaintext-runtime handoff boundaries;
- one lifecycle owner, deterministic shutdown/drain, capability-loss
  revocation, crash recovery, and no inactive-mode resource creation;
- MSIX and standalone behavior at medium integrity, including collision,
  upgrade, uninstall, and package-content inspection;
- diagnostics that expose bounded state and reason codes without pipe names,
  credentials, raw event payloads, paths, or endpoints.

The proof must also demonstrate that inactive and merely `available` features
create no pipe, database, timer, thread, worker, or network activity.

## Implemented dormant proof

`BattleNamedPipeIngestHost` is the current transport proof. It is not registered
in launcher startup, a Home, Settings, Diagnostics, or any runtime-composition
factory. Construction is passive, and an inactive activation returns without
creating a pipe or worker.

The proof currently accepts only one role and operation:

| Role | Operation | Authorization |
|---|---|---|
| `stfc-mod-runtime` | `ingest` | Windows current-user pipe isolation, exact 32-byte credential, and a lifecycle-supplied receipt binding process ID, process start time, executable path, and reviewed runtime-evidence SHA-256 |

Bridge management remains in-process and optional-module roles are denied; the
proof does not invent unused pipe APIs. A future module capability adds a new
closed role/operation contract rather than inheriting runtime or Bridge
authority.

Each connection is one bounded request. It first sends a maximum-4-KiB closed
JSON header containing the exact protocol version, role, operation, and
credential. Duplicate (including escaped-equivalent), unknown, missing,
non-string, control-bearing, or noncanonical properties reject. Only after
fixed-time credential comparison, OS client-PID lookup, and exact process
receipt authorization does the server return `ready`. The client then sends one
length-prefixed exact ingest envelope, capped by the existing 512-KiB request
limit. The shared parser retains exact bytes, family/capability validation,
chunk limits, and whole-batch atomicity before the existing sink is called.

Pipe instances use `PipeOptions.CurrentUserOnly`, asynchronous byte mode, and
`FirstPipeInstance` for collision failure. Handler admission is capped by the
existing 16-request limit. Stop ends acceptance, drains within the reviewed
bound, then cancels and joins the cancellation-compliant sink. Health contains
only state, bounded counters, a typed failure, and transition; it exposes no
pipe name, PID, executable path, evidence hash, credential, payload, event ID,
or endpoint.

The proof deliberately does not create/rotate the credential, select the pipe
name, discover a game process, inspect a runtime artifact, acquire
`runtime.lock`, open SQLite, edit TOML, or decide eligibility. Those receipts
must be injected by the accepted lifecycle and activation owners. The signed
release workflow now carries a standalone and medium-integrity MSIX package
qualification gate before attestation; its first real signed execution remains
required evidence before composition.

Production caller discovery now has a separate dormant exact-receipt seam. It
enumerates only currently running `prime` processes, accepts exactly one process
whose executable path is the selected game installation's exact `prime.exe`, and
binds its positive PID, UTC start time, normalized path, and the injected
reviewed-runtime evidence SHA-256 into `BattleNamedPipeAuthorizedProcess`. The
handshake authorizer reopens that PID and rechecks start time and path, preventing
PID reuse from inheriting the receipt. No provider name participates.

Zero exact matches is `absent`; multiple exact matches is `ambiguous`; any
uninspectable process, duplicate PID, invalid timestamp/path, capture failure, or
more than 64 observations is bounded `unavailable`. An invalid evidence digest
rejects before process capture. Construction and discovery start no process,
listener, watcher, timer, or background task and create no filesystem state. The
seam is not registered with the shell or runtime coordinator yet.

The dormant runtime-owner opener now covers the later-session half of the
lifecycle handoff. It runs only beneath the existing root `operation.lock`,
requires the Battle lifecycle journal and root delete recovery state to be
absent, opens the exact existing `battle\runtime.lock` without following a
reparse point, and retains that exclusive handle. A canonical prior `clean`
record is preserved as clean-start evidence; a canonical prior `running` record
is preserved as unclean-start evidence for the storage recovery owner. A live
owner is `busy`, malformed bytes are `invalid`, and recovery/delete ownership is
`recovery-required` without rewriting the lock.

This opener never creates `battle` or an unjournalled `runtime.lock`. Initial
activation must hand off the exact marker-bound lease created by the lifecycle
transaction; clean shutdown rewrites that retained file through the same handle
and leaves it for a later session to reopen. Construction remains passive, and
the opener is not registered with launcher startup or the runtime coordinator.

Terminal lifecycle cleanup now has that exact live-lease handoff path. It holds
the runtime lease's operation gate across cleanup, revalidates its exact path,
marker-bound byte count/SHA-256, owner, `running` record, and closed-schema bytes,
then removes the bound candidates and operation marker while retaining the
exclusive runtime file. Clean shutdown can therefore write `clean` only after
marker cleanup finishes. If the process dies instead, the existing recovery path
has no retained lease and removes the now-stale marker-bound runtime file before
finishing cleanup. No path-presence inference selects between those paths.

The pipe host rejects the older #62 capability-plus-demand activation object.
Its only accepted activation is derived from `LauncherBattleFeatureSnapshot`,
which already combines normalized runtime capability, checked-in product
policy, and independent per-feature player preference. An unavailable,
available/unset, or disabled feature therefore cannot create an accepted ingest
family even if a caller invokes the legacy helper directly.

The producer boundary is now aligned by
[stfc-mod#245](https://github.com/Guffawaffle/stfc-mod/pull/245), from signed
source commit `5020d388ce1186ebc563497b12c69c6ce7525742` and merged as
`722c37e`. Its exact `named_pipe` transport uses this protocol, closed role and
operation, canonical credential encoding, length and deadline bounds, and
fails closed without falling through to legacy HTTP. That merge is producer
evidence only; it does not activate the Bridge host.

The launcher single-writer topology now recognizes the exact `named_pipe`
transport and `pipe_name` fields. Its typed transition clears the obsolete
legacy URL, preserves unrelated TOML, and rejects noncanonical transport or
pipe-name values. A named-pipe target is lifecycle-managed and read-only in
the generic Data Sync workspace; the global Settings search also excludes
dedicated Data Sync keys so it cannot become a second TOML writer. The final
pipe name, credential, and config mutation still belong to the marker-first
lifecycle transaction and are not written or activated by this prerequisite.

`BattleRuntimeCompositionCoordinator` is the dormant process-owned orchestration
proof above the pipe host. It asks for provisioned resources only when at least
one reviewed feature projection is `enabled`; unchanged family sets are a no-op.
Changing the enabled family set drains and closes the prior host, releases its
exact provisioning lease, obtains a new lease for the new typed snapshot, and
starts a new host with only the requested Battle and/or Fleet sink. Capability,
policy, or preference loss drains collection and releases the lease. Shutdown or
provisioning cleanup failure retains ownership for an explicit serialized retry
rather than forgetting an uncertain resource.

The provisioning interface is intentionally internal and has no production
registration. Marker-last cleanup now issues a typed handoff receipt only after
it has removed the exact candidates and DPAPI marker while retaining the exact
marker-bound running `runtime.lock` lease. The dormant production factory binds
that receipt to the same reviewed activation plan, committed per-feature
preferences, authoritative protected credential identity, exact running game
process receipt, and only the enabled family sinks. It opens and decrypts the
already-committed credential through its closed passive reader; it does not
accept caller-supplied plaintext credential authority.

The handoff is single-claim. A changed feature snapshot, extra family sink,
changed credential, mismatched runtime evidence, or replay fails before a host
can be constructed. Claiming atomically transfers the runtime handle; stale raw
lease references can neither mark it clean nor close it. Unclaimed or
failed-start cleanup drains the supplied sink lifetime, marks the retained
runtime receipt clean through its same locked
handle, closes it, and zeroes both owned credential buffers. Cleanup failure
retains the exact ownership for an explicit serialized retry.

This factory cannot resolve a state root, create a credential or store, discover
a process, edit TOML, acquire `runtime.lock`, decide eligibility, or register a
listener. Store creation/promotion and the Fleet runtime sink remain separate
lifecycle-owned prerequisites. Launcher startup therefore still composes none;
operational registration remains blocked on the retained signed standalone and
MSIX qualification gate.

## Credential foundation

The dormant Core now has a closed-schema credential codec and passive reader
for the lifecycle-owned `battle\ingest-credential-v1.dpapi` record. Candidate
creation uses 32 bytes from the Windows cryptographic RNG, a lowercase opaque
credential ID, a positive generation, the exact local-IPC protocol and pipe
name, canonical UTC timestamps, and a closed rotation reason. The canonical
JSON plaintext is protected with DPAPI `CurrentUser` and exact UTF-8 entropy
`STFC Mod Bridge Battle ingest credential v1`; the protected record is limited
to 16 KiB. Plaintext credential buffers are owned by disposable leases and are
zeroed on disposal.

Loading is passive: construction and an absent read create no directory or
file. A present record is opened through a no-follow locked handle, bounded
before DPAPI, parsed with exact case-sensitive properties, and rejected for
duplicate (including escaped-equivalent), unknown, missing, noncanonical, or
out-of-contract values. Results expose only bounded state codes and metadata;
they never expose the credential, arbitrary exception text, or a path through
diagnostics.

This foundation does not write the final record, set its DACL, generate or edit
TOML, acquire `runtime.lock`, create a marker, start IPC, or register the runtime
composition coordinator. The marker-first lifecycle owner must atomically
promote the protected candidate, apply and verify the current-user plus SYSTEM
ACL, bind its generation and protected-byte hash to the transaction, and hand
the resulting lease to composition. Until that owner lands, this code remains
non-operational.

## Marker-first lifecycle foundation

The dormant Core now also has the first bounded lifecycle-journal seam. A typed
root `LauncherOperationLease` can be retained across asynchronous Battle work,
so disposal of the outer operation waits for its exact retained scopes rather
than releasing the process-wide mutation lock early.

The first `active-operation-v1.dpapi` marker is written directly to its final
path with `CreateNew`, write-through, flush, and readback verification before a
runtime lock, credential, database, or TOML mutation can exist. It uses a
closed canonical schema, DPAPI `CurrentUser`, exact lifecycle entropy, and a
64-KiB protected-byte limit. The marker binds the operation owner, affected
features, before/candidate/after file identities, credential generation,
configuration source revision and hashes, and the derived feature transition.
Unknown, duplicate, noncanonical, unsafe-path, reordered, or incoherent values
fail closed.

Later stages are exact monotonic successors under the operation recovery
directory. Only one validated successor may exist, and recovery either promotes
that exact successor or removes an exact empty transaction-owned residue. Torn,
tampered, reparse, ambiguous, foreign, or structurally unknown state is
preserved as recovery-failed; recovery never guesses or recursively deletes a
directory.

The initial `battle/runtime.lock` bootstrap is likewise marker-bound. Its exact
canonical running bytes, current process ID, owner ID, length, and SHA-256 must
already be present in the prepared marker before the final path is created. The
4-KiB record is then held with `FileShare.None` for the runtime lifetime and can
transition to a flushed canonical clean record through that same handle before
release.

The next dormant seam now prepares the first exact activation candidates. It
accepts only typed, active `battle.collection` and/or `fleet.collection`
decisions and derives the two category flags independently. The generated TOML
uses only `named_pipe`, the reviewed canonical pipe name, and the new local
credential; it clears any legacy local URL and preserves unrelated TOML. If a
local target already exists, preparation requires a typed review receipt bound
to the exact source SHA-256, requested feature set, and pipe name. A bare boolean
or provider name cannot authorize the change. This first-activation seam rejects
adding a category to an already enabled shared target until that path can carry
the existing credential receipt instead of silently rotating shared authority.

The prepared marker freezes the complete credential and configuration
candidate inventory before any resource is written. The runtime lock and the
two candidates then appear only under that marker's exact ownership while the
root launcher operation lease is retained. The marker contains credential and
configuration hashes, sizes, and a hash-only mutation receipt—not the plaintext
credential. Preparation neither edits the authoritative TOML nor creates the
authoritative credential, database, or pipe listener.

Pre-commit rollback is equally narrow. It requires the exact source TOML bytes,
an inactive released runtime lock, and an inventory containing only the
marker-owned candidate paths. It opens those exact paths without following
reparse points, removes the marker last, preserves every foreign or ambiguous
entry, and returns bounded blocked/unavailable states instead of guessing. Torn
candidate bytes can be removed because their final paths were already frozen in
the first durable marker. Repeating rollback after success is a no-op.

The protected-backup handoff also remains within existing ownership. Only
`ManagedVerified`, game-closed installation evidence with complete stable
attribution may advance a first activation from `prepared` through `quiesced` to
`backup-verified`. It uses the existing provider-scoped configuration backup
store, rereads and byte-verifies the protected payload, and binds the exact
backup ID and source-content SHA-256 into the successor marker. A failure leaves
the journal at `quiesced`; an exact retry can create and bind a new verified
backup. Pre-commit rollback may retain that protected backup while removing the
still-disposable Battle candidates and marker.

The existing launcher preference store now exposes an exact Battle-only
compare-and-swap seam for the later commit stage. It checks both collection
before-values under the store's normalized-path gate, changes both values as one
atomic document replacement, and preserves search, color, launch-target, source
review, and every other preference. A stale caller performs a byte-exact no-op;
concurrent contenders cannot both commit. Malformed, duplicate-key,
unknown-field, or noncanonical existing preference documents fail closed rather
than being interpreted as defaults and overwritten. This is the existing
preference owner, not a Battle-specific file or second writer.

The first-activation commit seam is now executable but still unregistered. It
requires the same retained root operation and runtime leases, exact
`backup-verified` marker, source-path binding, protected credential candidate,
TOML candidate, and Battle preference before-values. It persists
`commit-started` before the first authoritative mutation, then promotes the
credential, applies the candidate through `AtomicTomlStore`, and changes both
Battle preferences through the existing compare-and-swap owner. The credential
is created with an exact read/write/delete-capable handle, flushed and rehashed
through that handle, assigned a protected non-inherited ACL containing only the
current user and Local System with full control, reread and verified through the
same handle, and retained against replacement until commit verification.
Immediately before `commit-started`, the coordinator also rechecks exact
`ManagedVerified`, game-closed installed-artifact evidence and rereads the
existing provider-scoped protected backup against the marker-bound source bytes.

Every authority must be in its exact marker-bound before or after state.
Foreign, malformed, or stale state is preserved and blocks before mutation.
An owned in-process failure compensates only writes made by that attempt in
reverse order and verifies the complete all-before result. If compensation
cannot prove the original state—for example, a concurrent external TOML
change—the exact external bytes are preserved, the credential handle is safely
released, and `commit-started` remains for recovery. Exact mixed states left by
a prior interruption roll forward without inventing a second writer. Only a
fully reread credential/TOML/preference set advances to `commit-verified`.

The terminal recovery seam is now executable but likewise unregistered. Under
the same retained root operation lease, it first completes only an exact
journal successor, then accepts `commit-started` only with the marker-bound
stale runtime-lock identity. It reconstructs the source solely from the existing
protected backup and reconstructs the credential/TOML candidates solely from
their exact no-follow journal paths. All-before, exact mixed, and all-after
states therefore reuse the same commit writer and converge on `commit-verified`;
missing, changed, foreign, or unbound bytes are preserved and block.

Before terminal cleanup, installed artifact evidence, protected backup,
credential, TOML, preferences, and runtime-lock identity are reread. The marker
then advances to `cleanup-pending`; only still-matching candidate files and the
stale runtime lock are removed through exact delete-capable handles, owned empty
directories are removed non-recursively, and the DPAPI marker is removed last.
Cleanup-pending inspection deliberately accepts only missing marker-owned
members, so a crash after any individual deletion re-enters idempotently while
unknown entries or changed bytes remain untouched. Success returns a typed
session-recomposition requirement rather than composing services itself.

The provider session now owns one immutable Battle feature snapshot. Consumer
and diagnostics access refreshes launcher-owned persisted preferences before
returning that snapshot, while normalized runtime capability and checked-in
product policy remain authoritative ahead of preference. Capability loss makes
retained player intent unavailable; it does not invoke the lifecycle coordinator
or create a listener.

The signed release workflow now runs a closed qualification mode after PE/MSIX
signing and package inspection and before attestation. The mode accepts no pipe
name, credential, payload, path, endpoint, or secret from its caller. It uses the
real named-pipe host, the current process's exact authorization receipt, a random
ephemeral pipe and credentials, a synthetic in-memory sink, and proves wrong-
credential rejection, exact-byte acceptance, first-instance collision failure,
bounded drain, and post-stop unreachability. It does not impersonate or claim to
qualify the future STFC producer process.

The standalone binary runs this proof directly. The workflow then refuses any
pre-existing Bridge package, installs the exact signed MSIX as a disposable
package, activates its exact package identity/AUMID at medium integrity, and
removes that exact registration. This explicit release-test mode creates no
product state, database, configuration, or network authority. Ordinary startup
cannot enter it. Because PR builds are unsigned, only a retained passing tagged-
release run can satisfy the signed evidence gate.

This is not operational activation. Neither coordinator is registered in
startup or UI, and neither opens the Battle database, registers runtime
composition, starts a named pipe, enables the dormant HTTP proof, or grants
outbound product-feature network authority. The marker-last lifecycle now
retains the reviewed runtime lease and can hand exact resources to the dormant
single-owner provisioning factory, but a retained passing signed release must
still satisfy the package gate before the pipe host can be registered.

## Dormant Fleet snapshot ownership

`FleetRuntimeSnapshotSink` now supplies the missing process-local sink boundary
for an enabled `fleet.collection` handoff. It consumes only an already
transport-validated `stfc.fleet.runtime_snapshot.v1` payload, projects the
reviewed slot fields into immutable native state, and retains no raw envelope or
payload bytes. It creates no file, database, listener, timer, worker, or network
resource.

The sink binds on first commit to the exact producer source and session. Batch
receipts are bounded to 2,048 source/session/batch identities: exact replay is a
no-op, changed bytes under the same identity fail closed, and an older
`producedAt` is accepted as stale without replacing current state. Equal-time
different state is rejected because the current producer contract supplies no
ordering sequence that could truthfully break the tie. Projection mirrors the
accepted Sidecar rules for slot identity, fleet hashes, tracked/unavailable
state, hull and exact string ship identity, and bounded active-timer evidence;
unknown source fields do not become native authority.

This is deliberately only the current-snapshot sink/read-model foundation for
Sidecar #65. It does not claim persistence, recent-combat correlation, alerts,
reminders, notification delivery, UI composition, or operational IPC
activation. Those remain separate per-feature decisions and evidence gates.

## Current composition

`battle.collection` and `fleet.collection` are now first-class, independent
feature decisions. Their readiness projection combines the immutable runtime
activation plan with launcher-owned player preference only after capability and
product-policy resolution. Diagnostics can truthfully show `unavailable`,
`available`, `disabled`, or `enabled` intent.

Even the `enabled` intent state remains operationally dormant today. It is not
a listener-activation receipt. The accepted Battle Home baseline, production
registration of the reviewed provisioning handoff, and retained signed-release
qualification evidence remain required before collection can start.

## Future external communication

If Bridge or Battle Bridge later needs an Internet service, that work requires
a separate feature contract covering endpoint ownership, authentication,
disclosed data, retention, consent, product policy, diagnostics, offline
behavior, and a fail-closed fallback. It must use outbound HTTPS and must not
turn the named-pipe broker into a remotely invokable service.
