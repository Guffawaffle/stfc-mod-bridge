# Portable host adapter foundation

The engine exposes `EmbeddedOwner` for an external event loop to construct,
progress and dispose an owned `LocalHost` on its original thread. The engine
has no Tauri or frontend dependency. The existing blocking runner continues
to serve the same dispatcher and retains its own lifecycle behavior.

The controlled transport also carries a nonclone, non-Debug, non-Serialize and
non-Send operation custody token through the actual original-thread kernel.
Abandoned replies/subscriptions and deferred close retain it until the owner
settles work. Temporal observations require token destruction while the lease
and provider context remain alive, on that same owner thread. This is portable
component evidence, without production native writer or caller qualification.

An embedded turn consumes at most one inbox request, one owner progression
and bounded event delivery. The pump reserves its budget before callbacks;
repeated ticks do no extra work. A driver that does not tick receives one
fallback tick. The caller services its OS loop between turns. This count does
not bound a synchronous native call or establish real GUI responsiveness.

Only an owned `'static` host may enter the retained shell. Temporary setup
configuration may be borrowed by the factory. The local marker prevents
transfer and sharing even when the host itself is Send. No API extracts the
host or lets a borrowed pump escape its callback.

Close intent is latched without calling the owner during a recursive callback.
Each turn observes it before the inbox, after dispatch and before further
progression. Observed close retires later driver callbacks; independent owner
progression remains active during Deferred draining. A recoverable driver
error requests close while preserving the first failure. Owner or outer
driver panic permanently taints the shell, refusing further owner calls and
Closed publication. A caught unwind cannot preserve call-local native guards.

Closed requires a fresh validated Ready or safe RecoveryRequired disposition,
the actual owner cursor and successful original-thread destruction. Destruction
panic preserves the first failure and observed closing state and denies Closed;
it does not prove that a partly destroyed owner survived. Dropping an unresolved portable shell
leaks its owned runtime and abandons servicing. Production composition must
instead enforce an aborting panic policy before native construction, keep the
OS loop servicing Deferred work, veto close/exit synchronously, and treat loss
of its guarded owner as forced process death requiring journal recovery.

The frontend's `TauriTransport` accepts four fixed commands: exchange,
subscribe, poll and unsubscribe. It shares the existing raw wire validators,
typed client and observation reconciliation. Root composition must issue one
trusted epoch and monotonic registration allocator per document lifetime.
Readiness waits for an exact native ACK after actual engine subscription
readiness; registering a task or JavaScript callback cannot supply that ACK.

The adapter permits eight local subscribers, 32 pending request reservations,
8MiB aggregate raw requests and one unresolved poll across its lifetime. One
poll returns zero or one scalar raw frame, bounded to 256KiB UTF-8. Retired
generation callbacks cannot revive observation. Sent invocation reservations
remain held until the real SDK promise settles, even after local abort or
disposal. Only a closed validated native refusal DTO can assert `not_sent`
after invocation begins; other failures remain uncertain. Exchange is never
automatically retried. Cleanup uses the known registration key with at most
two unsubscribe attempts. Native high-water retirement, expiry and caller
authorization remain separate production requirements.

## Portable registration registry

`bridge-host-adapter` implements the native-side observation state without a
Tauri, webview or platform dependency. It holds only engine subscription
receivers, DTOs and bounded synchronization state. Root creates one registry
for one trusted document epoch; a registration key is never caller authority.
The shared engine owner, services and journal remain on their original thread.

Eight Preparing/Ready/Closing slots remain reserved until actual readiness
work, receiver disposal and any poll lease finish. Canonical nonzero u64 keys
advance a permanent high-water mark even when capacity refuses admission or
unsubscribe arrives first. Existing earlier live keys remain addressable.
Preparing has five seconds from admission. Only a valid admitted poll renews
the 30-second Ready deadline; root must service expiry independently of the
renderer and revoke the registry on document or timer/worker failure.

Readiness waits on an actual engine watermark ACK in a bounded off-main task.
The registry creates no task or thread. Unsubscribe during that wait cannot
release its slot or return a Closed ACK before actual work/receiver disposal.
Two close waiters per key and sixteen overall retain quota until token drop.
Mutex poison permanently refuses admission and never fabricates cleanup.

One global poll lease holds its reservation through one receiver read, bounded
JSON serialization and raw response construction, including retirement. It
returns zero or one exact UTF-8 frame. Early response loss retires the stream;
root explicitly calls `abandon_response` if response construction fails after
serialization. Successful serialization does not attest browser delivery or
SDK promise settlement. Observation expiry/revocation never cancels admitted
work or closes the engine owner.

`confirm_ready_ack` is a point-in-time check. Native root composition must
coordinate expiry, unsubscribe and document revocation with final caller/key
authorization and response publication under one short admission boundary.
It must not await readiness, perform native work or block on cleanup inside
that boundary. These platform scheduling/publication requirements are not
implemented or qualified by this portable crate.

The suite requires 31 registry cases in a separately selected current library
test artifact, alongside the 43 engine host/kernel cases. The controls use
actual engine channels and original-thread embedded turns with a test-only
host. Bounded worker tests exercise revocation/unsubscribe before the engine
ACK and after it is consumed but before registry installation, response
abandonment, permanent mutex poison and real cleanup notification. They do
not establish native executor, caller, timer or IPC behavior.

## Portable transport controller

`TransportController` binds one already-created engine handle, one trusted
document epoch and one trusted clock. CLI and Tauri composition can use the
same controller without adding either frontend to the engine or adapter. It
creates no local host, native service, worker, thread or timer.

Exchange admission reserves one of 32 jobs and at most 8MiB of original request
bytes before enqueueing a frame of at most 256KiB. A delivery ticket observes
the job; dropping it does not release an unresolved engine reply or its quota.
Root must service replies independently of the requester. Each bounded service
pass checks each retained reply once. Ready responses remain within the job
reservation through response construction or explicit abandonment. Only a
known refusal before enqueue claims `not_sent`; admitted failures remain
uncertain. Raw bytes pass through unchanged and exchange has no retry path.
The caller has already allocated the input vector, so native framing and
framework parsing need their own bounds before allocation.

Subscription and cleanup work retain the registry's real scheduling tokens,
five-second preparation deadline, engine readiness ACK and bounded close
waiters. A poll retains its single lease through one read, bounded JSON
serialization, synchronous raw response construction and a final commit
callback. Builder or commit failure, unwind or abandonment retires the stream
instead of silently losing a consumed event. The controller holds no mutex
across caller response callbacks.

Final readiness and poll checks are point-in-time observations. Production
root must coordinate caller authorization, document loss, expiry, unsubscribe
and final response publication under one short admission boundary. A callback
return alone does not establish a native framework's publication point. Root
also supplies independent expiry and reply service, bounded work scheduling,
and original-thread owner retention through Deferred close. Stopping new
submission preserves existing reply and cleanup work; dropping the controller
does not prove safe host close.

The separately selected controller integration artifact adds 11 controls over
actual engine channels and a test-only owned original-thread host. Its
controlled response barrier tests its explicit callback boundary. These
controls do not qualify a native executor, caller identity, timer, platform
publication or production service factory.

## Qualification boundary

```powershell
node scripts/next/make-runner-plan.mjs --suite host-adapter-foundation --host windows-x64
```

Apple Silicon uses `--host macos-arm64-native` on that actual native host. Run
the resulting plan through LexRunner. This is a single-host suite observation;
the dispatcher refuses full br-21 package acceptance. Exact current prerequisite
packages br-03, br-04 and br-13 remain mandatory. The suite retains native Cargo
artifact selection, executable architecture and before/after hashes, complete
fixed test inventories, command logs, pinned tool payload observations and
source inventories. It refuses caller formatter, nested Cargo and fmt/Clippy
alias overrides before tool discovery. Format and Clippy invoke the observed
physical subcommand payloads directly, bypassing configured alias dispatch;
their nested Cargo calls and formatter use the observed toolchain payloads.
PATH discovery accepts a symlink only when its resolved payload is a regular
file and retains the original invocation route for Rustup's shim dispatch.
The physical tool payload is still inventoried and hashed before/after checks.
The source inventory includes the engine's transitive `bridge-toml` and
`bridge-native` roots. Older observations whose suite inventory omitted a
compiled transitive root retain that narrower scope; a passing host job does
not retrospectively complete its source closure.
The driver retains byte-identical copies of its four selected test executables
inside the uploaded observation folder and rechecks them before completion.
Those copies are evidence payloads; only the original current Cargo artifacts
are executed. Older archives without those payloads permit log/hash readback
but cannot supply independent rehashing of the tested bytes after job completion.
It runs strict format/Clippy, engine and adapter dependency checks,
ownership compile-fail controls, frontend type checking and focused injected
adapter tests. No synthetic host enters production composition.

| Criterion | Required observation |
| --- | --- |
| BR21-FND-01 | Owned original-thread embedding, one-turn budget, reentrancy refusal and first-failure preservation. |
| BR21-FND-02 | External/inbox/validated close retires the driver while actual kernel custody and independent progression remain retained. |
| BR21-FND-03 | Panic, close error, unresolved abandonment and destructor recursion cannot manufacture safe Closed. |
| BR21-FND-04 | Typed raw adapter readiness, bounded quotas, stale-generation fencing, exact capture and conservative delivery classification. |
| BR21-FND-05 | Current native test binaries and stable source/tool inventories, frontend independence and compile-fail ownership boundaries. |
| BR21-FND-06 | Real engine ACK, bounded registration/poll/cleanup custody, monotonic retirement and permanent poison refusal in the portable native-side registry. |
| BR21-FND-07 | Bounded controller admission and retained exchange/response work, exact raw replies, actual registry tokens and lease custody through controlled response construction and commit. |

Real CLI application workflows, native Tauri command/caller/document/ACL
composition, production journal and service provisioning, Windows WebView2
and Apple Silicon WKWebView behavior, installed STFC and release qualification
remain outside this foundation observation. Native GUI conformance and headless
workflow suites continue to block full br-21 acceptance.

The versioned-close test includes an actual stale Snapshot cursor rejection.
Its controlled host pauses only fixture progression while obtaining and using
a fresh real cursor, then resumes Deferred draining. It does not establish that
a renderer can always win an active cursor race. Production close needs the
owner-native close signal or a separately reviewed resnapshot policy. The
safe-recovery and terminal-journal failure controls exercise the real kernel
with in-memory ports; they do not qualify protected persistent storage.
