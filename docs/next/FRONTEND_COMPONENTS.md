# Shared frontend components

The browser gallery at `/gallery/` composes the same Svelte primitives, tokens
and shared facade used by the production shell. It runs without Rust, native
libraries or a game. Shared strict protocol fixtures and the deterministic
ScenarioTransport supply declared outcomes. Gallery and mocks are rejected
from the production dependency graph and emitted application assets.

| Criterion | Evidence |
| --- | --- |
| BR12-01 | System/light/dark tokens; semantic native button, field, select, navigation, notice, progress, target summary and dialog. Actual primitive semantics and 20 contrast comparisons plus browser samples. Unknown/stale progress is indeterminate; admission is never displayed as completed Save. |
| BR12-02 | Native modal keyboard focus exclusion/trap, Escape/Stay and connected-opener restoration; busy requests retain ownership. Browser keyboard, forced colors, reduced motion, long labels, validation and 200% text checks. Windows/macOS shell presentation is chosen at the thin shell; operational policy remains in the backend. |
| BR12-03 | Frozen facade/context reuses the accepted WorkContext, ObservationStore and typed client. Exact staged-edit acknowledgment, distinct review/explicit commit, uncertain exact replay, confirmed/refused Discard and authoritatively completed old-draft reconciliation retain queued navigation correctly. Context has one target/draft/operation/navigation owner. |
| BR12-04 | Actual component assertion inventory, private pinned browser journeys, retained screenshots and independent behavioral/UX source review. Layout/copy/style remain available through Vite hot reload. Screenshots alone do not approve behavior. |

`Shell` supplies the semantic page landmarks, skip link, navigation and live
announcements. The facade's focus controller accepts registered local keys,
never selectors supplied by a backend. Its bounded announcements contain
sanitized outcomes. Component layouts contain no filesystem/process policy.

The backend acknowledges staged edits before preparation. Only the current,
same-binding/schema, exact-edit acknowledgment can advance a draft revision;
foreign, stale, refused or uncertain output retains local intent. Save presents
a review before a separate Confirm Save action. Admission preserves pending
navigation until a completed operation for that exact reviewed draft arrives.
An uncertain commit retains its exact replay input, with no implicit retry or
new preparation. A sent domain refusal has no admission-disposition proof and
therefore retains uncertain custody, including a refusal received during replay.
Only this facade's identical retained capture/input can be retired after exact
terminal reconciliation or deliberate abandonment of a proved-unsent submission.
Unknown delivery, preexisting keys and replaced captures retain their replay.
Stay abandons navigation intent while keeping local edits;
Discard requires an exact backend receipt.

The gallery exposes separate declared Save/Discard modes because its fixtures
are scripts, not a second policy engine. Reset disposes owned observers and
generation guards prevent stale requests from changing a new session. The
scenario workbench remains separate and covers the full shared catalog.

The `frontend-components` and `frontend-accessibility` gates record actual test
counts/criteria, exact browser identity and source/fixture inputs. These gates
qualify this reusable browser layer with synthetic backend outcomes. Installed
Tauri webviews, native window behavior, Narrator, VoiceOver, finished product
screens and release qualification still require their later work packages.

The browser suite has 28 required sampled checks and retains 22 screenshots;
the component suite binds all 89 actual assertions and six evidence regressions.
Its development-only CSS hot-reload probe changes one guarded gallery file,
observes computed style without navigation or lost draft/form context, then
restores the exact original bytes in `finally`. It never changes production
primitives. Automated checks are bounded samples, not a comprehensive
accessibility audit. Text scale is CSS root font at 200%, not full browser zoom.
