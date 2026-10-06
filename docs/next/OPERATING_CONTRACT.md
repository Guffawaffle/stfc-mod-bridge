# Rust Bridge operating contract

Status: accepted implementation direction; implementation and release qualification remain separate.

Owner: `Guffawaffle/stfc-mod-bridge`.

Tracking: [rewrite epic #231](https://github.com/Guffawaffle/stfc-mod-bridge/issues/231), [scope package br-00 / #232](https://github.com/Guffawaffle/stfc-mod-bridge/issues/232).

This contract governs the fresh Rust Bridge application. It supersedes the old
Windows-only C#/WPF application-stack decision for this implementation. There is
no requirement to preserve, translate, or retain the old implementation. Earlier
contracts, tests, and observations supply useful behavior scenarios; they do not
qualify the new code or its artifacts. Existing published releases keep their
own recorded support and qualification status until an explicit release cutover.

The implementation plan is in
[`../plans/rust-tauri-cross-platform/README.md`](../plans/rust-tauri-cross-platform/README.md).
Its dependency graph organizes work; it does not grant execution or release
eligibility. This document defines required behavior. The companion
[`SCENARIOS.md`](SCENARIOS.md) maps that behavior to acceptance scenarios.

## Product scope

Bridge is a desktop application for selecting an STFC installation and profile,
launching or focusing the intended game session, managing the game and supported
runtime distribution, editing configuration, and resolving actionable failures.
The first release covers Windows x64 and Apple Silicon macOS together.

Intel Mac hardware is outside this release's required host matrix. This is a
user-directed scope decision, not a verified announcement about the game's
future support. The architecture of the actual game process still determines
the injected runtime and loader architecture. An arm64 Bridge shell does not
qualify an x86_64 game or injected module running under translation. A required
translation route must have its own evidence on Apple Silicon.

Rust owns the application engine. Svelte 5, TypeScript, and Vite are the default
frontend implementation. Tauri supplies a thin desktop adapter. Interface work
must run, hot reload, and exercise complete typed mock journeys without a game,
native library, account data, or compiled Rust backend. The frontend remains
replaceable through the versioned client contract.

The player-facing scope includes profiles and installations; ordinary and
isolated launch/focus; game status/update/recovery; runtime install, update,
source switching, repair and removal; Settings and Data Sync; configuration
history; diagnostics; application preferences; and Bridge update/recovery.
Battle ingestion, sink activation, and convergence with an always-running
sidecar are deferred. No dormant capability is activated by this release plan.

## Authority and application boundaries

| Component | Owns | Required boundary |
| --- | --- | --- |
| `bridge-contracts` | Versioned requests, snapshots, events, errors, fixtures, generated frontend types | Stable identities and semantic values; no visual controls or styling |
| `bridge-domain` | Pure decisions, capability projections, supported actions and reason codes | No filesystem, process, Tauri, webview, or DOM dependency |
| `bridge-engine` | Captured operations, admission, preparation, transactions, events, recovery and application services | No Tauri/webview/DOM dependency; usable from a headless client |
| Canonical native consumers | Safe use of Profiles and shared TOML APIs | Verified source/binary pairing; originating-module buffer and lease ownership |
| Windows/macOS adapters | Native identities, processes, protected storage and platform services | Actual platform semantics; no assumed cross-platform equivalence |
| CLI/AXF and Tauri | Translate client requests and deliver results/events | Same dispatcher and policy; no independent mutation path |
| Frontend | Navigation, staged presentation, copy, layout, icons, focus and accessibility | Typed real/mock client; no filesystem, account, process or trust policy |

The Bridge repository owns the new application, backend, adapters, packaging and
release automation for both operating systems. It does not acquire the existing
macOS launcher, loader, injected runtime, or provider source by moving that code.
Runtime producers retain their artifact, manifest, schema, hook and loader
authority. Required macOS loader changes remain in the owning producer repo.

STFC Profiles remains the common authority for profile/catalog identity,
account-store lifecycle, installation registration, session/launch coordination,
exclusions and the shared game-update route. Bridge consumes its versioned native
API; it must not introduce a private replacement catalog, account store, game
downloader, patch engine, or conflicting native writer authority. The shared
TOML component supplies preservation and edit semantics through its own contract.

Authentication remains the game's and supported official launch route's
responsibility. Bridge does not accept passwords or infer a commander/account
identity from a profile name, selected UI entry, readiness flag, or process.
Canonical ordinary-profile identity belongs to the current native OS user;
isolated identity belongs to its immutable catalog ID. Platform availability
and unsupported operations are explicit results, not successful empty defaults.

Named game profiles run under the current OS user through the canonical
profile-aware runtime. Routine profile launches do not create per-profile OS
accounts or collect/store their credentials. Importing another user's saved
setup and any native OS consent for that import remain separate operations.

## One explicit target

Shuttle Bay and Engineering are views over the same selected profile and
installation model. They share one visible selection. Friendly names are
presentation; immutable IDs and native physical identity determine the resource.
An ordinary request remains ordinary even when isolated profiles exist.

Each target-dependent request supplies an explicit profile ID or ordinary mode,
installation ID and/or validated installation directory, and the exact session
identity when targeting an existing process. A process identity includes PID,
creation time and executable identity. Conflicting selectors reject. Process
inspection failure remains unknown; it cannot mean stopped or ready.

Profile installation preferences describe the next launch. Running sessions keep
their captured installation/process identity. A changed UI selection, branch,
display name, saved preference, or first enumerated process cannot retarget an
admitted operation. A profile selection never redirects a bare game launch.
An explicit request and an observation remain separate facts.

The backend captures the target, revisions, artifact/schema identities and
operation kind during preparation. Confirmation reviews that captured scope.
Commit rechecks relevant identities under the owning retained exclusion;
preparation does not reserve a resource or grant permission. A stale or ambiguous
plan rejects without silently substituting the current selection.

## Drafts and configuration

Settings and Data Sync edit one draft bound to an exact document, target,
baseline revision and schema identity. View-only navigation preserves it.
Changing its target or closing an unsaved workspace offers Save, Discard or
Stay. Save must succeed before a requested target change is applied. Discard
changes the draft, not the existing document. Stay retains the original target.

Fields, types, constraints, defaults, platform support, categories, search terms,
deprecations and apply timing come from the provider's versioned schema. The
frontend may improve presentation without maintaining a second handwritten
TOML feature list or changing persistence semantics. Unsupported schema/runtime
combinations stay unavailable with an actionable reason.

Save writes sparse user intent, preserves unknown keys and comments, validates
the complete result, retains a recoverable prior-byte backup when a document
exists, and replaces only the reviewed target atomically under its exclusion.
Removing an override is a first-class edit. A missing document is a virtual empty
baseline; opening or staging creates nothing. A meaningful first Save uses
create-new semantics, and a no-change Save creates no empty file or backup.
Unsupported preservation syntax blocks Save instead of rewriting the file.

External replacement, target/schema drift, stale revisions, failed validation,
busy admission and persistence failure preserve the draft and expose a recovery
choice. There is no automatic target switch, destructive cleanup or retry that
discards newer external edits. Apply-timing feedback describes the complete
staged set, including mixed immediate/next-launch/restart requirements.
Launcher preferences remain application state, separate from runtime TOML.

## Operation custody and replay

Requests carry stable request IDs. Accepted work has an operation ID, captured
target, observable phase, result and structured reason/recovery codes. Snapshots
and event cursors have defined revision/sequence scope so a reconnect can obtain
an authoritative snapshot and resume observation without duplicate execution.
Progress represents observed native/backend work; unknown totals do not become
invented percentages. UI acceptance of an action is visibly acknowledged even
when the final result changes nothing.

Prepared plans expire when their engine host restarts and on relevant identity
drift. Commit idempotency keys bind the exact reviewed input. Replaying the same
commit after response loss returns the original operation and its status;
conflicting key reuse rejects. An already admitted durable operation survives
as recovery evidence even though its old preparation token is no longer valid.

Admission consumes the preparation's opaque owner token and transfers it once
to the worker. Fresh keys cannot reuse that PlanRef after cancellation or
completion; exact-key replay remains first. Live pre-admission refusals preserve
the token for retry, while exact expiry retires it. The worker retains custody
through uncertain persistence, recovery and session obligations, and destroys
it before its lease and provider context. Restart reconstructs recovery-only
state from exact durable identities, never old protected preparation bytes.

The first implementation hosts the engine inside the CLI or Tauri process;
there is no implicit daemon. A writing worker retains its native/resource leases
through completion or a proven safe recovery boundary. Duplicate activation,
client disconnect, dropped observation, or caller cancellation cannot release
the worker's exclusion while it is still writing. Cancellation distinguishes
request accepted, cancelled before commit, too late, committed, and recovery
required. A losing writer returns busy before downloads, staging, backups or
journals attributable to that attempted mutation.

Normal window close/process exit waits for admitted work to finish or reach a
safe durable exit boundary, and reports why exit is deferred. Session lifetime
resources require a qualified handoff to their canonical native/runtime owner
or continued host custody; closing the UI does not make a live lease obligation
disappear. Bridge closing must not kill the game as an exit shortcut. Forced
process termination does not leave a surviving in-process worker. The next
host inspects retained journals and reports or recovers exact owned work before
dependent mutation. An interrupted journal blocks conflicting operations;
recovery cannot claim ownership of externally replaced resources.

## Independent trust domains

| Operation family | Authority |
| --- | --- |
| Runtime distribution installation/update/source switch | Exact provider IDs, producer contracts, selected release and verified artifact authority |
| Game client update/recovery | Canonical Profiles game route, official payload observations, exact installation and exclusions |
| Bridge install/update/recovery | Bridge package/channel identity, signing/verification policy and paired application/native payloads |

A signer, source pin, release preference or successful update in one family
does not authorize another. Installed bytes/provenance are separate from the
provider preference for a future check. Source switching is an explicit reviewed
transaction that includes applicable configuration compatibility and rollback;
an ordinary update does not silently cross provider authority. Unrecognized
runtime bytes remain explicitly unrecognized until the user chooses an allowed
adoption or replacement flow.

The initial package and self-update design must record Windows identity/channel
migration and macOS application identity, signing/notarization and native pairing.
Tauri packaging support alone does not qualify either installed product. Existing
release workflows and update channels cannot be assumed safe for new payloads.

## Interface and privacy

Home shows whether the player can act, the few states that affect that action,
and a contextual next action. Deliberate management and configuration use a
larger workspace. Game update, runtime update and Bridge update have separate
names, operation states and recovery. Offline but locally healthy is an explicit
usable state; unknown or unavailable remains distinct from healthy/stopped.

One shared frontend context governs target, draft and operation presentation.
Screens consume stable generated types and do not invent separate operational
policy. Browser mocks validate the same wire contract as real clients and offer
deterministic failures, progress, latency and reconnect scenarios. Production
packages exclude mock mutation hosts and test transport bypasses.

Every flow has keyboard access, visible focus, meaningful accessible labels,
focus restoration and status announcements. Status is understandable without
color or motion. System/light/dark themes, reduced motion, scaling, long labels,
high contrast and ordinary platform window/menu behavior are required. Browser
component checks do not replace native assistive-technology acceptance.

Normal UI uses meaningful target labels without displaying private filesystem
paths. Explicit target review/diagnostic disclosure can reveal a path when the
user chooses that disclosure. Diagnostics previews exactly what an export will
contain, excludes secrets/account contents by default, and makes optional path
disclosure explicit. Read-only support queries do not create account/config
stores, launch services or mutate preferences. Isolation readiness, successful
spawn and account sign-in are separate observations.

## Acceptance criteria for br-00

These IDs are stable scope checks for the qualification dispatcher. Checking
that this document and its scenario mapping exist establishes scope completeness
only. Runtime, native, accessibility and release acceptance require the later
implemented checks and their actual evidence.

| ID | Required decision |
| --- | --- |
| BR00-01 | Windows x64 and Apple Silicon macOS are required; Intel hardware excluded; actual shell/game/native architecture matrix remains explicit. |
| BR00-02 | Fresh Rust engine, Svelte 5/TypeScript/Vite default, thin Tauri shell; no C#/WPF preservation obligation. |
| BR00-03 | Headless backend and replaceable frontend share one versioned contract; complete mock UI works without native/game/Rust prerequisites. |
| BR00-04 | Profiles/TOML and runtime producers retain canonical authority; existing Mac loader remains producer-owned. |
| BR00-05 | One visible selection; explicit ordinary/isolated and exact installation/session targets; preferences cannot retarget admitted work. |
| BR00-06 | Settings/Data Sync share an exact staged draft; Save/Discard/Stay, sparse intent, preservation and conflict recovery are required. |
| BR00-07 | Captured preparation, under-exclusion revalidation, durable operation identity and exact-input commit idempotency are required. |
| BR00-08 | Runtime, game and Bridge updates retain distinct trust, operation and recovery authority. |
| BR00-09 | In-process custody, deferred safe normal exit, honest cancellation and forced-termination journal recovery are required. |
| BR00-10 | All first-release journeys in SCENARIOS.md are mapped to observable outcomes and appropriate future proof layers. |
| BR00-11 | Private paths/account contents are excluded from ordinary UI/exports by default; preview/disclosure and accessible feedback are required. |
| BR00-12 | Exact native Windows WebView2 and Apple Silicon WKWebView/architecture evidence is required; missing evidence stays unavailable. |
| BR00-13 | Battle activation and always-running sidecar convergence are deferred and confer no current capability. |

## Evidence and cutover

Contract fixtures, browser tests, Rust engine tests, native ABI/transport tests,
installed-package QA and live journeys are distinct proof layers. The exact
layer required for each scenario is recorded in SCENARIOS.md. Existing C# tests
are behavior references to reexpress, not passing executable gates for this
implementation. No scenario or future gate in these documents is recorded as
passed by authoring the contract.

Native qualification records actual host OS/architecture, commit and dirty diff,
toolchain/native dependency identity, final artifact hashes and command outcome.
Runtime evidence additionally binds client version, physical installation,
ordinary mode or immutable profile ID, actual PID/start/executable and relevant
loaded module observations. Hashing a module's disk path does not establish its
in-memory bytes. Sign-in requires its own observation.

Windows qualification exercises actual installed WebView2 and Narrator/keyboard/
high contrast/DPI behavior. Apple Silicon qualification exercises actual installed
WKWebView, VoiceOver/keyboard/scaling/native behavior and any required translated
game route. A browser mock, compile-only result, old artifact or different host
does not satisfy these checks. Missing hosts, commands, native APIs, signing
identities or required receipts remain blocked/unavailable. Final release and
old-stack retirement follow independent review, correction review, platform
qualification and the owning repository's release authority.
