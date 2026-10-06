# Native consumer boundary

Bridge owns `bridge-native`, `bridge-profiles` and `bridge-toml`. These crates
consume existing producer APIs without creating another catalog, account store,
configuration writer or game updater. `bridge-profiles` uses Profiles JSON API
2 through its six-export v1 allocation ABI; `bridge-toml` uses the three-export
shared TOML ABI 1. The TOML adapter exposes all nine offline text operations.
TOML values, including int64, dates and quoted dotted keys, remain strings.

| Criterion | Implemented evidence and limit |
| --- | --- |
| BR05-01 | Exact component/host/source/module pins; physical regular-file confinement, native DLL/dylib header and export-origin checks. Controlled invalid pin/header/changed-byte probes and actual selected module export resolution. The owning backend explicitly adopts foreign code through an unsafe boundary; hashes do not establish safe code, signatures or the in-memory image. |
| BR05-02 | Private function tables retain the originating module through every native call, return allocation and data/installation lease. Allocation guards call the exact producer free function once, including refusal and malformed response paths. Public handles are neither Send nor Sync. Controlled function-table and compile-fail tests establish the ownership boundary. The current actual producer refusal stops before installation custody, after-client-drop retention and final-release assertions; those remain pending. |
| BR05-03 | Exact 32 controlled test inventory plus three actual producer tests and ten compile-fail ownership examples. Bounded UTF-8/JSON/version/error/contradictory output cases, all 24 Profiles request families and nine TOML operations. Windows proof cannot accept the two-host matrix; native Apple Silicon producer pins and execution remain required. |
| BR05-04 | Calls are synchronous on the creating thread. There is no consumer cancellation API or renderer-owned writer. Observer loss cannot release separately retained native leases. The portable local host supplies a creating-thread actor and borrowed pump; actual native owner, panic and GUI integration remain unqualified by this packet. |

Profiles' C response has no length export. The bounded NUL scan assumes trusted
producer-allocated memory remains readable through the terminator. Unit probes
use genuinely allocated memory; there is no claim that arbitrary pointers can
be validated. Requests are at most 65,536 UTF-8 bytes, paths at most 32,768,
responses at most 8 MiB and lease diagnostics at most 65,536. Native diagnostics
are suppressed into closed error classifications. TOML request/response bounds
are 64 MiB; pointer, status and length checks precede constructing a slice.

Loader fixtures pin the canonical physical directory even when TEMP uses a
junction or macOS's lexical `/var` path. A separate mandatory test creates a
private directory junction (Windows PowerShell) or symlink (macOS) and verifies
that both linked-root and linked-relative-child requests refuse before loading.
This fixture normalization does not weaken the production loader's link barrier.

Profiles data leases are shared BrowserLease directory stability, separate
from the future configuration writer's exclusion and exact document comparison.
Installation leases expose no authoritative physical/session identity getter.
The metadata-only bind handshake uses `catalog-location`. Most other catalog
queries can provision the producer's directory layout/coordination files when
absent; they are not general read-only preparation primitives.

The operation kernel's `OperationPorts`, `DurableJournal` and `ResourceLease`
native-owner bounds no longer require Send. The portable local host constructs
its kernel and owners inside a thread-local factory or a borrowed caller-thread
pump; only owned protocol frames cross its bounded command and observation
queues. Platform ports must still expose retained handles on that owning thread.
The source boundary and compile-fail examples do not qualify actual native owner
adoption, GUI service during deferred close, or custody after a native-owner
panic. See [Local host](LOCAL_HOST.md) for those explicit limits. No unsafe
Send/Sync implementation or premature lease release may be used to make the
types fit.

`dependencies/next-native-inputs.json` explicitly adopts historical Windows
producer bytes with matching source/archive/module/build-receipt digests for
consumer probes. Resolved-recipe evidence is incomplete. These pins are not
release inputs; source component-tree equality does not qualify rebuilt bytes.
No native Mac input is adopted yet.

The current Windows producer probe is not accepted: its private installation
status query returns the source-defined `RootRedirected` refusal. Controlled
consumer tests, export-origin checks and all nine actual TOML operations pass;
the Profiles fixture reaches both shared data leases and Busy archive refusal.
Positive installation-lease custody and the final actual release checks remain
unqualified. Retained failing observation:
`artifacts/next/native-ffi/db1d497b-3bd4-4355-b483-d5359552ad37/execute-profiles-producer.json`.
An exact test-process observation at
`artifacts/next/native-ffi/684f80f8-ae2c-4a9c-ab52-a9f9efa6ba28/execute-profiles-producer.json`
records PID 27632, x86_64, own package query 15700 (no package), matching neutral
native catalog-location and Rust physical shared-root paths. These facts do not
explain the producer refusal or establish another child context. No producer
protection, catalog storage or runtime target was changed.

Run the exact current-host probe through a single-item LexRunner projection:

```powershell
node scripts/next/make-runner-plan.mjs --suite native-ffi-contract --host windows-x64
lexrunner --no-emit-frames gate run --plan artifacts/next/plans/br-05.native-ffi-contract.windows-x64.plan.json --artifact-dir artifacts/next/runner/br-05-windows-probe --timeout 630000 --max-level 0 --keep-cache --json
```

The dispatcher verifies current prerequisites, source head and full source
inventories before and after. The suite compiles current exact native test
artifacts from Cargo JSON, lists their required names, executes them with zero
ignored/absent cases, retains the exact binaries/module identities and verifies
their bytes again. The explicit producer tests are ignored in routine Cargo
checks and must be selected with `--ignored --exact`; routine skipped cases are
never ABI proof. Actual tests fail if this host lacks either adopted module.

Probe receipts have `packageAcceptance: false`, `matrixAcceptance: false`,
`nativeRuntimeQualified: false` and `releaseQualified: false`. Their latest
file is `probe-<host>.json`, separate from package acceptance. Requesting the
whole br-05 matrix remains blocked until independent host aggregation exists.

The Profiles probe creates only a fresh retained Unicode catalog and a fresh
empty ASCII installation under its root-owned private fixture directory. The
producer's installation coordination lock is created under the user's actual
canonical catalog `.locks` directory even when a fixture catalog is supplied.
The exact lock is recorded and left intact after release. No shared catalog
enumeration, account import, game launch/update/configuration or cleanup occurs.
