# Management and Support

The Management and Support views consume the shared typed client, observation
store, work context and action review controller. The separate development
entry at `ui/management-preview/` mounts the actual App with an injected fixture
facade, so its navigation, shared ActionReview, operation feedback and focus
behavior are the same components used by the application. This document defines
br-20's source and browser evidence boundaries; it records no package, native or
release acceptance.

Run from the canonical Bridge checkout:

```powershell
node scripts/next/frontend-management-tests.mjs
node scripts/next/frontend-management-browser.mjs
```

The command accepts no selection, filter, alternate report, loader, runtime or
fixture routing override. It requires the exact Node pin in the workspace
manifest and toolchain descriptor. It runs the actual Svelte/TypeScript check
with zero errors and warnings, then only the frozen focused Vitest files in
`frontend-management-evidence.mjs`. The inventory includes Management and
Support tests, cancellation, recovery custody, adversarial action custody and
diagnostic integrity, plus the closed Management/Support preview-session cases.
The frozen inventory covers 195 assertions across nine files, including eleven
preview-session cases. Its exact authored count is exported as `managementCounts`
in `frontend-management-evidence.mjs`. Counts come from the executed JSON report;
writing an inventory or a document establishes no passing coverage.

Every required assertion must execute exactly once and pass. Missing, renamed,
duplicate, substituted or extra cases/files refuse evidence. Failed, pending,
skipped, cancelled and todo cases refuse even when the command exits zero.
Synthetic positive and adversarial report controls separately validate this
receipt parser. Controls explicitly refuse an omitted preview file or case,
substitution with another passing source case, duplicate preview assertions and
skipped preview cases. They are not Management or Support behavior tests.

The driver retains command outcomes, logs, the actual JSON report and hashes in
a fresh owned `artifacts/next/frontend-management-tests/<invocation>/` directory.
It records source and directory membership before and after execution through
`input-tree.mjs`, rejects links in source trees, and proves identical inputs.
Because the whole-UI type check reads shared code and test sources, the bound
inventory includes every current UI source entry, including preview modules
imported from outside `src` and `tests`, generated types, scenarios, fixtures
and the exact driver dependencies. Only the dependency container and `ui/dist`
build output are excluded from this UI source traversal; actual installed tools
are observed separately. A formal run needs this whole
source set frozen; editing another screen can invalidate the run even though
its tests were not selected.

Actual tool observations bind Node bytes, pinned package identities, recursively
resolved installed runtime dependency packages and UI command shims before and
after execution. Linked packages resolve to physical directories within the
owning dependency installation; package bodies then use link-refusing input
inventories. Optional dependencies absent on this host are recorded as absent.
Neither a lockfile nor a successful package lookup alone proves the executable
tool bytes. Failures after an owned invocation starts retain their observations
and cannot emit a passing receipt. Invalid invocation/tooling preconditions
refuse before any test command runs.

## Scope criteria

These IDs bind the source behavior to the browser driver's fixed ordered check
map. Focused tests exercise closed synthetic DTOs and portable
controllers/presentation functions. Browser checks exercise the actual App over
the same typed fixture boundary. Neither supplies native operation authority.

| ID | Required behavior | Evidence and current limits |
| --- | --- | --- |
| BR20-01 | Profiles and native import use immutable identities, exact observed revisions, explicit destination owner and a host-bound canonical catalog mutation baseline. Duplicate names remain independent. Future-launch preferences do not retarget the current draft; ordinary deletion stays unavailable. | Browser checks cover duplicate rows, Stay/focus, admission versus completion, archived restore, explicit deletion, creation and native-approval choice. Source tests refuse stale/foreign references and inaccessible imports. Canonical catalog and import authority remain native requirements. |
| BR20-02 | Installation rename and registration capture exact registered/physical identity and the canonical catalog baseline. Explicit directory entry does not disclose the private path in normal review. | Browser checks cover rename and registration review. Source tests retain exact action scope. Native registration, path validation, exclusions and physical revalidation remain unqualified. |
| BR20-03 | Runtime, official game and Bridge updates retain separate target, package and trust authority. Runtime changes include the exact configuration participant; a different distribution requires explicit source-switch review. Bridge checks require an observed current application. | Browser reviews preserve each update route and expose absent metadata. Source tests refuse ownership, host, installation, schema and release drift. Producer recognition, payload policy, signing/notarization and transactional writer services remain native/backend seams. |
| BR20-04 | History uses the shared current document and retained producer backup. Operations distinguish admission, completion, cancellation requested/too late and recorded game/Bridge recovery. Duplicate requests, reentrant publication, disposal and late replies cannot replace custody. | Browser checks exercise main History navigation, restore review, pending cancellation and recorded recovery reviews. Source tests cover exact replay, revision correlation and original/replacement custody. Durable admission, native cancellation boundaries, lease lifetime, forced-death recovery and safe close require separate engine/native proof. |
| BR20-05 | Support verifies a redacted preview by default, requires explicit fresh path disclosure and an opaque backend-captured destination, and stops at reviewed export preparation. | Browser checks exercise redaction, chooser-before-review, fresh disclosure and cancelled destination. Source tests cover digest/scope/privacy drift, Rust Option None, async hash abandonment and disclosure resets. Real chooser/export and comprehensive native secret/account filtering remain unqualified. |
| BR20-06 | Normal Management UI excludes private custody and filesystem paths. The actual App retains labels, review focus, responsive layouts, dark/forced-color themes, reduced motion and enlarged text. Production excludes development fixture hosts. | Pinned-browser checks capture desktop and compact 200% text, geometry, focus and screenshots. The separate production build inspects its module graph. Installed WebView2/WKWebView, Narrator/VoiceOver and native window behavior require independent host evidence. |

## Actual App preview and browser evidence

The development session composes bounded synthetic responses from current
Rust-derived accepted fixtures and records their hashes. Requests and replies
cross the shared strict client boundary. Reviewed plans preserve exact captured
scope and semantic digests; commit admission, observed completion, cancellation
and recovery remain distinct. The session contains explicit synthetic catalog,
application and runtime metadata solely for these journeys. It cannot discover
native authority or authorize a real filesystem, game, update or export effect.

The browser driver owns a private pinned Chromium context and local Vite child,
checks fixture provenance, refuses external requests and browser errors, and
retains screenshot bytes/hashes. Its fixed 30 checks are bound to BR20-01 through
BR20-06. Source files and directory membership are compared before and after;
concurrent changes invalidate the receipt. Cleanup must complete before a
passing receipt is written. Logs, screenshots and JSON stay in a fresh owned
`artifacts/next/frontend-management-browser/<invocation>/` directory.

The browser driver invokes no Rust build or native service. A browser receipt
describes only its exact source candidate, observed browser and synthetic
journeys. Root's final suite separately combines focused tests, browser checks
and a production dependency inspection after the whole candidate is frozen.

## Review, admission and recovery custody

Controllers read and prepare through the shared client. A prepared review binds
the captured target and semantic digest; it grants no lock or permission.
Confirmation is explicit. An admitted reply remains an observation of work,
not a completed mutation. Changing visible target selection cannot retarget a
captured operation. Unknown totals remain unknown, and old replies cannot
replace a newer operation revision.

The client retains exact commit input and idempotency identity after uncertain
delivery. Caller abort or view/controller disposal abandons observation rather
than sending backend cancellation or proving that work did not run. A proved
unsent original attempt may return to review; a sent rejection or lost response
keeps uncertainty until reconciliation. Explicit replay submits the retained
exact input. A newly requested replay after a host change may observe the
original durable identity; a reply crossing the epoch during an earlier
request cannot enter the replacement observation context.

Observed recovery parks the original submission while allowing a separate
modeled recovery review. Stay, rejected preparation and disposal preserve that
original replay. A recovery commit has its own replay custody. Its terminal
result does not retire the original operation's replay before original terminal
reconciliation. Release compares both exact input and captured request identity,
so an equal-input replacement replay remains owned by its replacement caller.
Typed recovery target/capture correlation must pass before the observation
store adopts a row and a controller derives an intent. Unsupported recovery
families retain their recorded evidence without an invented generic command.

Settings and Data Sync continue to own the shared staged draft. Management
views do not read TOML or write stores. A runtime configuration participant or
History restore must remain bound to the reviewed document and schema. Dirty
drafts and missing/changed baseline observations prevent an incompatible
management action; view navigation and Stay preserve local edits. Backend
admission, resource exclusions, physical revalidation and persistence retain
their own qualification requirements.

## Remaining qualification seams

The focused driver executes source type checks and controller/client/presentation
and preview-session tests. Presentation assertions and SSR rendering qualify
only their observed source output. Actual App browser interaction, focus, modal
lifecycle, layout, scaling and themes require the separate browser receipt.
Source labels, browser focus and mock announcements do not qualify Narrator,
VoiceOver or native assistive technology.

The production application still needs authoritative bootstrap inputs for the
current Bridge application, catalog mutation baseline, provider inventory and
runtime ownership/recognition. Missing metadata must remain explicitly
unavailable. A fixture value, friendly display name, remembered selection or
caller-supplied placeholder cannot become native authority. Backend projections
must establish actual availability and action-specific refusal reasons.

The current wire Snapshot and Hello do not expose an explicit catalog-wide
mutation baseline, current Bridge application binding or ordinary runtime
ownership query. Profile/installation inventory revisions are not defined as
the catalog mutation baseline. Bridge update queries require a current
application binding as input, and optional application/runtime diagnostic facts
do not establish a general metadata bootstrap. CLI/Tauri composition needs an
explicit typed backend observation route; it must not derive these values from
event sequence, display names, release offers or fixture metadata.

`ManagementController` captures its `ManagementInputs` at construction and
currently checks metadata freshness against authoritative confidence and host
epoch. Same-host catalog/application/runtime changes therefore need an explicit
typed refresh path or deliberate controller recomposition with newly observed
inputs. Refreshing the ordinary snapshot alone cannot certify the original
metadata as current. Until that integration exists, missing authority remains
unavailable and backend revision checks retain their own enforcement role.

Native Profiles/TOML invocation, runtime producer compatibility, installation and
process identity, writer exclusions, cancellation/durability/recovery, actual
diagnostic filtering and export, protected storage and exact backup ownership
are separate backend/native acceptance tasks. Synthetic fixtures contain no
real game/account material and cannot prove that support reads leave real
stores/configuration/services untouched.

Windows x64 installed WebView2 and Apple Silicon installed WKWebView each need
their own package-bound real journeys, native window/menu/shortcut behavior,
keyboard/focus/scaling, Narrator/VoiceOver and signing/update evidence. Actual
game/runtime architecture, including any required translated route on Apple
Silicon, stays explicit. This source driver invokes no Rust build, native host,
browser, game, chooser or filesystem writer service, and grants no release or
old-stack retirement eligibility. Root owns gate composition, registry,
campaign admission and independent review.
