# Windows native test execution context

On 2026-10-03, actual Profiles consumer and freshly rebuilt producer tests
refused an installation lifecycle lock with `RootRedirected`. Controlled ABI
tests passed. The consumer failed before acquiring the installation lease, so
its after-client-drop custody assertions did not run.

A separately compiled SDK-only metadata helper compared its inherited Codex
launch context with a child dispatched through the current desktop Explorer.
It read process generation, package/token metadata, known-folder results and
attributes/physical identity of the existing neutral Profiles root and one
source-derived synthetic test lock. It did not load a producer, enumerate a
catalog, acquire an exclusion, read configuration or touch a game process.

| Observation | Inherited child | Desktop-dispatched child |
| --- | --- | --- |
| `GetCurrentPackageFullName` | 15700 / no package | 15700 / no package |
| Ordinary and no-package-redirection LocalAppData | Same requested user LocalAppData | Same requested user LocalAppData |
| Existing Profiles root final path | Matches requested path | Matches requested path |
| Existing Profiles root FILE_ID | Differs from desktop observation | Differs from inherited observation |
| Exact existing synthetic lock | Final handle path inside Codex package LocalCache; differs from request | Missing, error 2 |

The observed lock redirection is concrete. No-package classification and
lexical root agreement did not establish an unredirected child context.
Root path agreement also did not establish equal physical root identity.
The desktop observation does not prove that every native operation from that
context will be unredirected, nor does it qualify a producer or package.

The synthetic requested installation key was
`55b7007b20c092f486862804adfdc91bdfc5760083754dc79c16d956ac4af10f`.
It came from the failed producer fixture's executable-directory derivation,
not an owner-returned installation identity. The helper opened only that
already existing `.locks/install-<key>.lock` with `OPEN_EXISTING`, read
attributes and shared read/write/delete. It created neither root nor lock.

The retained comparison is
`artifacts/next/preparation/exec-in-explorer/fc20ac25-93a8-4c51-8432-0760f1094e91/comparison-b5e24fde-abd6-494a-88e5-85f86f696fa7.json`,
SHA256 `d7d481b601ab28407330356e20a7e59d3d2130b5d719f6e8be8142b67acb47cb`.
Its build receipt SHA256 is
`4175c9d22b6cc0a33636ee1e2e441972fbe1fbbefb29a8e9ade24782ceecc78d`;
the executed x64 helper SHA256 is
`ba242ab1463998659db522ea228a2530475e3a48266aef49f9c12d88f74e3357`.
Root independently checked 317 source/tool/header/archive/binary records,
the reviewed C++ delta and the exact eight Windows system imports. Four
closed-grammar/root/digest refusal probes passed before desktop dispatch.

Broker PID 16152 and child PID 20588 are historical observations with full
creation FILETIME recorded in the receipt. Explorer dispatch acknowledgment
is separate from the child's receipt; no external live child handle was
retained. An executable's disk digest does not identify its mapped image.

## Fixed desktop native attempt and harness defect

The reviewed SDK launcher then dispatched the fixed command
`node scripts/next/native-ffi.mjs` with unchanged inherited desktop environment,
the canonical Bridge cwd and retained SDK-child/Node-worker/Node-suite handles.
The packet was `artifacts/next/preparation/desktop-native-probe/9aecac61-4c8c-43dc-ae28-611bf671c455/`,
nonce `9cc18a85-83f0-4356-a5a4-8bd54e2038e1`. Exact helper digest was
`1a81ff3e654b1d33afd26d254a180127826be7180ed533b7df8a4e646c84ac2c`;
Node digest was `58e74bf02fc5bbacc41dcb8bef089961cd5bddd37830b87784e4fc624d145d1f`.
Worker PID 56012 had creation FILETIME 134355352856036953; suite PID 23852 had
creation FILETIME 134355352858921704. These are historical session-1 x64 facts.
The broker, child, worker and suite terminated with observed exit 1; no process
was stopped to make the diagnostic finish.

The suite created `artifacts/next/native-ffi/b826f2fb-a03b-4aca-9342-25b802fe3878/`.
Its exact retained records show 32 controlled tests, ten compile-fail docs and
all three actual producer tests passing. In particular, the Profiles case
completed installation acquire/drop/release custody. Profiles' neutral-root
and physical-path protections were unchanged. These partial test results do
not establish complete gate acceptance: the harness subsequently failed at
its final physical module path comparison and produced no `native-ffi.json`.

The failure was `EISDIR`, syscall `lstat`, path `D:`, at the hash-matched
`native-ffi.mjs:138` call to JS `realpathSync(observed.physicalPath)`. It was not
the earlier Cargo manifest comparison. The producer paths used the Windows
extended namespace. A separate read-only investigation reproduced the exact
failure from ordinary inherited Node, while `realpathSync.native` resolved
the same full module paths correctly. Standard Cargo paths succeeded with
both APIs. The investigation retained seventeen path observations, exact
embedded Node sources and before/after input hashes at
`artifacts/next/review/node-realpath/bd02670b-5534-43df-935c-126347aac538/`;
characterization SHA256 is
`7bebad6d8dbbfcb57b1b7f15b9b696d5ff44e0d87980e597213bcad41f1f2839`.

This is a Bridge qualification-harness compatibility defect. No LexRunner or
AXF defect follows from it. The correction uses native filesystem resolution
only for that observed physical path comparison, preserving expected owner,
component/host, source provenance, digest and all explicit artifact ancestry
and junction refusals. [The pinned Node source](https://github.com/nodejs/node/blob/v24.14.1/lib/fs.js)
separates its JS path walker from the native binding. A focused regression
checks equivalent namespace spelling, a different file and missing paths.
Current qualification requires a new source-fenced attempt; the failed packet
and partial records remain unchanged.

Preserve the inherited-context `RootRedirected` failure. A desktop-dispatched
test result does not qualify every operation in that context, another caller,
Apple Silicon, a game installation or a release. Never weaken neutral-root
protection, infer authority from no-package classification, or select another
catalog to obtain a passing test.

## Complete corrected Windows consumer observation

The fresh packet `db553b9e-3ba7-49cb-b925-3b08f4a4b355` changed only its
immutable root and exact input/worker digests in the SDK dispatch code.
Independent read/hash review reconciled 356 bound records; root then checked
them and the four closed-grammar/root/digest refusals before dispatch. The
compiled helper digest was
`523eade5c0f65ebb83d07f741eb836903c7b7647db564881cf3616ef25aeb726`.
Nonce `7dc97b2e-15f3-4742-a414-ca584d5a5f47` completed with all four processes
observed at exit 0, unchanged source/preparation fences, and complete bounded
stdout/stderr. Worker PID 33412 had creation FILETIME 134355374030875213;
suite PID 51220 had creation FILETIME 134355374034199138, session 1, native x64.

The complete consumer receipt is
`artifacts/next/native-ffi/64d41a00-2569-45f0-a4da-1fe9c7b1925a/native-ffi.json`,
SHA256 `ad9624baee419c4f7b8e3db445c8533d97119006d673de0a4d193cc0de6ea6a9`.
It records 32 controlled cases, three actual producer cases, ten compile-fail
ownership examples and four evidence regressions. The SDK worker receipt
SHA256 is `3fb817ee722c5b2d2f0cea5d822027774adc5792a9cb419864dc1a2f38b0272f`.
This closes the namespaced-path harness defect for that exact Windows consumer
invocation and historical producer inputs. It is direct diagnostic evidence,
not a LexRunner application, two-host package acceptance, recipe qualification,
platform-service proof, installed-game smoke, signing or release qualification.
