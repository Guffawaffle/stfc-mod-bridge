# Historical Windows Launcher Handoff Frames

> [!IMPORTANT]
> This is a migrated index of planning-era Lex frames. Every status and next
> action below is captured historical state, not a current instruction. Current
> product and release authority is listed in
> [CURRENT_AUTHORITY.md](CURRENT_AUTHORITY.md); live work is issue-backed in
> `Guffawaffle/stfc-mod-bridge`.

This was the branch-visible index for durable Lex frames created while planning
and orchestrating `Guffawaffle/stfc-mod#182`. The file preserves stable IDs and
the intended historical sequence; it must not be used to recover a current
branch, merge target, work-item state, or release decision.

| Sequence | Role | Frame | Outcome | Next action |
|---:|---|---|---|---|
| 1 | Foreground orchestrator | `frame-1785006342138-93252cdc-5507-4c7b-9440-a53c657bdbd8` | Codex/LexRunner setup validated, dogfood issues filed, and delivery-base conflict isolated. | Collect independent plan and base audits. |
| 2 | Plan-audit worker | `frame-1785006556980-05886fbd-e6c7-48e2-8e16-8790c8e9f884` | All 10 issue mappings and DAG edges agree; five waves are exact. Governance and gate-enforcement gaps block dispatch. | Resolve base, worker isolation, readiness labels, and acceptance enforcement. |
| 3 | Base-audit worker | `frame-1785006561899-3bf06cc9-b6a4-4a5d-a4a6-279cf36273e2` | `origin/main` is 33 commits newer with the same tree; `upstream/dev` omits 335 fork commits. | Record fork-main policy and fast-forward the planning branch. |
| 4 | Foreground orchestrator | `frame-1785007005652-7dbb403d-5241-4a0a-85a9-fad2c9dbc6fe` | Planning committed at `64d2c0f` and pushed; fork-main policy recorded; all launcher items remain pending and undispatched. | Pivot to user feedback; authorize isolation and dispatch WL-001 only when the sprint resumes. |
| 5 | Foreground orchestrator | `frame-1785125569518-2de46ced-7c86-44f7-b2e5-720650555601` | WL-001 dispatched in the foreground workspace; self-contained WPF shell, core boundaries, tests, package, 125% render, and Windows CI job are implemented. Codex `WINDIR` friction is filed as `openai/codex#35545`. | Direct gates passed after LexRunner could not discover the explicit plan; publish the branch/PR, then collect clean-runtime and 100%/150%/200% manual evidence. |
| 6 | Foreground orchestrator | `frame-1785126039923-c7a31132-fed3-4ab5-a344-56cf1f1371e5` | WL-001 is committed at `955fc83` and published as draft PR #198 with launcher, package, native, and policy gates green. | Accept the CI artifact and collect clean-runtime plus 100%/150%/200% DPI, keyboard, and screen-reader evidence. |
| 7 | Foreground orchestrator | `frame-1785129383575-884820b0-a639-43bb-b81c-6e834535e91e` | WL-001 architecture direction accepted; Wave 2 promoted; WL-002 implements bounded official-settings/conventional/manual discovery, exact `prime.exe` validation, confirmed selection, and composable health. The real custom game path passed the visible UI smoke. | Complete gates and diff review, then publish WL-002 as a draft PR stacked on #198. |
| 8 | Foreground orchestrator | `frame-1785131509443-591c7f02-808c-4e51-8179-37facec9dadf` | Product UX direction accepted: compact outcome-first Home, adaptive schema-driven Settings workspace, first-class notification configuration, progressive redacted diagnostics, streamer-safe paths, and System/Light/Dark themes. LCARS is no longer a requirement. | Resume WL-002 using `UX_DIRECTION.md` as the UI contract; retain the internal health model while replacing the diagnostic-first presentation. |
| 9 | Foreground orchestrator | `frame-1785133003664-9aca8895-8138-4d76-86d7-0d82149768f6` | WL-002 now implements the accepted compact Home with consumer-facing state resolution, real Set/Confirm/Change and Refresh actions, privacy-safe rendering, Light/Dark palettes, native title-bar theming, About, and accessibility semantics. Packaged and live UI smokes passed with no path or diagnostic-detail leaks. | Publish the update to draft PR #199, collect review, and merge WL-002 when its existing discovery and UI gates are satisfied. |
| 10 | Foreground orchestrator | `frame-1785134447036-b398a5bb-6b1e-42f8-ac36-271555d2c5d4` | Player visual review refined WL-002: one integrated branded title area, accessible caption controls with Snap Layout hit-testing, no redundant healthy `Set` label, stable product copy, and explicit filled/hollow client status lights. Future launcher preferences and named launch profiles are separated from schema-driven mod settings. | Publish the refinement to draft PR #199, collect review, and split dedicated preferences/profile PM work before implementation. |
| 11 | Foreground orchestrator | `frame-1785136412447-0c832d85-be50-4a33-a417-896484022229` | Blue-tape review hardened WL-002 with unprivileged event-driven game status, reusable accessible dialog and utility-action primitives, corrected custom chrome, stronger typography and WCAG AA palettes, and Windows identity derived from the approved macOS artwork. | Publish the hardened interactions to draft PR #199, update WL-002/WL-010 evidence, and observe one natural game shutdown to complete the event lifecycle smoke. |
| 12 | Foreground orchestrator | `frame-1785136955081-6ef0a744-0242-44a9-ac76-6b9218ae9c94` | Refresh feedback is an unresolved reusable action-state contract in #201, while launcher/sidecar convergence is a planning-only product-family direction in #202. Neither expands or blocks WL-002. | Discuss and select the action-feedback contract and a staged product model before either planning issue is marked ready or dispatched. |
| 13 | Foreground orchestrator | `frame-1785229655811-93652878-daa2-448b-bebb-39b40594b41e` | Home and Settings reached player-approved visual cadence on WL-006: unified title-bar navigation, compact category rail, accessible drag scrolling, conditional non-overlapping Save/Discard bar, and bounded Settings geometry. The current scalar-boolean adapter stages and atomically saves 82 settings; specialized input families remain deliberately separate. | Push the approved implementation commits and this handoff to PR #207, then resume the input-gap sprint without reopening the accepted shell. |
| 14 | Foreground orchestrator | `frame-1785296605899-526a9f67-5d7a-4c13-91f6-0bf587747851` | WL-006 now includes the schema-derived 90-binding Hotkeys adapter at `1fa5c3f`: keyboard/mouse capture, replace/add alternatives, explicit unbind/default behavior, strict normalization, and runtime group/trigger conflict checks. Schema, 111 launcher tests, build, format, package, and non-mutating UI smoke passed. | Collect manual Change/Add/Unbind and real-conflict smoke, then group hotkeys/notifications by runtime family and complete a disposable-file round-trip acceptance pass. |
| 15 | Foreground orchestrator | `frame-1785297185714-ba9e4f8d-7daf-47e1-9366-2ba5d2bf052f` | Hotkey interaction cadence was refined at `d38dda5`: alternatives are wrapping, individually removable chips; Add, Unbind, and Use default are compact inline actions; the misleading Change path is gone. Accessible chip names include their owning action. Remove, unbind, reset visibility, discard restoration, and untouched live-TOML behavior passed automation smoke. | Collect player visual and physical capture smoke, then proceed to runtime-family grouping and disposable-file round-trip acceptance. |
| 16 | Foreground orchestrator | `frame-1785298426075-08347347-bcd1-4c21-abdd-8e0d66fb39d9` | Lex's settings review was reconciled at `d3cd708`: WL-006 now explicitly separates authoritative runtime schema from generated player presentation, targets a borderless full-row renderer, standardizes semantic Fluent icons and additive focus states, and preserves the already accepted launcher shell. | Implement the generated presentation envelope and semantic `AppIcon` foundation, then weave existing scalar, notification, and hotkey adapters into the borderless renderer. |
| 17 | Foreground orchestrator | `frame-1785298662092-201a3364-49cd-40ad-8ad0-1dac9344f591` | Lex's concept art was reconciled at `470eac5`: the renderer direction is accepted, the title-area theme dropdown must show and mark its selected System/Light/Dark mode, future LCARS-like visual styles remain a separate axis, and the conditional dirty bar visibly summarizes when all staged changes take effect. | Implement presentation metadata and semantic icons, including selected appearance state and apply-timing aggregation, before weaving the existing adapters into the borderless renderer. |
| 18 | Foreground orchestrator | `frame-1785300853273-5f502d60-dd85-4b17-9e76-e871b7d9e6ea` | The five-node LexRunner uplift is complete at `e1be2ec`: generated player presentation, semantic Fluent icons, selected System/Light/Dark appearance, explicit override/apply state, borderless accessible rows, and a packaged non-mutating UI smoke are integrated. Schema checks, 120 launcher tests, warning-free Release build, packaging, UI Automation, and live-TOML hash preservation passed. | Let Guff perform the player visual pass on the packaged launcher, then push the clean branch and update PR #207. |
| 19 | Foreground orchestrator | `frame-1785304784042-2f5c6400-4322-4513-bed5-45879b3fc4a3` | WL-006 now separates schema defaults, saved values/override presence, and draft values/override presence at `067e1ae`; quiet Custom and transient Unsaved states, per-row revert, clear-override defaulting, humanized Data Sync options, independent notification channels, deliberate focus, and dirty-only Save/Discard are integrated. Schema 19/19, launcher 121/121, warning-free Release build, packaging, UI Automation, and live-TOML hash preservation passed. LexRunner consumed the tracked plan for status and merge order, but `gates_run` could not accept the same plan path. | Let Guff perform the final player visual pass, then push/update PR #207; separately deduplicate or file the LexRunner `gates_run` plan-path contract failure. |
| 20 | Foreground orchestrator | `frame-1785306538312-d86c4674-9d58-472a-b7fe-554f64835605` | WL-006 now has a startup-latched compatibility seam at `4e26e69`: positive runtime-manifest detection, normalized capabilities, product feature policy, immutable activation decisions, principal semantic grouping, and alphabetical fallback compose before the settings renderer. NetniV eligibility is capability-driven rather than owner-branched. Schema 20/20, launcher 132/132, formatting, warning-free Release build, packaging, activation-aware UI Automation, and live-TOML hash preservation passed. | Let Guff review About diagnostics and the semantic layout, then push/update PR #207. When release-source installation lands, point the detector at the selected installed runtime without changing activation or provider contracts. |
| 21 | Foreground orchestrator | `frame-1785308344678-ad9c7ec9-e93d-44f4-b238-992427f90d30` | WL-006 Hotkeys now use visible player-facing group landmarks and explicit approved family metadata. Saved zoom bindings render as one compact family, generated boilerplate help is removed, `Add binding` lives in a themed overflow menu, and focused key capture uses a separate accessible popup. Schema 21/21, launcher 132/132, formatting, warning-free Release build, package, activation-aware UI Automation, and live-TOML preservation passed. | Let Guff visually review the packaged Hotkeys page, then push/update PR #207 when accepted. |
| 22 | Foreground orchestrator | `frame-1785311539056-80a99508-ae80-4e50-a4a2-1d51a47f44e9` | The final modular configuration boundaries are locked and documented. The first extraction gate isolates startup/game-process events from Settings document reloads through a narrow lifecycle controller; deterministic counters prove zero Settings reloads for those transitions. Launcher tests 135/135, warning-free Release build, formatting, package/UI Automation smoke, and live-TOML integrity pass. | Extract visible-section flattened projections beneath the accepted UI and instrument construction counts; opening one section must construct zero unrelated WPF rows while domain-level hidden-hotkey conflict coverage remains complete. |
| 23 | Foreground orchestrator | `frame-1785312468875-637a4efd-d7c7-465b-9024-f5965eda305e` | WL-006 replaces eager construction of all 332 row ViewModels and WPF framework grouping with one flat active-section/search projection. Projection snapshots record constructed paths and header counts; raw invalid text survives row recreation through a stable editor draft store; hotkey validation reads all catalog commands even when search hides the conflicting row. Core tests 138/138, WPF tests 3/3, warning-free Release build, formatting, package/UI Automation smoke, and live-TOML integrity pass. | Introduce the document-scoped `ConfigurationWorkspace` seam beneath the current Settings ViewModel, separating domain catalogs and typed edit sessions from editor drafts and repository commit coordination without changing the accepted WPF presentation. |
| 24 | Foreground orchestrator | `frame-1785369786538-8518865c-fbf7-46a1-8bcf-23527cd9375a` | WL-006 now routes the active TOML through a revisioned document workspace and repository boundary. Typed semantic changes, sparse atomic commits, optimistic conflict preservation, delayed baseline advancement, and batched structural events are covered by regression tests; 147 launcher tests, warning-free Release build, formatting, packaged UI Automation, and live-TOML integrity pass. | Extract a narrow Settings application controller and move stable editor drafts behind it, then split scalar, hotkey, notification, and sync-topology edit sessions without changing the accepted WPF presentation. |

## 2026-07-28 end-of-night resume packet

### Exact state

- Branch: `feature/wl-006-config-editor`
- Accepted implementation checkpoint:
  `820c7ab` (`Polish launcher navigation and contextual actions`); this handoff
  commit follows it.
- Remote state: three approved implementation commits plus this handoff commit
  are ahead of `origin/feature/wl-006-config-editor`; the working tree is clean.
- Pull request: [#207](https://github.com/Guffawaffle/stfc-mod/pull/207) is open
  with its currently published checks passing. It does not yet include the
  final implementation or handoff commits.
- Validation: Release build clean, `69/69` launcher tests passing,
  `dotnet format --verify-no-changes` clean, and `git diff --check` clean.
- Last packaged executable:
  `windows-launcher/artifacts/win-x64/app/STFCModBridge.exe`.

### Accepted UI contract — do not casually reopen

- Home and Settings share one title-bar cadence:
  `[gear] Settings | STFC Community Mod` on Home and
  `[< Home] | STFC Community Mod` in Settings.
- The Settings rail is compact; Search is a remembered title-bar action.
- Mouse drag scrolling, bounded flick momentum, outside-window capture, themed
  scrollbars/tooltips, and reduced-motion behavior have passed player smoke.
- Save and Discard live in a dedicated bottom layout row that appears only
  while edits are pending. It never overlays a setting, and no empty footer is
  shown otherwise.
- Settings cannot be resized below the width needed by the current editor
  layout.
- About and manual Refresh were removed from Home. About remains a Settings
  destination; status refresh is expected to become event-driven product
  behavior rather than a consumer-facing button.

### Recommended first hour

1. Confirm branch, clean state, and the four commits ahead of origin.
2. Run the narrow launcher build/tests once if the machine or tooling changed
   overnight.
3. Push `feature/wl-006-config-editor` so PR #207 contains the accepted UI.
4. Review the PR diff and any Copilot feedback before changing the editor
   architecture.
5. Continue the input-gap sprint one bounded adapter at a time:
   ordinary non-boolean scalars, generated hotkey bindings, and union-typed
   notification policies remain distinct catalog producers behind one schema
   contract.

### Product backlog reminders

- Add first-class release-source selection for Guffawaffle and NetniV. NetniV
  must retain TOML compatibility even if Guffawaffle later gains a richer
  launcher-owned representation.
- Implement the accepted notification experience without flattening meaningful
  event prefixes or losing default/alias/provenance behavior.
- Keep direct game launch and official game update support in the future-state
  plan; this should eventually replace the ordinary STFC launcher path rather
  than merely wrapping it.
- Later launcher preferences include Windows startup, automatic update checks,
  launch profiles, theme, and remembered UI state.
- Launcher/sidecar convergence remains an intended direction requiring product
  discussion, not permission to merge the products opportunistically.
- Preserve modular, accessible controls and reuse approved macOS artwork.
- Resume Azure/GitHub OIDC and protected release-environment hardening before a
  public signed release.

### Input wanted from Guff

Nothing blocks the next engineering slice. When convenient, the highest-value
inputs are:

- choose which editor family should follow booleans first; the recommended
  order is ordinary scalars, hotkeys, then notifications;
- provide or point to representative, privacy-scrubbed NetniV and Guffawaffle
  TOML files with real customizations for migration/preservation fixtures;
- flag any must-have launcher preference or profile behavior before that schema
  is frozen;
- provide the highest-resolution original mod icon only if a better source than
  the approved macOS asset becomes available;
- be available later for interactive Azure/GitHub authentication or protected
  environment changes—those should not be attempted unattended.

No personal/admin action is needed overnight. Rest is the dependency.

## Historical handoff contract at capture

Each worker handoff must report:

- objective and non-goals;
- branch, base commit, and dirty-state snapshot;
- work-item and GitHub issue IDs;
- files touched and artifacts produced;
- commands, tests, gates, and outcomes;
- decisions, assumptions, blockers, and risks;
- recommended next action;
- stable frame ID and idempotency key.

At capture time, `workspace/unscoped` was a temporary module sentinel because
the former workspace had no loaded Lex policy. That statement is historical:
operator-local Bridge continuity may now load a repo-scoped policy, but local
memory configuration is not Git product or release authority. The original
policy and automatic unscoped behavior were tracked in `Guffawaffle/lex#800`.
