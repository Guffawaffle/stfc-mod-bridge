# Portable host adapter foundation

The engine exposes `EmbeddedOwner` for an external event loop to construct,
progress and dispose an owned `LocalHost` on its original thread. The engine
has no Tauri or frontend dependency. The existing blocking runner continues
to serve the same dispatcher and retains its own lifecycle behavior.

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
It runs strict format/Clippy, engine dependency checks,
ownership compile-fail controls, frontend type checking and focused injected
adapter tests. No synthetic host enters production composition.

| Criterion | Required observation |
| --- | --- |
| BR21-FND-01 | Owned original-thread embedding, one-turn budget, reentrancy refusal and first-failure preservation. |
| BR21-FND-02 | External/inbox/validated close retires the driver while actual kernel custody and independent progression remain retained. |
| BR21-FND-03 | Panic, close error, unresolved abandonment and destructor recursion cannot manufacture safe Closed. |
| BR21-FND-04 | Typed raw adapter readiness, bounded quotas, stale-generation fencing, exact capture and conservative delivery classification. |
| BR21-FND-05 | Current native test binaries and stable source/tool inventories, frontend independence and compile-fail ownership boundaries. |

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
