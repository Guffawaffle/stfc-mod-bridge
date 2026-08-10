# Battle Bridge local IPC boundary

Status: authenticated named-pipe transport and provisioned-runtime composition
proofs implemented but dormant; operational activation remains blocked on the
marker/credential/config transaction and signed-package qualification

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
must be injected by the accepted lifecycle and activation owners. Real signed
MSIX and standalone medium-integrity child-process qualification remains a
release gate before composition.

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
registration. It accepts only an already-provisioned pipe name, 32-byte
credential, caller authorizer, runtime-evidence digest, exact family sinks, and
lifetime owner. It cannot resolve a state root, create or read a credential,
create a store, discover a process, edit TOML, acquire `runtime.lock`, or decide
eligibility. Supplying that lease remains the marker-first lifecycle transaction
defined by Sidecar document 27; launcher startup continues to compose none.

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

This is still not the activation commit. It does not promote the credential,
commit TOML, save feature preference, create or open the Battle database,
register runtime composition, or start the pipe. Those steps remain dormant
until the remaining marker-owned transaction stages and signed package proof
are composed.

## Current composition

`battle.collection` and `fleet.collection` are now first-class, independent
feature decisions. Their readiness projection combines the immutable runtime
activation plan with launcher-owned player preference only after capability and
product-policy resolution. Diagnostics can truthfully show `unavailable`,
`available`, `disabled`, or `enabled` intent.

Even the `enabled` intent state remains operationally dormant today. It is not
a listener-activation receipt. The accepted Battle Home baseline, local IPC
implementation, runtime lifecycle owner, marker/credential/config transaction,
and release qualification must land before collection can start.

## Future external communication

If Bridge or Battle Bridge later needs an Internet service, that work requires
a separate feature contract covering endpoint ownership, authentication,
disclosed data, retention, consent, product policy, diagnostics, offline
behavior, and a fail-closed fallback. It must use outbound HTTPS and must not
turn the named-pipe broker into a remotely invokable service.
