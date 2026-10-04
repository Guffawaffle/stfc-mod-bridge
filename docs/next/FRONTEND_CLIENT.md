# Typed client and frontend development

br-03 belongs to issue #237. `ui/src/client` contains the renderer-independent
client; `ui/src/mocks` and `ui/scenarios` provide a development workbench. The
production composition uses the same client with an explicitly unavailable
transport until br-21 supplies the native binding.

BR03-01: run `node scripts/next/pnpm.mjs --dir ui dev` and open
`http://127.0.0.1:1420/scenarios/index.html`. Pick any of the 147 accepted shared
journeys. The manual clock controls delivery/progress, reply loss, malformed raw
frames and explicit disconnect/reconnect. Install the project browser for the
automated route with `node scripts/next/browser.mjs install`; then run
`node scripts/next/mock-development.mjs`. These commands invoke no Rust build,
native Bridge library, game or account profile. Playwright is browser test
tooling; this does not qualify a native WebView2/WKWebView client.

BR03-02: request/reply/event guards are compiled from checked-in Rust-derived
schemas with Ajv strict validation and bundled browser helpers. Data is copied
and frozen before serialization; accessors, conversion hooks, cycles, exotic
objects, duplicate JSON keys and unsafe numeric values are refused. Replies
must match the exact request ID, method, version and applicable captured
relationships. Preparation compares every applicable input/selector relationship;
directory paths cannot prove equivalence to opaque native bindings. Prepare and
commit verify the Rust semantic review digest using browser WebCrypto, including
Rust optional/default serialization, effect ordering and the descriptive restore
timestamp exception. Timeout, abort or disposal during hashing cannot admit a
late replay identity. Missing or failed hashing is a sanitized local fault;
uncertain commit input remains available for exact replay.
Document/draft/protected-input echoes and backend binding observations compare
Rust-equivalent optional `None` fields within explicitly generated DTO closures.
The bounded helper excludes request/reply envelopes and commit input; required
nullable reply IDs, exact replay input, staged edits and local reviews retain
their original checks. Drift tests inspect each nullable binding property and
its owning Rust serde annotation. Real identity/revision/document changes still
refuse rather than being erased by normalization.
The same guards validate all 569 shared wire goldens. Rust still
owns cross-field semantic validation and native decisions. Generated guards and
the 147-script/452-frame catalog have deterministic source/hash manifests;
`generate-browser-validators.mjs --check` and `generate-mock-catalog.mjs --check`
refuse drift without compiling Rust.

BR03-03: mock exchanges accept only the next declared shared request and reply.
Only request correlation IDs are remapped; immutable target IDs, physical
references, revisions, errors, idempotency keys, digests and event order remain
the shared script. A scripted refused command cannot invent an admission.
Observer abort/timeout/disconnect can lose a response while scripted backend
progress continues; it is never a backend cancellation command. Backend
cancellation is an explicit protocol command. The client retains exact commit
input for deliberate replay and bounds pending requests, replay records,
subscriptions and recent IDs. Replay retention must be explicitly released.

Observation reconciliation uses BigInt decimal counters, stream/epoch identity,
authoritative snapshot watermarks, buffered events and stable operation/draft
captures. Gaps, duplicate events, regressed/reused revisions, changed captures,
partial inventories and omitted pending work require resnapshot. A recorded
event without an authoritative anchor cannot establish readiness. The shared
work context retains target binding and pending draft navigation; only exact
backend Save/Discard outcomes release edits. The workbench displays those
observation boundaries and developer frame data rather than claiming a finished
player-facing workflow.

BR03-04: the production Vite graph must include the common client seam and
exclude mocks, scenarios, fixtures, tests and native/browser test automation.
The browser suite inspects actual emitted assets for shared mock/control
markers. It launches the explicitly selected project-local headless shell and
records its pinned package metadata, actual version and executable hash before
and after the journeys. No global browser or user browser profile is used.
The current schema bundle causes a visible Vite chunk-size warning; performance
and complete UX review belong to later br-25 and installed client gates.

`mock-contract` runs generated drift, all golden guards, fixture typing, Svelte
typing and the actual client/mock/frontend tests. Machine-readable Node/Vitest
inventories must account for required files, named criterion tests and every
full fixture ID with no skipped/todo/pending tests. Output/install/target
ancestry is inspected before execution; real junction regressions cover these
guards. Cleanup failure cannot skip the owned development server teardown.
`mock-development` runs an
actual browser and checks production output. Both suites are required for br-03
acceptance through the owning-root LexRunner projection. Receipts are local
source-bound observations; they are not signed review, a Runner Attempt,
native execution or release authority.
