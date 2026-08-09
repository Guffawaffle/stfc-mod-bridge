# Battle Bridge Package Topology Evidence

Status: integrated-package planning default; Battle-specific measurement gate
open

Tracking issue: [Guffawaffle/stfc-mod-sidecar#66](https://github.com/Guffawaffle/stfc-mod-sidecar/issues/66)

## Decision

The Battle Bridge v1 planning default is to extend the existing signed
`Guffawaffle.STFCModBridge` MSIX and application. Battle implementations remain
dormant until the existing feature resolver establishes eligibility and,
where a feature defines an opt-in preference, the player enables it.

A separately signed executable or executable optional package is not the v1
default. Reconsider it only if a real integrated Battle canary fails the
measurement gate below and a separate related-set spike proves a material
footprint improvement without adding player friction or weakening servicing,
signing, inventory, or rollback guarantees.

This decision does not close issue #66. The Windows packaging mechanics are
proven, but the native Battle implementation and selected SQLite provider do
not exist yet, so their exact compressed and runtime deltas cannot be measured.

## Existing evidence

The signed RC10 package provides the current baseline:

| Artifact | Measured size |
| --- | ---: |
| Signed MSIX | 80,651,559 bytes (76.92 MiB) |
| `STFCModBridge.exe` | 171,097,464 bytes |
| Bridge entry compressed within the signed MSIX | approximately 69.34 MiB |
| Bridge executable contribution in the standalone fallback ZIP | 70.11 MiB |
| `STFCModBridge.ReleaseVerifier.exe` | 18,604,440 bytes |
| Self-contained updater | 68,715,504 bytes |
| Updater contribution in the standalone fallback ZIP | 29.56 MiB |

The updater's fallback-ZIP entry is a conservative scale proxy for duplicating
an independent self-contained .NET 8 executable: it is 38.4% of the complete
MSIX's size, although those are not like-for-like compression formats. A
separate self-contained WPF executable can approach the main Bridge
executable's much larger contribution. An executable related package could in
theory share the main process/runtime through a code-loading design, but that
exact .NET 8 WPF path is unproven and conflicts with the current no-dynamic-
loading precedent. These proxies do not replace the real Battle delta.

The completed MSIX compatibility proofs already establish that:

- the actual WPF Bridge launches responsively from `WindowsApps`;
- a disposable external native-DLL handoff canary succeeds from the packaged
  process;
- launcher-owned LocalAppData remains at its nominal external path;
- a locally development-signed App Installer `1.0.0.0` to `1.0.0.1` update
  succeeds over loopback HTTP;
- `UpdateBlocksActivation` prevents an old package launch during update;
- external state survives update and package uninstall;
- hosted App Installer/MSIX byte-range and MIME behavior works;
- package removal completes;
- separate production-like RC10 inspection proves the reviewed Authenticode
  publisher and package-content integrity.

Those proofs apply to the integrated topology and do not need to be recreated
inside #66. Signed production upgrade qualification remains a later release
gate after the Battle implementation exists.

## Why one package remains the narrow precedent

The integrated topology retains:

- one immutable package identity and publisher;
- one medium-integrity full-trust application;
- one App Installer association and channel pointer;
- one version, rollback, and removal transaction;
- one signed/attested/SBOM release inventory;
- one acquisition and update experience;
- the existing package-inspection and hosting model.

An executable optional package is Windows-supported in general, but it would
create a new deployment architecture for this hand-authored, self-contained
.NET 8 WPF product. Executable related packages must remain version-aligned,
their removal can require restarting the main application, and listing an
optional package directly in App Installer installs it with the main package
rather than producing an on-demand footprint saving. True on-demand
acquisition requires an additional staged acquisition flow.

The current pipeline assumes one main package and a fixed executable set.
Optional executable delivery would widen manifest authoring, signing,
attestation, SBOM, package inspection, release inventory, immutable hosting,
update, removal, rollback, and recovery contracts before demonstrating a
benefit.

No executable code may be downloaded to writable application data as an
unverified shortcut around package servicing. The supported standalone
fallback already runs the signed Bridge from LocalAppData, so any additional
native dependency there needs an explicit controlled loading and trust
contract rather than a blanket path assumption.

## Zero-cost base-mode contract

"Zero cost" means zero Battle-owned runtime activity when no Battle feature is
active. It cannot mean zero installed bytes inside the immutable package.

A Battle-disabled launch must prove:

- no Battle database, history, or Battle-owned configuration file;
- no Battle listener, outbound connection, or bound port;
- no Battle service or factory construction;
- no Battle timer, worker, or child process;
- no loaded SQLite native module;
- no Battle-owned writable extraction outside immutable package bytes.

The current Bridge is self-contained, single-file, and uses
`IncludeNativeLibrariesForSelfExtract`. The first launch of a new executable
bundle writes five signed WPF native libraries totaling 8,214,968 bytes beneath
the per-user `.net/STFCModBridge` temporary extraction root; warm launches
reuse that bundle extraction. Embedding a SQLite native library the same way
may extract it before application composition, even when Battle is inactive.

For MSIX, the integrated implementation must prove either:

1. an integrity-protected package-adjacent SQLite native dependency loaded
   only after Battle activation; or
2. a compatible Windows-provided SQLite implementation.

Any loose native dependency must be covered by the accepted signing/trust
policy, release inventory, SBOM, and an expanded package allowlist. The
standalone fallback must either embed the dependency inside a signed
executable, place it in an installer/package-manager-protected location whose
owner and ACL the running principal cannot relax, or verify an exact
signed/hash-inventoried DLL while retaining a deny-write/delete handle or
equivalent lease from verification through `LoadLibrary` and for the loaded
module's lifetime. Otherwise it must leave the dependent Battle feature
unavailable. A user-owned "read-only" LocalAppData directory or controlled
absolute path does not close the verify-to-load race. Adding a Guffawaffle
signature to an already vendor-
signed native library may also conflict with the current single reviewed-
publisher policy and requires explicit multi-signature qualification.

## Finishing measurement spike

Before building, preregister the acceptable compressed package-delta budget,
startup and idle-memory tolerance, A/B statistic, fixed idle interval, machine
conditions, and method for producing genuine first-launch samples. This keeps
the decision threshold from moving after results are known.

Then build deterministic unsigned outputs from one commit:

1. the current Bridge baseline;
2. an integrated dormant canary containing the minimum real native Battle
   slice: ingest abstraction, selected SQLite provider, parser/core assembly,
   one lazily composed WPF surface, and activation through the real feature
   resolver.

For both outputs:

- build and package into separate clean output directories;
- record total MSIX and per-entry compressed sizes;
- measure first launch after separately versioned package installs and warm
  launch with at least five alternating A/B samples;
- record window-ready time, private bytes, working set, CPU, thread count,
  loaded modules, child processes, and listening ports after a fixed idle
  interval;
- snapshot launcher state and the exact `.net` extraction directory before
  and after disabled launches;
- use deterministic construction counters/fakes to prove that no Battle
  factory, listener, database, timer, worker, or network client starts when
  capability or preference is absent;
- enable Battle once and prove those resources appear only then.

Accept the integrated topology finally when the preregistered dormant startup,
memory, and package-delta budgets pass. The 29.56 MiB fallback-ZIP proxy is
context for setting that budget, not a post-hoc binary cutoff.

Only if the integrated canary fails that gate should a new optional-package
spike attempt exact related-set acquisition, version alignment, signing,
servicing, removal, restart, rollback, and player-recovery proof. A supported
mechanism without a material measured benefit is not sufficient reason to
change topology.

## Consequences

- #67 through #70 may design around one package identity, but final release
  qualification remains blocked on the finishing #66 measurements.
- Battle composition must remain lazy and capability-driven.
- The SQLite provider decision is now part of packaging and zero-cost
  qualification, not merely a storage implementation detail.
- Every integrated-package user downloads and possesses Battle code and its
  dependencies even when they remain dormant. Dormancy prevents runtime work,
  not shipped attack surface; parser or SQLite vulnerabilities may require an
  update for every Bridge user. The composition/lifecycle boundary must ensure
  inactive Battle code is never initialized; once active, Battle code is not
  process-isolated from base Bridge.
- The Sidecar's Electron/Node/PostgreSQL package sizes are historical donor
  evidence and are not meaningful native Battle delta estimates.
- Package topology does not decide which provider supports a feature; the
  runtime capability and feature activation contracts remain authoritative.
