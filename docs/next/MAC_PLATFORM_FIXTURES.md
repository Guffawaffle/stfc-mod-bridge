# Apple Silicon platform fixture subset

`macos-platform-fixtures` is a limited native observation suite for Bridge issue
244 (`br-07`). Its entry point is `scripts/next/macos-platform-fixtures.mjs`.
It supplies **40 default controlled tests, 17 individually selected native tests,
and 7 native compile-fail documentation blocks**. The full `macos-platform` gate
and package acceptance remain separate requirements.

The suite refuses public arguments, foreign hosts, Intel/Rosetta execution,
noncanonical checkout/cwd, injected fixture/helper/Keychain selectors and Node
loader/preload overrides. Initially it runs only on an actual `darwin/arm64`
GitHub-hosted ephemeral job with its checkout SHA, run, attempt and job metadata.
Those environment observations route a disposable fixture; they authenticate no
account or job signer. Manual execution needs a separately reviewed disposable
user route. There is no home/temp/helper fallback or automatic tool installation.

Root registers the suite with `host: macos-arm64-native` and
`packageAcceptanceAvailable: false`, while retaining the unimplemented full
gate. The outer qualifier must validate current `br-02` prerequisites and their
`br-01`/`br-00` chain before and after execution. The intended CI route follows
its same-job successful `br-02` gate:

```text
node scripts/next/make-runner-plan.mjs --suite macos-platform-fixtures --host macos-arm64-native
lexrunner --no-emit-frames gate run --plan artifacts/next/plans/br-07.macos-platform-fixtures.macos-arm64-native.plan.json --artifact-dir artifacts/next/runner/br-07-fixtures --timeout 630000 --max-level 0 --keep-cache --json
```

This is a Mac-only step in the existing foundation job. No receipt from another
job/run is a substitute. A PR job observes its checked-out merge SHA; a push job
observes its actual push checkout SHA.

## Selection and source/tool custody

The script fingerprints the complete reviewed input inventory before commands
and at completion, including the graph/dispatcher controls, this document,
foundation workflow, scripts, lockfiles and the Mac/domain/contracts sources.
The source head must equal the job checkout SHA throughout the invocation.

Node is exactly 24.14.1 and the tracked Rust pin is exactly 1.99.0. `rustContext`
selects the native `aarch64-apple-darwin` target and rejects compiler wrappers.
The suite resolves and hashes Rustup, its concrete Cargo/rustc/rustdoc routes,
the underlying pinned toolchain and fmt/clippy tools, the actual Node/script,
Git, and each fixed system tool. Compiler `-vV` must report the exact release and
native host. Direct underlying tool executables receive no `+pin` argument;
their context still sets the pinned toolchain, target and inspected compiler
paths. All recorded tool and executable bytes are checked again before success.

Rustup is a multicall executable. A Homebrew `rustup` alias may physically resolve
to `rustup-init`, whose default dispatch is the installer. The seven pinned
`rustup which` queries execute the inspected physical binary with the fixed
internal `argv0: rustup`; each check records both values. No other command gets
an alternate dispatch name. Inherited nonempty `RUSTUP_FORCE_ARG0`, including
whitespace, refuses admission; that variable is also removed from child context.
Physical path, byte and identity fences remain unchanged. This dispatch follows
[Rustup 1.29's command selection](https://github.com/rust-lang/rustup/blob/1.29.0/src/process.rs#L43-L53).

Importing the script does not execute its entry point. Its exported bounded
admission, Mach-O, Cargo artifact, process-row/no-match, ACL and volume parsers let
shared tests check refusals on Windows. Synthetic parser results supply no
native execution or qualification evidence.

Each current Cargo invocation must report one exact `compiler-artifact` with
the owning manifest, source path, target name/kind, correct `profile.test` and
one executable inside the native target route. The three libtest binaries are
the library, `tests/format.rs` and `tests/native.rs`. The owned helper is built
as `--example native_fixture_child`, with `profile.test: false`. No executable
is selected by glob or stdout receipt path. Current cached artifacts may be
used only when the invocation itself reports them and all fences remain valid.

Executable observations require a complete thin 64-bit little-endian arm64
Mach-O executable and a bounded valid load-command table. Universal, Intel,
wrong-type and incomplete artifacts refuse this narrow protocol.

The three current libtest executables also receive exclusively created copies
under the retained private fixture directory. Bounded descriptor reads and
writes bind each copy's bytes to the current compiler artifact; its complete
Mach-O, length, digest and private identity are observed and checked again
before success. The original executable remains the execution route. Its
retained copy has the same bytes, and is uploaded with the fixture receipts.

## Private filesystem fixture

One exclusively created UUID leaf under owned
`artifacts/next/macos-platform-fixtures` has exact mode0700 and a retained
directory descriptor/dev/ino. Real UID must be nonzero and equal effective UID
and owner UID. Existing ancestry may contain no link/junction. Creation uses
checked existing parents, with no recursive creation through unchecked paths.

Only that fresh leaf receives `/bin/chmod -N`. Bounded `/bin/ls -lde` output and
descriptor checks must then observe absent extended ACL and exact mode0700;
unknown ACL output refuses. The same check covers the helper copy. Xattr-marker
presence does not itself mean an ACL exists. [Apple chmod contract](https://raw.githubusercontent.com/apple-oss-distributions/file_cmds/main/chmod/chmod.1).

The helper copy is created exclusively with mode0700, one link and the current
UID. Bounded reads, source/copy hashes and a retained read/write descriptor bind
the bytes copied from the current example artifact. Native tests independently
check its physical containment and digest. No helper entropy or secret data is
logged.

The inspected `/bin/df` runs with fixed `-P -k -I -Y` and this exact private root
as its only operand. Apple `df` calls `statfs` on that directory and reports its
filesystem type, mounted device and mountpoint; APFS firmlinks may resolve a
directory to the Data volume even when its path starts below `/`. There is no
root-volume fallback or all-volume query. Bounded output must contain one exact
C-locale header and one APFS device/mountpoint row, with inode columns suppressed,
1024-byte units, no stderr and a successfully closed command. Unknown output
fails. Inherited nonempty `LIBXO_OPTIONS`, including whitespace, refuses
admission; that output-control variable is scrubbed from child context.
[Apple df source](https://github.com/apple-oss-distributions/file_cmds/blob/659a8a301e2acf0343f8b8673a154a2ca4d07084/df/df.c#L256-L298),
[Apple df options and directory example](https://github.com/apple-oss-distributions/file_cmds/blob/659a8a301e2acf0343f8b8673a154a2ca4d07084/df/df.1#L273-L279).

The reported mountpoint must be a physical directory with a retained descriptor;
its path and descriptor device/inode must agree, and its device must equal the
private root's retained device. Root custody is checked around selection. The
exact-directory `df` observation and both directory fences repeat before success;
device/mountpoint changes refuse. These are repeated observations, not an atomic
mount attestation or a namespace exclusion. The five system tools are `chmod`,
`ls`, `ps`, `sh` and `df`; together with Node/script, Git/Rustup and pinned Rust
routes the suite seals 19 tools before and after success. Raw volume output is
retained only as bounded hashes; selected filesystem/device/mountpoint and
descriptor identities are explicit fixture diagnostics.

A private `CaseProbe`/`caseprobe` lookup
records actual case behavior and identity. This covers one selected volume and
case mode. No volume/image creation or mounting is authorized.

The fresh tree remains as evidence for ephemeral VM disposal. The script does
not recursively clean files, user homes, Keychains, game stores or installations.
These physical observations are not a production namespace exclusion.

## Closed test inventories

The format binary has exactly the sixteen names in `macFormatNames`. Its four
Keychain-purpose/reference cases parse synthetic identifiers and access no
Keychain. Execution requires 16 passed and zero ignored/filtered cases.

The native library discovery has nine controlled provider names in
`macControlledNames`, fifteen controlled journal-policy names in
`macJournalPolicyNames`, two ignored names in `macProviderNames` and five ignored
journal cases in `macJournalNativeNames`. The foreign host refusal test must be
absent. Default library execution explicitly skips all seven native cases:
24 passed, 0 ignored, 7 filtered. Each native library case then runs separately
with `--ignored --exact --test-threads=1`: 1 passed, 30 filtered.

The two selected provider names are:

- `providers::tests::native_continuous_milliseconds_are_readable_and_advance`
- `providers::tests::native_secure_random_returns_an_owned_sample_without_logging_it`

The clock observes advancement over 5ms, without suspend or UTC proof. The RNG
case observes actual API success and owned-sample wiping without logging random
bytes or asserting statistical entropy.

The five retained-journal cases use private test-only construction beneath the
internally supplied `BRIDGE_MACOS_NATIVE_FIXTURE_ROOT`. Each creates its own
random nonce namespace and retains its tree. They cover roundtrip/reopen and
process-local reservation, mode-change refusal without repair, multiple-link
refusal without unlinking, replacement by a directory containing the same
bytes, and retry after faults injected after each completed constructor flush
stage. The last case does not inject a kernel fsync error or power loss. The
cases spawn no process and establish no cross-process exclusion or crash
recovery. They bypass production home/Library discovery and cannot qualify the
zero-argument production namespace route.

Production source uses native `getpwuid_r` home discovery and retained
root-to-home descriptors, allowing independently observed system-volume
transitions before the captured local APFS home. From home through the private
leaf it requires that captured device. It refuses root/set-ID/thread credential
overrides, links, Finder aliases, unsafe ownership/modes, unknown observations
and extended private ACLs; existing OS ancestry permits only restrictive DENY
ACL entries. Existing permissions are never repaired. A restrictive creation
umask can cause refusal. Lifetime BSD flock is cooperative; it cannot prevent
malicious same-user namespace or byte changes. These are implementation
constraints awaiting native qualification, not observations of this machine.

The native integration inventory contains exactly eleven ignored cases. Ten
are selected separately, each requiring 1 passed and 10 filtered:

- `native_descriptor_identity_and_parent_replacement_refusal`
- `native_symlink_ancestor_and_finder_alias_are_refused`
- `native_case_semantics_follow_the_captured_volume`
- `native_staged_exchange_retains_backup_and_explicit_permissions`
- `native_exchange_refuses_stale_and_hard_link_destinations_before_call`
- `native_exact_process_observes_architecture_and_rejects_forged_generation`
- `native_owned_child_exit_is_not_a_reusable_pid_binding`
- `native_bundle_executable_requires_descriptor_ancestry`
- `native_unsigned_signature_cannot_grant_os_trust_or_publisher_policy`
- `native_non_gui_focus_and_unavailable_shell_features_are_explicit`

`native_keychain_roundtrip_wrong_purpose_and_exact_delete` is discovered and
excluded. The suite never sets `BRIDGE_MACOS_KEYCHAIN_FIXTURE` and never uses
blanket `--include-ignored`. Data Protection Keychain needs a separately reviewed
logged-in user and main-executable signing/entitlement setup; an ephemeral file
Keychain is not a substitute. [Apple TN3137](https://developer.apple.com/documentation/technotes/tn3137-on-mac-keychains).

The bundle case constructs private CFBundle metadata and never launches an app.
Unsigned signature observations grant no publisher policy or known-signer
corpus qualification. The focus case runs on a normal libtest worker with one
test thread; it observes refusal/unavailable or NoWindow/Denied, without GUI
focus transfer or consent/menu integration.

Exactly seven actual rustdoc compile-fail rows must bind the current block/item
identity: `filesystem::RetainedDirectory`, `filesystem::ReadOnlyFile`,
`filesystem::StagedReplacement`, `process::ExactProcessGuard` and
`secrets::Plaintext`. Displayed line numbers must fall within the matching
current source block. Those five docs reject a combined `Send + Sync` transfer
bound. The journal owner adds separate blocks rejecting `Send` and `Sync`, each
bound to its own source block even though both report the same item.
Zero/ignored/unknown doctest rows
fail, and `cargo test --all-targets` is not documentation execution.

## Owned process disposal and fixed start barrier

The helper accepts only `bridge-native-child-v1` and a 32-lowercase-hex token.
Stdin byte `q` exits0; invalid arguments exit64, EOF/other input exits65. Its
source performs no path/account/catalog/game/network/Keychain operation. The
token is a protocol value, not an authorization secret.

The existing Rust owned-child test lacks a panic kill/reap guard. To contain a
failed test, the parent forks this same inspected Node/script as an internal IPC
supervisor with `detached: true`, empty `execArgv`, sanitized env/cwd and a fresh
custody nonce. The role accepts only three fixed disposal self-checks, three
pre-anchor cancellation self-checks, or that one exact source-bound native case.
There is no public executable, command, PID
or arbitrary cleanup API. The supervisor installs deadline, disconnect, signal
and failure handlers before launch, and performs asynchronous custody work.

Cancellation permanently closes the admission latch. Every awaited setup and
child continuation checks it again, including immediately before anchor,
deadline replacement, launch and stopped-child continuation. Cancellation
cannot resume initialization after a successful delayed validation; the setup
directory descriptor closes in its initializer's finally path. Foreign-host
tests hold and release a validation promise to check this terminal policy; they
do not observe native signal or IPC delivery.

The native pre-anchor self-checks continue into the same guarded startup path
after their held validation. Their expected cancellation must stop that
continuation; an unexpected anchor, child admission or exit fails the parent's
exact event inventory.

The fixed start barrier extends the initial packet and requires independent
review:

```text
/bin/sh -c 'kill -STOP "$$"; exec "$@"' bridge-macos-fixture-start-v1 <bound executable> <closed arguments>
```

The shell bytes and program digest are retained. Caller shell startup/function
and loader injection is refused/sanitized. The supervisor asynchronously observes
the retained stopped child PID and its exact inherited PGID while its own anchor
is alive, then sends SIGCONT only through that retained ChildProcess. The fixed
`exec` replaces that same shell with the bound Mach-O helper or native case;
unknown stopped state, failed continuation or early exit refuses. This avoids
requiring a fast completed Rust test to remain alive for a later PGID query.
[Apple shell PID and exec contract](https://raw.githubusercontent.com/apple-oss-distributions/bash/bash-138/bash-3.2/doc/bash.1).

Node's detached POSIX child becomes a new process-group/session leader. The
live supervisor verifies itself and inherited children using only fixed numeric
`ps` columns. Normal result, fixed failure, deadline or parent IPC disconnect
dispatch SIGKILL to the supervisor's own negative PID/group while it remains
the anchor. A bounded stdout disposal-intent record stays observable after IPC
disconnect. Parent disposal requests travel by IPC; no late numeric group kill
is attempted after anchor loss. Diagnostic writes have a fixed 250 ms bound before
group disposal; missing markers fail proof without suppressing the kill attempt.
[Pinned Node lifecycle](https://raw.githubusercontent.com/nodejs/node/v24.14.1/doc/api/child_process.md),
[Apple group signal contract](https://developer.apple.com/library/archive/documentation/System/Conceptual/ManPages_iPhoneOS/man2/kill.2.html).

Three separate groups first run actual helper-alive self-checks for fixed
failure, supervisor timeout and parent IPC disconnect. The helper's stdin stays
open before each trigger. These are harness observations, not extra platform
test names. A result remains provisional until the retained supervisor reports
the expected disposal intent, closes via SIGKILL, and bounded `ps` observations
show group absence. `COMMAND_MODE=unix2003` and `LC_ALL=C` select the reviewed
group-selector contract; exit1 with empty stdout/stderr is no match, while
unknown rows/errors fail. [Apple ps modes](https://raw.githubusercontent.com/apple-oss-distributions/adv_cmds/main/ps/ps.1).

Three additional groups exercise cancellation during a fixed 500 ms pending
validation checkpoint before anchor admission: internal 100 ms deadline, parent
IPC disconnect, and SIGTERM sent through the retained ChildProcess. Exact
validating/refusal events must retain zero child admissions, a completed exit 2
with no signal/error, and actual group absence. They launch no fixture helper
and are separate from the three helper-alive disposal observations. An early
failed/unknown native probe or blocked initialization is not a successful check.

Parent-initiated IPC disconnect can suppress Node24.14.1's combined
`ChildProcess` `close` event even after the child exits and both output pipes
reach EOF and close. A bounded owned Windows Node probe observed that behavior;
it does not qualify the Mac supervisor. Capture retains the actual exit event,
stdout/stderr EOF and close events, stdin closure and IPC disconnect separately.
That complete set may finish capture without the combined event; exit alone,
closed pipes without EOF, or a disconnect alone cannot. The ordinary combined
close event remains a supported Node lifecycle acknowledgement. Timeout, output
error and missing facts still refuse; no unknown group is signalled to manufacture
closure. Native replay must observe the same facts and the existing group-absence
checks. See pinned Node [peer EOF](https://github.com/nodejs/node/blob/v24.14.1/lib/internal/child_process.js#L638-L644),
[local IPC disconnect](https://github.com/nodejs/node/blob/v24.14.1/lib/internal/child_process.js#L889-L943)
and [close accounting](https://github.com/nodejs/node/blob/v24.14.1/lib/internal/child_process.js#L1096-L1100).

The stopped-shell barrier and all six cancellation/disposal checks still require actual
Apple Silicon execution and independent review. A foreign-host parser test is
not native validation. Unexpected anchor death, blocked event loop, escaped
descendant, IPC/signal error or surviving group member remains failed/unqualified.
Ephemeral VM destruction is containment only. Group absence does not claim the
supervisor reaped a Rust grandchild; the successful Rust case itself waits for
its helper. Generic compiler-command timeouts kill only their retained direct
child and never qualify universal descendant disposal.

## Retained result and remaining boundary

The fresh leaf retains a versioned observation, exact source records/head,
actual host/UID/job routing, tool routes/versions/hashes, current Cargo artifact
origins, Mach-O/executable/helper records, closed inventories, each checked
execution/filter count, seven documentation blocks, private mode/ACL/volume/case
facts, supervisor pre-anchor cancellation/barrier/readiness/disposal/absence records and bounded separate
command stdout/stderr. Whole environments, entropy, credentials and unrelated
disk/process rows are excluded. Failed or unknown reached observations are
retained before refusal. Pre-admission refusals create no private fixture tree.

| Criterion | Limited observation |
| --- | --- |
| BR07-FIX-01 | Exact source/tool/artifact/binary origin and inventories |
| BR07-FIX-02 | Ordinary-user private fixture provisioning and bounded retention |
| BR07-FIX-03 | Descriptor/ancestry/alias/case behavior on the selected volume |
| BR07-FIX-04 | Staging/permissions, stale/hard-link refusals, isolated retained-journal custody and completed flush-stage retries |
| BR07-FIX-05 | Exact self/owned-helper generation, bytes and observed exit |
| BR07-FIX-06 | Private CFBundle, unsigned trust refusal and worker-thread refusal |
| BR07-FIX-07 | Continuous-time/RNG native success and controlled ownership/error docs |
| BR07-FIX-08 | Owned-helper disposal and failure/input stability |

Only a fully successful native invocation may record
`nativeFixtureSubsetExecuted: true`. Every result keeps `packageAcceptance`,
`nativeRuntimeQualified` and `releaseQualified` false. This limited receipt
cannot satisfy a `br-07` package prerequisite or authorize merge/release.

Remaining unavailable includes Keychain/login/locked-user setup, signed-main and
known-signer/revocation corpus, native GUI/menu/shortcut/consent composition,
installed game sessions, suspend, both APFS case modes, comprehensive ACL/xattr
matrix, producer ABI/platform-service/production journal/bootstrap composition,
production journal home/firmlink discovery and ACL observations, contained
cross-process journal exclusion and killed-process WAL reopening, actual
crash/new-namespace/power-loss recovery and full `br-07` acceptance.
Successful flush syscalls establish neither hardware durability nor production
exclusion. Windows execution/cross compilation supplies no native Mac result.
