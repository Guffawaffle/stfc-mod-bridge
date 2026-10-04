# Home/Shuttle Bay integration

Use `Home` from this directory with the one `BridgeFacade` provided by the app.
`facade` is optional when `provideBridge` has already installed that context.
The component and its read controller capture one facade lifetime; wrap all
screen/navigation components in `{#key facade}` when replacing a development
scenario. Destroy the old facade and transport explicitly at reset.

Root composition owns connection and routes. Connect `facade.session` through
`facade.connect()` to populate the shared snapshot, rather than treating hello
as an inventory. Compose `WorkspaceNavigation`, one global `UnsavedChanges`,
and one global `ActionReview` from `ui/src/navigation`. View navigation preserves
the current draft. `Use target` calls the existing facade target transition;
dirty changes are applied only by its exact completed Save or confirmed Discard.

`Home` accepts optional `onreview(intent, focusKey)` and
`onmaintenance('game' | 'runtime' | 'bridge')` routing hooks. The default review
uses `facade.actions.prepare`. The maintenance defaults navigate to Engineering
or Preferences; root can route to the specific management screen. The shared
action API is `prepare`, `confirm`, `replay`, `reconcile`, and `stay`; availability
is a backend observation and does not grant execution permission or a lock.

Stable accessible controls are `Installation`, `Profile`, `Use target`,
`Review launch`, `Isolated data mode`, `Unrecognized runtime`,
`Check focus for session N`, `Review focus for session N`,
`Confirm action`, `Replay exact action`, and `Refresh action outcome`.
Dirty navigation exposes `Save`, `Confirm Save`, `Discard`, and `Stay`.
Session N is an inventory row label; the submitted session remains the exact
PID/start/executable/session binding from that row, independent of the selected
profile or its next-launch preference.

Mock setup supplies an initial shared snapshot. Explicit target application
queries `resolve_target`, then `get_actions` for one ordinary/isolated action
against the resolved target. Explicit session inspection queries `get_actions`
for `focus_session` against that exact session. No first item is selected.
The isolated store mode and optional per-attempt runtime consent are explicit
choices; preparation remains the backend's decision. Script request frames must
preserve the observed installation/profile revision assertions supplied by the
screen. The shared scenario catalog remains unchanged; root owns route-specific
fixture composition and provenance.

Focused tests use the real typed ScenarioTransport/ManualClock, accepted shared
catalog entries and schema-validated compositions/failure injections. They prove
portable review, custody, observation and SSR semantics. They do not prove
browser layout, native webview accessibility, producer execution, game behavior,
an account identity, Windows/Mac native equivalence, or release qualification.
Root owns coordinated browser proof and qualification registration.
