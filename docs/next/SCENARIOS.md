# Rust Bridge first-release scenarios

Status: required acceptance catalog; scenarios are not yet implementation or
qualification results.

Owner and direction: [`OPERATING_CONTRACT.md`](OPERATING_CONTRACT.md),
[br-00 issue #232](https://github.com/Guffawaffle/stfc-mod-bridge/issues/232).

Every first-release journey must be usable in the browser mock frontend before
native integration is required. The versioned protocol package supplies typed
fixtures and assigns exact response/error/event schemas. Scenario IDs remain
stable as those fixtures and executable checks are added. Names, IDs, paths and
account-like values in fixtures must be synthetic; fixture data is not copied
from a real user's account state.

## Proof layers

| Layer | Establishes | Does not establish |
| --- | --- | --- |
| M: typed mock/browser | Schema conformance, renderable states, interaction, draft/focus flow and deterministic event handling | Native operation success, account isolation, installed WebView behavior |
| E: Rust engine/transport test host | Actual dispatcher policy, serialization, identity binding, contention, journal, idempotency and fault outcomes with controlled ports | Actual OS/native ABI or game/runtime behavior |
| N: native platform/ABI | Real adapters, canonical APIs, invoke/events, identity, native exclusions and custody on the required host | Signed installed UX, all live game or accessibility outcomes |
| P: installed package/human QA | Exact signed/pairing boundary, WebView2/WKWebView, platform conventions, accessibility and supported installed journeys | Unexercised game/account routes or another artifact's qualification |
| L: explicit live target | Actual client/artifact/profile/session behavior and required update/recovery route on a bound host | General account correctness or other versions, installations or architectures |

M and E may use controlled failure injections. N/P/L use independently recorded
host and artifact receipts. A test-only native host/server, mocked IPC plugin or
fixture account adapter must be absent from the shipped artifact. Required live
checks use approved test identities and exact targets; no automatic destructive
action follows from inclusion in this catalog.

## Required journeys

| ID | Journey and cases | Observable acceptance | Required proof | Scope criteria |
| --- | --- | --- | --- | --- |
| SC-01 | Start, restore preferences, discover/select installation; missing, invalid, unavailable and multiple candidates | Show an actionable initial state. Do not auto-select the first process or silently accept conflicting identity. Read-only discovery leaves stores/config untouched. | M, E, N, P | BR00-01, BR00-03, BR00-05, BR00-10 |
| SC-02 | Ordinary launch without an isolated profile; mod missing, supported mod present, unrecognized/changed mod, blocked/unknown process | Preserve ordinary OS-user state. Explain an allowed unmodded launch; require per-attempt override where policy permits an unrecognized proxy. Invalid target, unknown ownership and unfinished mutation remain explicit blocks. | M, E, N, P, L | BR00-04, BR00-05, BR00-08, BR00-10 |
| SC-03 | Named isolated launch; two IDs sharing one installation, duplicate request, unsupported runtime, failed readiness | Pass immutable ID and exact installation. Successful spawn and isolation readiness remain separate. Duplicate or unsupported request rejects; readiness failure is visible without silent kill/retry or a logged-in claim. | M, E, N, P, L | BR00-01, BR00-04, BR00-05, BR00-09, BR00-10 |
| SC-04 | Observe and focus running session; PID recycle, exited process, stale receipt, multiple sessions, access failure | Focus/observe only exact PID/start/executable. Advisory receipts cannot substitute for live identity. Unknown is distinct from stopped; profile preference does not move the session. | M, E, N, P, L | BR00-05, BR00-09, BR00-10, BR00-12 |
| SC-05 | Create/edit profile; ordinary metadata, isolated store-state projection and correct new/resume/existing launch routing, duplicate names, invalid/stale IDs | Create publishes a new immutable ID through the canonical catalog. Resume/existing launch keeps its existing ID and uses SC-03's native store mode; it does not clone account state into a new identity. Missing established stores do not become empty accounts. Editable names/preferences do not rewrite identity or create a private Bridge catalog. | M, E, N, P, L | BR00-04, BR00-05, BR00-10 |
| SC-06 | Windows-user import or platform-supported setup; prepare/review/cancel, denied access, protected-user consent, retry | State source/destination/scope before action. Cancellation/failure publishes no profile. Bridge has no password form or browser-copy flow; unsupported platform import is clearly unavailable. Successful import does not launch a game. | M, E, N, P, L | BR00-01, BR00-04, BR00-09, BR00-10, BR00-11 |
| SC-07 | Archive/restore/delete; active session, cancelled confirmation, duplicate ID, ordinary protected entry | Preserve immutable ID and entire owned data on archive/restore. Shared exclusion prevents live-session lifecycle races. Ordinary profile cannot be archived/deleted. Permanent deletion is a separate confirmed operation. | M, E, N, P, L | BR00-04, BR00-05, BR00-07, BR00-10 |
| SC-08 | View navigation and target change with clean/dirty/invalid/stale Settings or Data Sync draft | Shuttle Bay/Engineering share one selection. View-only changes preserve drafts. Save changes target only after successful commit; Discard drops the draft; Stay preserves old target. One transition produces one review, not competing dialogs. | M, E, N, P | BR00-03, BR00-05, BR00-06, BR00-10, BR00-11 |
| SC-09 | Schema settings and Data Sync editing; all semantic field types, search, reset override, missing file, mixed timing | Schema drives controls and validation. Preserve sparse intent/comments/unknown keys. Opening/staging/no-change Save create nothing. First meaningful Save creates only selected overrides. Display accurate whole-draft apply timing. | M, E, N, P | BR00-03, BR00-04, BR00-06, BR00-10 |
| SC-10 | Save/history/restore; outside file replacement, same revision on different target, backup failure, unsupported syntax, busy lease | Bind exact target/document/schema and retain edits on failure. Refuse destructive rewrite or stale save. Prior bytes receive a verified recoverable backup where applicable. Restore is an explicit reviewed mutation of the captured target. | M, E, N, P | BR00-06, BR00-07, BR00-08, BR00-10 |
| SC-11 | Runtime install/update/repair/remove/adopt/source switch; invalid authority/hash, offline check, modified/unowned files, selection change, interrupted commit | Review exact provider/release/installation and configuration participant. Revalidate under exclusion. Preserve unowned/external resources. Separate remove from stop-managing. Failures reach rolled-back, committed or explicit recovery-required state; no silent provider crossing. | M, E, N, P, L | BR00-04, BR00-05, BR00-06, BR00-07, BR00-08, BR00-10 |
| SC-12 | Game status/check/update/recover; checked version, active shared-install sessions, offline, unavailable native route, selection change, interruption | Canonical Profiles route owns work. Update keeps exact checked target/version and observed native progress. Every session exclusion applies. Unsupported Mac route stays unavailable until a qualified direct update or managed handoff fulfills the recorded journey. | M, E, N, P, L | BR00-01, BR00-04, BR00-05, BR00-07, BR00-08, BR00-09, BR00-10 |
| SC-13 | Bridge install/update/recover; pairing/signature/channel mismatch, interruption, downgrade/withdrawal, denied replacement | Use separate Bridge authority. Exact helper/native/app bytes and package identity are bound. Update cannot mutate game/catalog roots. Show explicit recovery and retain external user data through uninstall. | M, E, N, P | BR00-01, BR00-07, BR00-08, BR00-09, BR00-10, BR00-12 |
| SC-14 | Duplicate submit, UI/CLI race, lost commit response, reconnect, conflicting idempotency-key reuse | One exact writer; loser is busy before mutation side effects. Exact replay returns the original operation; changed-input replay rejects. Resnapshot/reconnect observes actual work without launching it again. | M, E, N, P | BR00-03, BR00-05, BR00-07, BR00-09, BR00-10 |
| SC-15 | Cancel, close window, normal exit, forced host kill/restart at durable boundaries | Cancellation is honest about too-late/committed states. Normal exit retains worker/lifetime custody until safe. Force kill leaves no fictitious surviving engine; next host inspects exact journal and blocks conflicting work until recovery. Game is not killed to make Bridge exit. | M, E, N, P | BR00-07, BR00-09, BR00-10, BR00-12 |
| SC-16 | Diagnostics preview/export, capability unknown, optional path disclosure, secret-like input, offline support | Ordinary UI hides private paths. Export matches reviewed preview; secrets/account contents are filtered. Explicit optional disclosure is separate. Support reads do not create stores, change config or start sinks/sidecars. | M, E, N, P | BR00-03, BR00-10, BR00-11, BR00-13 |
| SC-17 | Keyboard/accessibility/theme/window behavior across all normal/error/progress/recovery fixtures | Visible focus, labels, announcements and restoration; duplicate input prevented; no-change action acknowledged. System/light/dark, reduced motion, high contrast, 200% text, long labels and native menus/window controls are usable. | M, P | BR00-01, BR00-03, BR00-10, BR00-11, BR00-12 |
| SC-18 | Headless/real/mock client parity and architecture evidence; Apple Silicon translated game where necessary | Same normalized domain outcomes across mock, CLI and actual Tauri transport. Headless code does not link Tauri. Actual installed Windows/Apple Silicon receipts qualify their own bytes; in-process native modules match shell, injected modules match game. | M, E, N, P, L | BR00-01, BR00-02, BR00-03, BR00-04, BR00-10, BR00-12 |

## Cross-cutting checks

Every applicable journey includes ready, working, completed-with-changes,
completed-without-changes, unavailable/blocked, failed and recovery states.
Expected refusal is a successful test only when the actual refusal and absence
of forbidden side effects are asserted. A skip, missing command/host, empty
response, unimplemented action or unavailable required production capability
cannot be counted as successful implementation acceptance.

Mock UX review covers all first-release journeys and retains screenshots plus
interaction/focus observations against exact frontend inputs. An approved mock
milestone allows independent engine work and eventual real binding; it does not
approve native operations. Real binding must conform to the same typed fixtures
and include native invoke/event/reconnect checks on each required platform.

Windows installed QA records WebView2, Narrator, keyboard, high contrast and DPI
behavior. Apple Silicon installed QA records WKWebView, VoiceOver, keyboard,
display scaling and platform window/menu behavior. Where automation coverage
does not exercise an outcome, retain explicit human/native evidence rather than
label a mocked browser result as that proof. Test tools and servers are absent
from the released artifact.

Mac profile import need not duplicate Windows-specific SID/UAC behavior. Any
offered native import/setup route must fulfill canonical platform authority and
consent semantics. Ordinary and isolated launch, focus, profile lifecycle,
configuration, game management, runtime management, support and Bridge update
remain required journeys on both operating systems. Missing native capability
blocks that platform's release; a friendly unavailable screen alone is not
cross-platform completion.

## Criteria coverage and result recording

BR00-01 through BR00-12 map to the journey table. BR00-13 also maps to SC-16 and
to the explicit non-activation boundary in OPERATING_CONTRACT.md. New fixtures
and tests reference stable SC/BR00 IDs; an intentional scope change updates the
contract and catalog together through the contract owner.

Implemented gate receipts identify scenario, proof layer, owning repository,
exact source/diff, command/cwd, host architecture/toolchain, dependency/native
hashes, artifact hashes, observations, outcome and unresolved limitations.
Live receipts also identify the client, physical installation, profile/mode and
exact process/session. User/account values remain excluded. Every required
suite and platform receipt is collected; one passing suite cannot accept a
multi-suite package. No result is attached to these scenarios by this document.
