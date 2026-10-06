# Native private journal storage

The Windows backend owner implements `bridge_journal_io::JournalStorage` for
the engine's retained journal codec. Its only public constructor selects
`LocalAppData/STFCModBridgeNext/v1/operations.wal` through the Windows Known
Folder API. It exposes no path override, handle extraction, cloning or thread
transfer. Production host bootstrap does not adopt this owner yet.

Construction refuses thread impersonation, unsupported or redirected physical
observations, reparses, nonlocal/nonfixed/non-NTFS volumes, and foreign existing
permissions. Existing OS-managed ancestry is trusted only within the captured
local-volume, physical-identity and retained-handle boundary; its ACL is never
changed. The two private directories and journal require the current process
user as owner and an exact protected one-user DACL. Relative native opens retain
ancestry, the leaf excludes competing opens and holds its whole-file lock.

Every constructor runs leaf flush, the entire private directory chain bottom-up
through its LocalAppData parent, another leaf flush, and custody revalidation.
This includes existing entries left by a failed earlier constructor. Existence
does not prove that a prior namespace flush completed. Failed construction may
leave declared objects; it does not remove or repair them. Append/truncate
durable boundaries flush and revalidate the retained leaf.

One atomic process reservation precedes pending-capable native submissions and
remains held for the owner's lifetime. A request is armed before its call.
Completed failures permit normal destruction; an unexpected pending result or
unclassified unwind permanently refuses further construction and retains the
complete heap request, output slots, input graph, handles and reservation until
process exit. The owner never polls pending output or treats refusal as rollback.
This is defensive lifetime policy, not an observed native pending outcome.

Thirteen unit cases cover synthetic pending/unwind/concurrency/trait-object-drop
custody, stable real request graphs with mock completion, completed-status
refusal, repeat constructor flush scheduling, bounded route/SID parsing and
synthetic private security variants. Two independent compile-fail documents
reject Send and Sync. Token/security-descriptor calls use the current process
but create no journal or private directory.

Actual ordinary-user ACL, relative creation, sharing, directory flush,
cross-process exclusion, interrupted constructor and killed-process WAL reopen
fixtures require fresh execution evidence. The existing Windows platform suite's
other native fixtures do not establish this owner's qualification. Successful flush calls
cannot claim power-loss durability. No production namespace was opened during
source implementation, and no installed game or release is qualified by it.

Apple Silicon needs its own retained private owner and namespace protocol;
the shared trait or Windows source supplies no Mac storage qualification.

## Selected Windows fixture gate

`windows-private-journal-fixtures` is a partial `br-06` suite on native Windows
x64. Its driver binds pinned tools, current source, one current Cargo library
test executable and a separate normal library build. It requires an exact
38-name inventory, 28 default cases with ten explicit skips, all five ownership
documents and nine sequential exact ignored native selections. The tenth ignored
test is the internal child helper; the driver never selects it directly.

| Criterion | Selected case and intended observation |
| --- | --- |
| BR06-WJ01 | Fresh construction and reopen: current-user owner/DACL, local fixed NTFS identity, retained namespace and complete constructor flush schedule. |
| BR06-WJ02 | Twelve synthetic DACL changes plus live drift: refusal without ACL or journal repair. |
| BR06-WJ03 | Retained leaf/private-directory sharing denies competing access. |
| BR06-WJ04 | An exact owned child competes for the same nonce, observes exclusion and releases custody. |
| BR06-WJ05 | Six injected failures after actual completed constructor flushes, followed by a complete clean retry; seven marker rows. |
| BR06-WJ06 | Two durable codec records and reopen after owned child kill; actual cleanup and retained artifact identity. |
| BR06-WJ07 | Torn append after owned child kill repairs to the last valid prefix; second open is stable. |
| BR06-WJ08 | Complete corruption refuses without changing journal bytes. |
| BR06-WJ09 | Owned-child containment failure observes exit, pipe EOF and reader cleanup; unknown cleanup retains custody and poisons further fixture construction. |

All native work uses the test-only
`LocalAppData/STFCModBridgeNextFixtures/<fresh UUIDv4>/v1/operations.wal`
namespace. The caller supplies no path, SID, handle, executable or external PID.
The shared fixture directory ACL is never repaired. Created trees remain for
inspection. A successful run reports 25 distinct nonces across 27 marker rows;
that does not enumerate or prove all 26 created trees. Child processes use the
current test executable, retained executable/process identity and unique
kill-on-close Job containment. Kill plus observed exit does not prove that no
destructor ran.

Before startup, the owned child must match the retained executable's physical
name and native architecture. Cleanup keeps that same `Child` handle, observes
exit and pipe EOF, joins readers, revalidates the retained executable and checks
the process creation FILETIME again. It does not repeat the image-name query
after exit, when that query can fail even for a valid retained process handle.
No PID is reopened or substituted during cleanup; unavailable or mismatched
creation-time observations still fail the cleanup result.

The parser validates bounded raw UTF-8, closed JSON members, exact libtest
completion, prescribed row order and native failure witnesses. Declared FILETIME
and volume u64 values are preserved exactly; receipt JSON labels their decimal
representation rather than rounding through JavaScript Number. Synthetic parser
controls do not establish native behavior. The driver must retain actual command
status, raw logs and stable source/tool/artifact observations separately.

Normal-library isolation requires an actual selected `--lib` build, its normal
dependency/features, source cfg guards and bounded archive observations. The
platform already reaches contracts indirectly through domain; the added direct
engine/contracts development edges and JobObjects fixture feature must be absent
from that normal closure. Test namespace/role markers must not appear in the
normal archive. No single absent string or source guard proves this boundary.

Cargo may hard-link its top-level normal archive to the hashed `debug/deps`
archive. The driver admits only that exact two-name pair, derived from the
selected current compiler-artifact row and this run's fresh isolated target.
Both owned regular routes must have the same file identity and exactly two
links; both open descriptors and routes are checked before and after bounded
reading and at later command fences. Separate single-link copy outputs are
also admissible. The standalone emitted metadata route is ancillary and is
not independently scanned or alias-qualified. The exclusion scan and retained
normal evidence copy cover the complete selected archive. The test executable
and every retained copy still require a single link. These disk observations
are neither a writer exclusion nor an attestation of a mapped image.

Ordinary-user qualification requires observed privilege context; Runner/Codex
startup is insufficient. Each selected process emits a separate closed context
row as its first test statement, querying its own process/current-thread token,
creation FILETIME and native architecture before journal effects. The driver
binds its PID to the actual spawn result. Only a primary non-elevated token at
medium integrity with no current-thread token permits journal work. This is
an observation of that test process, not Node's token or an account identity.
Copies of the selected test executable and isolated normal library archive
are retained as evidence and never executed in place of the original artifacts.

Hosted Windows CI uses a fixed same-user context launcher before binding and
executing this partial gate. It observes its own source token and the child
bootstrap's token separately. An already ordinary source uses its own process
context; an elevated source must produce a restricted primary token at medium
integrity that also reports non-elevated. Any failed context, launch or custody
observation refuses the run. The launcher creates no account, changes no UAC
policy or ACL, and has no elevated fallback. The native cases remain the final
observations of their own privilege context. See
[`WINDOWS_JOURNAL_CI.md`](WINDOWS_JOURNAL_CI.md) for the separate CI boundary.
Until fresh native receipts exist, the cases above
describe implemented checks awaiting observation. Full `br-06` selection remains
blocked by `PACKAGE_INTEGRATION_UNQUALIFIED`, and a partial receipt cannot satisfy
a dependent package. Foreign owners, reparse/hard-link/mapping/KnownFolder
redirection, allocator/reader-launch failure and broader custody-loss matrix,
real native pending outcomes and production owner adoption remain unqualified.
