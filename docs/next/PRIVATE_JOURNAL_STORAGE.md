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
fixtures remain required. The existing Windows platform suite's other native
fixtures do not establish this owner's qualification. Successful flush calls
cannot claim power-loss durability. No production namespace was opened during
source implementation, and no installed game or release is qualified by it.

Apple Silicon needs its own retained private owner and namespace protocol;
the shared trait or Windows source supplies no Mac storage qualification.
