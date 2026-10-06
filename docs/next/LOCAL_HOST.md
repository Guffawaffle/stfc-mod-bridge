# Local host source boundary

Tracking: [br-21 / #248](https://github.com/Guffawaffle/stfc-mod-bridge/issues/248).
This is a development source boundary, not br-21 package acceptance, a native
service adapter, CLI/Tauri conformance or a release candidate.

`bridge-engine::host` delivers bounded owned protocol frames to one local host.
`KernelHost` uses the existing `Engine::dispatch`, prepared/admitted operation
policy, durable replay, cancellation, snapshot and retained event cursors. It
does not introduce another writer, catalog, event sequence or recovery policy.
Production constructors do not create a synthetic host or fall back to fixtures.
The existing kernel truthfully rejects unsupported application services and
reports unavailable native observations in its snapshot.

## Construction and thread ownership

`LocalHost` intentionally has no Send or Sync bound. `spawn_local_host` accepts
a Send factory, then constructs, invokes and drops the factory's possibly
non-Send result inside its own actor thread. Captured factory inputs contain
only Send configuration/DTO values. Load modules, create Profiles/TOML clients,
resolve retained platform handles and build local owners inside the factory.
Capturing an already-created non-Send host is a compile error.

The unnecessary Send bounds are removed from `OperationPorts`, `ResourceLease`
and `DurableJournal`; clock and identity source bounds remain. No unsafe
Send/Sync implementation, native-handle transfer or Mutex around the engine is
used. The small mutex in the transport protects only an observation-fault DTO.

`owner_channel` and `run_on_current_thread` support construction on a caller's
selected owning thread. The latter privately retains the host and lends its
driver a borrowed `OwnerThreadPump`; the caller cannot extract or drop the host.
The pump is itself non-Send even when a test host happens to be Send. A driver
can run one bounded platform event-loop turn, tick the host, inspect closed
failure state or request closure. Driver RequestClose stops driver callbacks
and retains normal draining on that same thread. The background actor uses the
same run boundary. Returning Continue does not relinquish native custody.

This API does not qualify a macOS main-run-loop integration. macOS focus requires
the actual main thread and an exact local process guard. Actual GUI embedding
must prove its run loop keeps progressing during normal close and pending
native GUI work. A driver that stops servicing required GUI work at RequestClose
is not qualified for that owner. Headless unavailable shell capabilities stay
unavailable; moving a retained guard to a background task is not an alternative.

## Frames and observation

Each `OwnedFrame` owns at most the protocol's 262,144 bytes. Its Debug output
contains only a byte count. The runner decodes requests with the common strict
Rust codec; invalid UTF-8, duplicate keys and malformed envelopes return the
codec's closed rejection without invoking a service. Replies and events pass
the same codec before delivery. A result must match its exact request ID and
query/command kind. Request-specific domain correlation remains the shared
dispatcher and contract's responsibility.

Fixed source limits, with no caller override:

| Subject | Bound |
| --- | --- |
| Queued request/control/registration jobs | 16 |
| Retained pending reply/close receivers | 32 |
| Retained subscriptions | 8 |
| Queued events per subscription | 16 |
| Reply/readiness channel | 1 |
| Retained terminal observation-fault cell | 1 per subscription |

Submission uses nonblocking try-send. Oversize, queue/pending/subscriber limit
or disconnected submission failures mean not sent. After successful enqueue,
receiver timeout, loss or disposal cannot establish not sent. They never send
CancelOperation, undo admission or release the worker's lease. Thin adapters
must retain that delivery uncertainty and replay the exact original commit.

Subscription readiness acknowledges an actual owner-thread watermark. Await
readiness before the first Snapshot exchange. Events retain the kernel's exact
host, stream and sequence. Batches must start at the observer's watermark, pass
the strict contiguous event-batch codec and not extend beyond the actual owner
cursor. Foreign/expired cursors explicitly require resnapshot. Overflow has a
separate terminal fault cell, so a full event queue cannot hide its failure.
The consumer receives a resnapshot fault instead of silently skipping events.
Drop unregisters an observer without owning or affecting an operation.

The runner handles at most one queued job per automatic tick, progresses one
owner step independently of requests and then performs bounded event delivery.
Reply/event send failure never waits for an observer. `KernelHost` determines
eligible work from the actual complete operation snapshot, including work whose
acknowledgement was lost. It advances only Admitted, Running or
CancellationRequested operations in round-robin order. RecoveryRequired is not
automatically recovered, and a terminal operation does not imply session handoff.
Qualified application services can use the local kernel reference inside their
own host to perform explicit recovery or real canonical session handoff.

## Close and failed owner boundaries

Trusted host close controls and renderer protocol requests share the bounded
inbox. Trusted closure calls the kernel's RequestHostClose with its actual
current cursor, arming refusal of new admission before checking disposition.
The last request handle's disconnect requests this same closure; it does not
stop the worker. Dropping a thread JoinHandle only detaches observation.

The private runner can return and drop its host only after it has requested
closure and independently observes a valid Ready or safe RecoveryRequired.
RecoveryRequired remains that disposition in `HostExit`; it is never renamed
Ready or described as successful recovery. Deferred, persistence/close error
and unresolved session custody retain the host. Shutdown does not kill a game,
auto-recover, substitute an owner, forget a replay or release an unsafe lease.

An observer-driver panic is isolated from owner calls, recorded as DriverPanicked
and followed by requested normal closure and continued owner progression. This
does not mean the process was killed. Owner-method panic is different: it is
recorded as OwnerPanicked, permanently refuses further owner calls and safe
exit, faults observers and retains whatever remains in the host object on its
owning thread. The process can remain alive in this unavailable state.

**Catching unwind cannot prove call-local native leases or transactions survived
the panic.** Those locals may already have dropped before control reaches the
runner. Retaining the host afterwards is not full native custody proof. Native
adoption remains blocked until a reviewed no-unwind/fatal-process or retained
owner policy closes that seam. This source changes no Cargo panic profile and
does not invoke process termination as a production shortcut. A factory is a
bootstrap boundary, not permission to perform native mutation before its host
is returned; a factory refusal/panic supplies no admission or native custody.

The focused fixture explicitly observes a dropped call-local guard while the
panicked owner's process and retained journal are still alive. It then kills
only its exact owned test child, opens the retained journal in a fresh host,
reports interrupted recovery, performs explicit synthetic rollback and replays
the original operation. This distinguishes observer panic, owner panic and
actual process death without claiming an in-process worker survived death.

## Development checks and unbound services

Use the repo's scoped pinned Cargo route, locked and offline:

```powershell
node scripts/next/cargo.mjs fmt -p bridge-engine -- --check
node scripts/next/cargo.mjs clippy --locked --offline -p bridge-engine --all-targets -- -D warnings
node scripts/next/cargo.mjs test --locked --offline -p bridge-engine --test host_transport -- --test-threads=1
node scripts/next/cargo.mjs test --locked --offline -p bridge-engine --doc host:: -- --test-threads=1
```

The source inventory covers actual actor-thread construction/call/drop of
non-Send host, lease and journal; caller-thread draining; strict frame refusal;
queue and receiver/subscriber limits; lost-response replay; disconnect/close
custody; explicit overflow/foreign/expired watermark refusal; reply ID/kind and
future-event refusal; unavailable application observations; safe recovery exit;
observer panic; owned child panic/kill/restart recovery; and compile-fail owner
transfer examples. Test helpers and services live only in the test target. The
child helper refuses caller routing overrides and uses only a direct fresh
fixture under ignored `artifacts/next/preparation/local-host-fixtures`.

Root owns module registration, a candidate-bound source gate and any eventual
dispatcher/CLI/Tauri composition. These source checks do not supply formal
LexRunner br-21 acceptance. The process fixture uses an actual filesystem
journal and killed test process with synthetic canonical owners; it qualifies
neither an installed native application nor canonical game/catalog exclusion.

Still unavailable: production private journal provisioning/permissions/physical
ancestry; entropy/clock bootstrap; canonical read-only inventory; complete shared
application snapshots and draft/support event publication; actual configuration
DocumentOwner/SchemaSource/SensitiveEntry and OperationPorts adoption; qualified
session/game/runtime/update/support/application owner adapters; persistent CLI;
Tauri command/event adapter and subscription/disposal integration; narrow native
capability permissions; production mock exclusion proof; macOS run-loop and
native producer adoption; native panic policy; and independent Windows/macOS
shell, runtime, assistive technology, packaging and release qualification.
