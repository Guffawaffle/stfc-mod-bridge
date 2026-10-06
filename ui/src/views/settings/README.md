# Schema-driven configuration workspace

`Settings.svelte` renders the provider's active schema through `SchemaField.svelte`.
Categories, search terms, field kinds, numeric limits, enums, key choices,
notification sounds, default values, platform support and apply timing come from
the typed schema. The view contains no TOML feature registry.

`SettingsController` reads the explicitly selected target through the typed client.
An observed document must match that selection and schema before it can supply
saved values. An unobserved baseline remains unknown. Refreshing the same bound
document retains its existing draft; an external binding change is passed to the
shared draft reconciliation path.

Each edit replaces one participant in the complete shared edit set. Removing an
override stages `remove_override`; it does not write a default into the document.
Settings and Data Sync use the same `WorkContext` and in-place Save/Discard
review. Failed synchronization, Save or reopening does not silently discard the
current editing state. The application owns the global confirmation dialog and
target-transition Save/Discard/Stay handling.

Incomplete numeric text belongs to the shared public-input custody buffer. It
retains exact decimal precision, marks the draft dirty across view navigation,
blocks Save and requires completion or deliberate Reset. Protected inputs are
captured by `BridgeFacade.captureProtectedInput` through the backend entry port.
The frontend holds opaque handles and status only; it renders no endpoint, token,
proxy, password or private path contents.

Native inputs/selects/fieldsets provide keyboard semantics. Each input has a
visible label and explanatory validation text. Save/Discard opener registration
supports focus restoration through the shared facade. Component/browser checks
are separate from Windows Narrator and Apple Silicon VoiceOver qualification.
