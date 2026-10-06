# Windows journal fixture CI context

The `windows-private-journal-fixtures` gate requires a primary, non-elevated,
medium-integrity token, no current-thread token and native AMD64 execution
before journal effects. GitHub's hosted Windows runner is documented to run as
administrator with UAC disabled. Ordinary child creation inherits that source
context, so invoking the existing gate directly does not establish its required
privilege context.
[GitHub's hosted runner privilege contract](https://docs.github.com/en/actions/reference/runners/github-hosted-runners#administrative-privileges).

The fixed zero-argument `scripts/next/windows-journal-ci.ps1` launcher derives
the owning checkout from its checked-in location and requires that canonical
cwd. It loads the checked-in C# API binding and observes only its own process
and current thread. An already ordinary source follows a fixed own-context
launch route. An elevated source follows one fixed restriction route:
`CreateRestrictedToken(DISABLE_MAX_PRIVILEGE | LUA_TOKEN)`, Administrators
deny-only, no additional restricting SID list, and medium integrity applied
only to the derived token. The derived token must satisfy the complete ordinary
predicate before launch and retain the source user, session and logon identity.

Before that derived token is used, the launcher adds one explicit `GENERIC_ALL`
ACE for its own captured user SID to its populated default DACL. Existing ACEs,
including denials, remain in their original order. An exact existing explicit
grant is reused. NULL, empty, unavailable, malformed or over-budget ACLs refuse;
the written ACL must read back byte-for-byte. This changes only the newly owned
token's defaults for objects created with it. The caller token, existing
objects, desktop ACLs, user profile, group restrictions and privileges remain
unchanged. The ordinary predicate and same-user/session/logon checks still run
after preparation, and the child observes its own context independently.

The retained hosted failure used a default ACL with full access for SYSTEM and
Administrators, and only read/execute access for the logon SID. Administrators
was deny-only in the derived token. A controlled local experiment using that
ACL shape reproduced `0xC0000142` before the PowerShell bootstrap; invoking the
new default-DACL preparation on a separate derived token allowed the same
fixed child to start, observe its ordinary context and settle its owned job.
This supports the narrow correction; it does not qualify the actual hosted
route, the journal fixtures or a released application.
[TokenDefaultDacl](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ne-winnt-token_information_class),
[SetTokenInformation](https://learn.microsoft.com/en-us/windows/win32/api/securitybaseapi/nf-securitybaseapi-settokeninformation).

The API documentation does not guarantee that these restrictions clear
`TokenElevation` on a UAC-disabled administrator. The launcher queries it and
refuses if it remains elevated. Launch privilege failures also refuse; neither
a successful restriction API call nor medium integrity alone proves ordinary
execution. No account creation, credential login, external process token,
permission relabeling, UAC change or retry with weaker restrictions is part of
this route.
[CreateRestrictedToken](https://learn.microsoft.com/en-us/windows/win32/api/securitybaseapi/nf-securitybaseapi-createrestrictedtoken),
[CreateProcessAsUserW](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessasuserw).

Before spawn, the launcher records the parent's current window-station and
thread-desktop names, flags and bounded owner/group/DACL and mandatory-label
snapshots. A query-only duplicate of the selected token supplies a read-only
`AccessCheck(MAXIMUM_ALLOWED)` against the captured DACL, with the documented
object-specific generic mapping. Refused, unsupported or oversized observations
remain unavailable; they do not change the launch route. The duplicate is never
attached to a thread, borrowed user-object handles are never closed, and no
desktop, ACL or privilege is changed. The fixed `Probe()` uses the same token
selection for local read-only observations without launching a child.
These snapshots describe parent objects; they do not prove the child's actual
assignment, mandatory-integrity authorization or DLL initialization. In
particular, a discretionary-access pass cannot qualify the native fixtures.
[AccessCheck](https://learn.microsoft.com/en-us/windows/win32/api/securitybaseapi/nf-securitybaseapi-accesscheck),
[user-object security](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getuserobjectsecurity),
[security-information query rights](https://learn.microsoft.com/en-us/windows/win32/secauthz/security-information).

Immediately before bootstrap creation, a separate bounded read of the parent's
own window-station and current-thread-desktop names forms the explicit
`station\desktop` request in `STARTUPINFO.lpDesktop`. This read does not depend
on the advisory DACL observations. Invalid or unavailable names refuse before
creation. Its UTF-16 allocation remains owned through process creation; the
three-entry standard-handle allowlist is unchanged. There is one fixed attempt
and no retry with a null desktop, new user object, altered ACL or privilege.
The later Node spawn retains its existing null-desktop behavior, keeping this
comparison at bootstrap startup.
[STARTUPINFOW](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/ns-processthreadsapi-startupinfow).

Immediately before that spawn, the launcher's optional `startupSecurity` bundle
queries `TokenDefaultDacl` on the exact selected candidate, or a query-only
handle to its own primary token on the ordinary route. The ordinary handle's
token type, elevation, integrity and user/session/logon identity must match the
selected context before its default DACL is recorded. The pointer header,
borrowed ACL pointer, returned extent and ACL header are bounded before native
dereferencing or copying; ACE extents are checked in the bounded managed copy.
A successful NULL default DACL, an empty ACL,
a populated ACL and an unavailable observation remain distinct.
[GetTokenInformation](https://learn.microsoft.com/en-us/windows/win32/api/securitybaseapi/nf-securitybaseapi-gettokeninformation),
[TOKEN_DEFAULT_DACL](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-token_default_dacl),
[ACL](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-acl),
[IsValidAcl](https://learn.microsoft.com/en-us/windows/win32/api/securitybaseapi/nf-securitybaseapi-isvalidacl).

After the returned child handles enter the owned Job and before `ResumeThread`,
the same launcher-only bundle records read-only owner/group/DACL snapshots for
the owned process and initial thread. `GetSecurityInfo(SE_KERNEL_OBJECT, 7)`
returns its error as a DWORD; the receipt retains that unsigned value directly.
Each fixed process/thread role and `assigned_before_resume` phase is bound to
the returned PID, process creation time and initial thread ID. The allocated
self-relative descriptor is copied within the snapshot bound and passed once
to `LocalFree` in `finally`; a reported free failure makes an otherwise observed
snapshot unavailable. Its component pointers are never freed
individually. No SACL is queried and no privilege or permission changes occur.
[GetSecurityInfo](https://learn.microsoft.com/en-us/windows/win32/api/aclapi/nf-aclapi-getsecurityinfo),
[self-relative security descriptors](https://learn.microsoft.com/en-us/windows/win32/secauthz/absolute-and-self-relative-security-descriptors),
[LocalFree](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-localfree).

Each new raw snapshot is limited to 4096 bytes, with exact byte count and SHA-256
binding. The closed diagnostic bundle is limited to 24576 serialized UTF-8
bytes, including its trailing newline. The existing aggregate launcher receipt
limit remains 65536 bytes. Optional raw observations become unavailable if
necessary to fit that receipt; the entire optional bundle is omitted if even
its validated metadata cannot fit. This does not relax the aggregate receipt
limit or change launch/custody success criteria. These observations neither
identify a failed DLL nor establish the cause of an initialization exit, an
access decision, loader success or native fixture qualification. The child
handshake and later Node spawn remain unchanged. The 179 pure controls include
synthetic pointer, ACL, self-relative descriptor, closed state/role/identity and
receipt-budget and own-user default-ACL preservation, idempotence and refusal
cases; they do not execute native queries or launch a child.

The child bootstrap is a fixed checked-in PowerShell script. It observes its
own token before starting pinned Node and compares its user/session/logon and
current-user LocalAppData observations to the bounded source handshake.

The fixture driver creates a fresh `test-target` below its own invocation's
artifact directory and binds Clippy, test compilation and ownership doc tests
to that target. It retains the separate fresh normal-library target and all
source/tool/executable fences. Cargo-selected test executables must resolve
under the exact fresh test target and native host triple. This avoids opening
the earlier elevated foundation build's shared Cargo lock; no existing target
ACL is rewritten, no caller target selector is accepted and no elevated retry
is introduced. Build-output isolation does not qualify journal construction.

The versioned closed handshake also carries the requested desktop. Before Node
starts, the child independently queries its own station and thread desktop,
records those observations separately from the parent DACL snapshots, and
requires an ordinal case-insensitive name match. The launcher binds that receipt to
the request and the exact returned child PID/creation time. Matching names do
not establish physical user-object handle identity, loader initialization or
native fixture success; an initialization failure can occur before any child
receipt exists.

Node's fixed entry validates the complete selected partial plan, its projection and
three prerequisite receipt hashes against the actual current Git head. It
retains unique one-link plan/projection copies and invokes the retained plan
through the pinned LexRunner 2.1.0 JS entry directly. The CLI and manifest must
remain physically inside the canonical npm package without redirected
ancestry. It supplies no caller-selectable command,
executable, plan, artifact directory, token, handle or PID. Node's token remains
unobserved by that script; each of the nine selected native test processes still
observes its own token and binds its emitted PID to the driver's actual spawn.

The launcher owns an unnamed kill-on-close Job. The fixed bootstrap starts
suspended and must enter that Job before resuming. Only the intended standard
handles inherit. Separate raw stdout/stderr, returned process identity and
bounded process/pipe/Job settlement are retained. The C# supervisor budgets
650000 ms for its child, 670000 ms for work and 700000 ms total, including up to
30000 ms for failure cleanup. Output draining and joins use the work horizon,
preserving the cleanup allowance. Even after an expired total deadline, failure
cleanup requests owned I/O cancellation and makes one nonblocking joint custody
observation; a request alone never establishes settlement. These clocks cover
the supervised C# operation;
PowerShell compilation/bootstrap and synchronous OS/filesystem calls are not
independently interruptible by that stopwatch. The workflow's separate 60-minute
timeout is the outer bound. Timeout, capture overflow,
nonzero exit, source/tool drift or unsettled custody fails. Cleanup terminates
only this owned Job and cannot qualify the gate. The bootstrap, Node entry and
native fixture observations have separate receipts under `artifacts/next/`.
Node observes original prerequisites, generated files, retained copies and
source/tool bytes before and after commands. These disk observations detect
observed drift; they are not a writer exclusion or proof of an immutable
in-memory executable image.

An implemented launcher, a local token-only probe or pure control tests do not
establish that the hosted restriction route works. Acceptance requires an
actual corrected-candidate hosted run, all nine ordinary-context observations,
the selected archive exclusion, default cases and ownership doctests, and
complete retained custody and raw command evidence. Full `br-06`, production
journal adoption, native application/game and release qualification remain
separate incomplete boundaries.
