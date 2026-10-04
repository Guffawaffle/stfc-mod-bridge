# Operation kernel

br-04 belongs to issue #238. `bridge-engine::operations` hosts the engine within
the calling CLI or desktop process. It depends on portable contracts and owner
ports, without a Tauri, webview or frontend dependency. The kernel supplies
dispatch, prepared-plan capture, admission/replay, worker progression,
cancellation, close dispositions, event cursors and durable restart recovery.
Native domain services and canonical platform consumers remain later packages.

BR04-01: the owner port acquires canonical resource exclusions before commit
revalidation. A losing writer returns busy before a download, stage, backup or
admission journal. The worker retains its lease while observers disconnect.
Fixture assertions count all these effects and race two engine instances.

BR04-02: native preparation retains the exact intent/capture, expiry and owner
bindings. Commit revalidates physical/revision identity under the lease and
refuses retargeting, stale revisions, foreign recovery bindings and expired
plans. The platform owner must retain and revalidate the original installation
directory/path assertion against the prepared intent under exclusion; a
directory selector may resolve to its registered installation. Launch captures
carry opaque native bindings, and v1 register-installation capture omits its
path. The portable kernel cannot derive physical equivalence from those refs.
Registered selectors still require the exact registration kind, ID and asserted
revision. Opaque identifiers never replace a physical owner check.
Public dispatch tests cover both ordinary and isolated directory requests and
registered kind/ID/revision substitutions before effects. A data-only unit test
also checks all 26 action kinds against the accepted shared preparation corpus,
while empty or duplicated owner-resource lists remain invalid.

BR04-03: observer loss does not cancel the worker or release exclusions.
Explicit cancellation distinguishes precommit cancellation, a retained request,
a native noncancellable boundary, honest completion and recovery-required state.
The fixture owner controls its durable boundary; the kernel cannot infer that
an arbitrary port error rolled back native effects.

BR04-04: normal close is deferred while admitted work or session custody has an
unsafe outstanding obligation. The host must obey the returned disposition;
closing a webview alone cannot authorize process exit. An actual forced child
process kill ends all work. Restart inspects its durable journal and native
owner boundary, without claiming execution continued after process death.
Whenever close is deferred, its operation obligations cover every noncompleted
operation at its exact observed revision, including a recovery already at a
safe owner boundary. Session custody is an additional obligation. Handing off
that session cannot release an unsafe recovery operation's resource exclusion;
the owning recovery must independently establish its safe boundary.
The full 64-operation fixture retains 128 operation/session obligations through
restart and native reinspection; the reply and event codecs preserve all of
them. A 65th admission is refused before acquisition, journaling or mutation.

BR04-05: admission is persisted before acknowledgement. Exact commit replay
looks up retained input/operation identity before rejecting an old host's plan;
it returns the original operation without new effects. Conflicting input for a
retained idempotency key is refused. The bounded candidate retains at most 64
operations and refuses admission before effects when full. It does not silently
evict replay records; sustainable history retention/pagination must be resolved
before a release workloads claim.

BR04-06: restart invalidates uncommitted plans and creates fresh host/stream
identity. Admitted durable work remains inspectable by operation ID. Scoped
consecutive events have bounded retention; an unavailable cursor requires a new
snapshot rather than reexecution. Complete snapshots never omit known pending
work to fit a wire limit.

BR04-07: the filesystem journal uses bounded length/JSON/SHA-256 frames,
exclusive file custody and sync boundaries. Complete corruption or conflicting
replay binding blocks open; incomplete trailing frames are handled through the
recorded recovery path. Tests exercise admission persistence failure, stage
interruption/rollback, native commit before terminal acknowledgement, foreign
replacement, close obligations and real subprocess kills at admitted, staging
and committed boundaries. A native owner supplies the exact recovery binding
before admission and reconciles its effects; missing custody is unavailable.

The concrete `FileJournal` assumes a private directory provisioned and qualified
by its platform owner. Kernel fixture proof does not qualify a shared game
catalog, permissions, canonical process lifetime exclusions or native domain
services. Journal custody alone is not a cross-process game writer lock.

`node scripts/next/kernel.mjs operation` runs formatting/strict Clippy, compiles
the exact current native test artifact, verifies its executable architecture
and runs the operation inventory. `kernel.mjs recovery` compiles its own current
artifact and executes the required named durable/crash tests, refusing absent
or ignored tests. The operation suite separately compiles, inventories and
executes the required accepted-corpus binding unit test. Together the inventories
cover all kernel tests. Each receipt
binds the actual Cargo compiler artifact and executable bytes before/after
execution. Both suites are required by the br-04 LexRunner projection.
These are synthetic owner/filesystem/process-boundary observations, not an
installed application, game/runtime, signed review or release qualification.
