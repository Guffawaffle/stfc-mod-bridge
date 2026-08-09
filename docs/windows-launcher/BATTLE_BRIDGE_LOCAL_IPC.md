# Battle Bridge local IPC boundary

Status: authenticated named-pipe transport and provisioned-runtime composition
proofs implemented but dormant; operational activation remains blocked on the
marker/credential/config transaction, producer alignment, and signed-package
qualification

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

## Current composition

`battle.collection` and `fleet.collection` are now first-class, independent
feature decisions. Their readiness projection combines the immutable runtime
activation plan with launcher-owned player preference only after capability and
product-policy resolution. Diagnostics can truthfully show `unavailable`,
`available`, `disabled`, or `enabled` intent.

Even the `enabled` intent state remains operationally dormant today. It is not
a listener-activation receipt. The accepted Battle Home baseline, local IPC
implementation, runtime lifecycle owner, producer alignment, and release
qualification must land before collection can start.

## Future external communication

If Bridge or Battle Bridge later needs an Internet service, that work requires
a separate feature contract covering endpoint ownership, authentication,
disclosed data, retention, consent, product policy, diagnostics, offline
behavior, and a fail-closed fallback. It must use outbound HTTPS and must not
turn the named-pipe broker into a remotely invokable service.
