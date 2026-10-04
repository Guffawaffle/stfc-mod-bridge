# New Bridge development

The foundation belongs to br-01, issue #234. Protocol work belongs to br-02,
issue #236. The typed frontend/client workbench belongs to br-03, issue #237;
the operation kernel belongs to br-04, issue #238. Full product UI and native
domain behavior remain later packages.
Foundation compilation and dependency-boundary tests do not qualify those
later behaviors or accept an incomplete protocol/scenario catalog.

Use Node 24.14.1, pnpm 11.19.0 and Rust 1.99.0. Exact direct versions are in
`dependencies/next-toolchain.json`; Cargo.lock and the root pnpm-lock.yaml retain
the complete graphs. TypeScript 6.0.3 matches svelte-check's stable API. The
root-owned pnpm workspace uses one root lockfile, superseding the planning
sketch's ui-only lockfile location. `.cargo/config.toml` bounds build concurrency.

Install the normal Windows MSVC/SDK/WebView2 or Apple Silicon Xcode prerequisites
from https://v2.tauri.app/start/prerequisites/. Install the pinned Rust toolchain
through rustup. This Windows checkout also supports its ignored local toolchain
under artifacts/toolchain; the cargo/desktop wrappers bind its explicit homes
without changing the user's PATH. Other clean hosts use their standard rustup
installation and the tracked toolchain pin.

Both wrappers enforce the tracked Rust pin, native target and owning target
directory in their child environment. Custom compiler wrappers are rejected.
The foundation observes actual rustc/Cargo versions and selects the executable
from this Cargo invocation's compiler-artifact output, rather than hashing a
possibly stale default-path binary. Its release-mode build explicitly enables
custom-protocol to embed frontend assets; dev mode continues to use Vite.

From the owning Bridge checkout, bootstrap once:

```text
npx --yes pnpm@11.19.0 install --ignore-scripts
```

Use the pinned workspace CLI through the current Node executable, avoiding a
global pnpm shim that selects another Node runtime:

```text
node scripts/next/pnpm.mjs --dir ui dev
node scripts/next/pnpm.mjs --dir ui check
node scripts/next/pnpm.mjs --dir ui test
node scripts/next/pnpm.mjs --dir ui build
```

Frontend development/build commands require neither Rust compilation, a native
library nor a game. Open `/scenarios/index.html` on the Vite server for the
shared scenario workbench. Its typed client and mock delivery controls use
the same checked-in wire contract as the unbound production application.
See [FRONTEND_CLIENT.md](FRONTEND_CLIENT.md) for its exact proof boundaries.

```text
node scripts/next/browser.mjs install
node scripts/next/mock-contract.mjs
node scripts/next/mock-development.mjs
```

The browser installer uses the pinned Playwright headless shell in this
checkout's ignored artifacts tree. Native Rust and game dependencies are not
part of this frontend development route.

For the native shell and core checks:

```text
node scripts/next/desktop.mjs dev
node scripts/next/cargo.mjs fmt --all -- --check
node scripts/next/cargo.mjs clippy --workspace --all-targets --locked -- -D warnings
node scripts/next/cargo.mjs test --workspace --all-targets --locked
```

BR01-01: the foundation gate executes pinned frontend tooling and locked Rust
commands, and retains exact command/host/output observations.

BR01-02: actual Cargo metadata is traversed to reject direct or transitive
Tauri/webview/renderer dependencies from contracts, domain and engine. The
desktop shell may depend on those packages; its dependencies cannot contaminate
the core closure.

BR01-03: embedded @wdio/tauri-service 1.4.0 and matching test plugins 1.4.0 are
selected and pinned. The plugins are not registered, granted capabilities or
compiled into this production shell. Native tests await later implementation
and supply-chain remediation in #235. Instrumented native UI evidence and signed
production package smoke tests remain separate.

BR01-04: a standalone Vite server starts without Rust or a game, serves its
transformed browser entry, and exits as an owned child. Typechecking, frontend
tests and production build use the same frontend-only route.

BR01-05: next-foundation.yml declares Windows x64 and native Apple Silicon jobs;
each job rejects a mismatched actual host and retains exact receipts and unsigned
unbundled shell bytes. Defining that workflow does not mean its Mac job ran.
Local Windows shell compilation, actual Mac compilation, native ABI, installed
game/runtime, accessibility and release qualification retain distinct boundaries.

Execute prerequisite and package acceptance through a current owning-root plan:

```text
node scripts/next/make-runner-plan.mjs --package br-00
lexrunner --no-emit-frames gate run --plan artifacts/next/plans/br-00.plan.json --artifact-dir artifacts/next/runner/br-00 --timeout 150000 --max-level 0 --keep-cache --json
node scripts/next/make-runner-plan.mjs --package br-01
lexrunner --no-emit-frames gate run --plan artifacts/next/plans/br-01.plan.json --artifact-dir artifacts/next/runner/br-01 --timeout 930000 --max-level 0 --keep-cache --json
node scripts/next/make-runner-plan.mjs --package br-02
lexrunner --no-emit-frames gate run --plan artifacts/next/plans/br-02.plan.json --artifact-dir artifacts/next/runner/br-02 --timeout 990000 --max-level 0 --keep-cache --json
node scripts/next/make-runner-plan.mjs --package br-03
lexrunner --no-emit-frames gate run --plan artifacts/next/plans/br-03.plan.json --artifact-dir artifacts/next/runner/br-03 --timeout 630000 --max-level 0 --keep-cache --json
node scripts/next/make-runner-plan.mjs --package br-04
lexrunner --no-emit-frames gate run --plan artifacts/next/plans/br-04.plan.json --artifact-dir artifacts/next/runner/br-04 --timeout 630000 --max-level 0 --keep-cache --json
```

Each invocation contains one real package gate; execute prerequisites first.
The locally adopted LexRunner candidate now also exposes durable MCP gate
operations. Generate the same owning-root projection, then call `gates_start`
with `repoRoot` set to this canonical checkout, the absolute `planFile`, a fresh
owned `outDir`, and an explicit idempotency key. Retain its `operationFile` and
`operationSha256` for `gates_status`. If the start acknowledgement is lost, retry
that same request/key; do not invent another producer. Status observation can
outlive the caller connection. It does not authorize merge or release. See
`TOOLING_ISSUES.md` for the installed candidate and actual restart evidence.

The generator refuses a dependent projection until current prerequisite receipts
validate, and records their references in the adjacent projection artifact.
Dependencies remain authoritative in work-packages.json and are revalidated by
the package dispatcher. These projections are command-gate inputs, not merge
plans, and contain no fake passing ancestor nodes. This explicit sequencing
avoids assuming that gate-mode execution waits on item dependency edges.
The generator
rejects unimplemented suites, wrong owners/hosts and unbound issues. It resolves
the current checkout cwd instead of reusing a machine-specific scheduling plan.
The dispatcher validates complete current-head prerequisite receipts before
and after dependent work. These are local observations, not signed review,
ADR-010 Run/Attempt acceptance or merge authority.

Production dependency audit is a required foundation check. The development
automation audit has actual remaining advisories tracked in #235: unavailable
patched extract-zip/braces releases. Available serialize-javascript/basic-ftp
fixes and WDIO peer alignment are pinned as overrides. This disposition permits
foundation engineering while keeping native automation/release qualification
pending. It does not label the complete development dependency graph clean.

The retained WPF workflows are unchanged. The new foundation workflow has no tag,
publish, signing, environment promotion, game deployment or process-cycle step.
