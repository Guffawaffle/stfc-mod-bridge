# Windows Launcher Configuration Editor

Status: v1 implementation complete; packaged qualification remains in issue #30

## Contract

The launcher presents one Settings experience while delegating value behavior
to scalar, keybinding, and notification-policy adapters. The generated schema
is the catalog authority; the mod runtime remains the TOML parser and default
authority.

The selected release source chooses the matching schema and capabilities.
Guffawaffle and NetniV are both packaged through stable provider IDs. NetniV
uses a provider-owned versioned schema set that resolves only an exact reviewed
provider, track, release version, and full source commit. The current stable
binding is NetniV `1.1.4` at `d912611fa1eca49fc54f363bdf8377dfebf8def0`;
the captured dev `1.1.5.1` catalog remains unavailable unless a selected release
matches its exact reviewed identity. Unreviewed releases fail closed instead of
reusing Guffawaffle or adjacent NetniV metadata. TOML remains the compatibility
boundary. Runtime facts, capabilities, feature policy, and startup activation
are kept separate by the
[runtime activation contract](RUNTIME_ACTIVATION.md).

Source selection does not rewrite configuration and does not establish which
provider produced the installed DLL. A destructive installed-mod source switch
uses the protected exact-byte backup contract in
[`MOD_DEPLOYMENT.md`](MOD_DEPLOYMENT.md#protected-toml-backup-contract). That
backup is not the editor's adjacent transactional `.bak`: it is encrypted,
retained, restored, and excluded from support evidence under a separate
source-transition journal. Broad or inferred TOML migration remains out of
scope. Diagnostics may separately offer explicit catalog-authorized cleanup for
one valid alias move or one redundant alias removal after a redacted preview.

## Accepted modular architecture boundary

Startup resolves behavior before the configuration workspace or WPF
presentation is composed:

```text
RuntimeDetector
  -> immutable ActivationPlan
  -> selected provider modules
  -> ResolvedConfigurationCatalog
  -> document-scoped ConfigurationWorkspace
  -> application controllers and queries
  -> flattened visible WPF projections
```

The activation plan selects the Guffawaffle or NetniV providers, grouped or
alphabetical layout, and legacy or composable Data Sync implementation. It is
read-only diagnostic evidence after composition. Downstream models,
controllers, and views do not repeat owner checks or branch on feature flags.

`ResolvedConfigurationCatalog` is an aggregate of domain catalogs:

- `ScalarSettingsCatalog`;
- `CommandCatalog`;
- `NotificationCatalog`;
- `SyncTargetTypeCatalog`;
- `ResolvedLayoutCatalog`.

Catalog composition validates one owner per canonical persistence path, rejects
canonical-path and alias collisions, and treats stable IDs as durable identity.
Conflicts fail closed before a workspace is created. Layout metadata references
domain nodes but does not define their semantics; approved legacy families are
presentation projections only.

`ConfigurationWorkspace` is the document-scoped logical unit of work, not the
storage implementation. It coordinates domain-specific edit sessions for
scalar settings, hotkey commands and binding collections, notification events
and delivery policies, and desired sync topology. The sessions share tracked
draft, validation, change-set, save, and discard primitives without being
flattened into a universal setting model.

The workspace prepares one semantic `ChangeSet`.
`IConfigurationRepository` maps those semantic changes to a sparse TOML
document and atomically commits against an expected baseline revision. No
session advances its saved baseline until the complete active-document commit
succeeds. A failed commit leaves every draft unchanged and dirty. This
atomicity contract covers the active configuration document only, not future
external stores such as credentials.

External-change detection, revision, stale state, and conflict state belong to
the workspace. Application controllers own user-facing replacement policy and
decide when to ask for reload, rebase, discard, or overwrite; the workspace
does not choose those actions independently.

Desired sync configuration remains separate from the immutable
startup-resolved `ResolvedSyncPlan`. `SyncTargetTypeCatalog` defines available
target types, while `SyncTopologyEditSession` owns desired target instances,
feeds, and routes. The editor may validate a candidate plan, but saving never
mutates the active runtime topology before restart. The composable Data Sync
module receives its own topology view instead of using the generic scalar row
renderer.

Raw invalid editor input is presentation/application state, not domain state.
An `EditorDraftStore`, keyed by stable field or action ID, preserves raw text
and parse issues across WPF projection disposal. Domain sessions receive typed
values only after parsing. Commit eligibility combines editor parse issues,
domain validation, and workspace-level cross-domain validation.

Domain value semantics, editor input behavior, and TOML persistence remain
separate:

- domain semantics normalize, compare, and validate typed values;
- editor adapters parse raw input and format editable or display text;
- TOML infrastructure encodes, decodes, performs sparse writes, and preserves
  unknown keys and comments.

The canonical tracked-draft operations are `StageOverride(value)`,
`ClearOverride()` (shown as **Use default**), and
`RevertDraftToSaved()` (shown as **Revert changes**). Dirty comparison includes
override presence as well as effective value.

Application controllers coordinate document selection, load, save, discard,
external-change handling, and lifecycle. Domain sessions retain transitions,
validation, conflicts, and dirty state. Query/projection services own search,
taxonomy projection, and display summaries. Workspace change events identify
changed, added, and removed stable IDs, summary changes, validation changes,
query/layout invalidations, and the workspace revision. Save, discard, and
document replacement publish one batched transition rather than per-row
events.

WPF uses typed domain-specific requests and shared presentation commands
without recreating a universal configuration model. The visible page is
projected into one flattened list of group headers, family headers, and typed
rows rendered by a single recycling items host with no nested scrolling or
framework grouping. Section projection counts must prove that opening one
section constructs no unrelated WPF rows. Domain-level hotkey conflict
coverage, including hidden commands, must not depend on which projections are
currently materialized.

The first migration gate is lifecycle isolation: a game process change updates
Home and causes zero Settings reloads. Subsequent extraction must preserve raw
invalid input across projection disposal/recreation, publish granular batched
workspace changes, perform no idle polling, and measure both section projection
counts and opening time before physical assembly separation.

## Implemented foundation

- The packaged launcher embeds and fail-closed loads schema version `1.0.0`.
- The packaged NetniV schema set materializes stable `1.1.4` and dev `1.1.5.1`
  catalogs from shared metadata plus reviewed deltas. Runtime status, feature
  gates, release identity, and full source SHA remain catalog data rather than
  WPF inference.
- Startup and game-process events refresh Home only. A narrow lifecycle
  controller and deterministic counter tests prevent those events from
  reloading the active Settings document; confirmed game-installation changes
  retain explicit document-reload ownership.
- A pure settings projection query resolves the active section or global
  search into one flat sequence of explicit group headers, family headers, and
  setting rows. The WPF list uses one recycling host without framework
  grouping and constructs row ViewModels only for that sequence.
- Projection snapshots record the constructed stable paths and header counts.
  Windows presentation tests prove that General constructs no unrelated rows,
  invalid raw editor text survives row disposal/recreation, and a visible
  hotkey still reports conflicts with commands omitted by the current search
  projection.
- The active TOML is now loaded and committed through a document-scoped
  `ConfigurationWorkspace` and `IConfigurationRepository`.
  `SettingsViewModel` no longer reads configuration bytes or invokes the
  atomic store. The workspace owns its SHA-256 baseline revision and prepares
  one typed change set; only the TOML repository encodes those values and maps
  them into the sparse document.
- Repository failures leave every draft dirty and keep the saved baseline
  revision unchanged. Optimistic conflicts preserve the external file, mark
  the workspace stale, and leave replacement policy to the application layer.
  Successful atomic replacement is the only path that advances the workspace
  and edit-session baselines.
- Explicit cleanup binds the selected remediation IDs to the source revision,
  provider, channel, catalog ID, and catalog version. Apply rechecks those
  identities, requires a verified protected-backup receipt, commits through the
  repository, and returns a post-commit diagnosis. Unknown bytes are never
  cleanup candidates.
- The effective-configuration export is a separate local JSON document. It is
  intentionally unredacted, warns before the file picker, includes value origin
  and runtime status, and never enters diagnostics, clipboard support output,
  or an upload path.
- The catalog retains all 332 settings while exposing only player-facing
  directly editable settings to the normal workspace. Dynamic
  `sync.targets.*.*` templates remain machine-readable but are withheld until
  a concrete target-name context exists.
- Search covers title, description, category, and canonical path without
  rendering raw paths in the normal row.
- Category filtering and the large settings list are keyboard accessible and
  virtualized.
- The source-preserving TOML engine updates or removes one canonical
  assignment without reserializing surrounding content.
- Unknown keys, comments, ordering, whitespace, BOM, and line endings survive
  supported edits.
- Source-transition capture and restore preserve the complete file
  byte-for-byte, including invalid-but-runnable syntax and sparse omissions;
  they never parse then reserialize. Explicit restore is a
  Diagnostics/recovery action and must close or reload an open workspace
  without changing preferred source or installed artifact provenance.
- Duplicate targets, malformed statements, array tables, unsupported target
  syntax, invalid UTF-8, and unsafe multiline target edits fail closed.
- The atomic store writes and flushes a sibling temporary file, rechecks the
  transformed document against the conservative supported grammar, refreshes
  a backup, serializes same-process writes per path, and performs a
  content-hash recheck immediately before replacement. Injected failures and
  concurrent edits detected by that optimistic check leave the destination
  unchanged by the launcher.

## Current UI boundary

The Settings workspace now uses the accepted persistent left navigation and
contextual save surface. Search is global and opens from a toolbar control
instead of permanently consuming a content row. Section navigation is supplied
by the startup-selected settings layout provider, the compact rail contains
only navigation, and a compact toolbar aligns Back, the active section title,
and Search directly beneath the window chrome. The back arrow returns to Home
without consuming a labeled navigation row in the rail. Release-source identity
appears in General and About rather than forcing the rail to accommodate
metadata. Search-open state is a launcher UI preference stored outside both mod
TOML formats; search text remains session-only.

The scalar adapter covers the 82 directly editable booleans, eight constrained
enums, 22 integer/decimal settings, and three public string settings. Boolean
toggles, enum selections, numeric edits, and public string edits change only an
in-memory editing session. Enum choices come from
the generated schema, render friendly labels without changing their canonical
values, and visibly fall back to the runtime default when an existing override
is invalid. Numeric rows retain schema minimum/maximum constraints, validate
before staging, normalize accepted values to canonical TOML, and likewise show
the runtime default instead of presenting an invalid override as active. String
rows use schema-declared URI or comma-separated-list adapters; URL values accept
only absolute HTTP/HTTPS locations, list whitespace is normalized, and empty
values preserve the runtime's default/disabled meaning. Private paths and
endpoints, secret tokens, internal diagnostics paths, and dynamic target
templates remain outside the generic scalar editor. A
bottom action bar appears only while changes are pending, reports their exact
count, and owns Discard and Save Changes with 44-DIP action targets. Removing
an override is also staged and restores the runtime default without
materializing it into TOML. Theme-aware tooltips explicitly own their
foreground, background, and border so raw-key and help hover content retains
contrast in Light and Dark themes. The settings scrollbar uses the same dynamic
palette, and rows support mouse drag-to-scroll from non-interactive content
without taking input away from toggles, buttons, editors, or the scrollbar
itself. Faster flicks carry bounded momentum after release and decay without
overscroll; new input cancels the motion, and Windows' reduced-motion
preference disables inertia.

Save builds the complete staged document in memory and writes it as one atomic
replacement against the session's original contents. It creates a sibling
backup and reports a conflict instead of overwriting when the selected file or
its contents changed after the session began.

Notification rows parse canonical `false`, `true`, and inline-table policies
into independent Windows and audio delivery controls. Notification and speaker
glyphs communicate channel state without a redundant aggregate On/Off label;
the sound dropdown is enabled only with audio delivery and exposes only
catalogued sounds. Each interaction replaces the whole canonical event policy,
stages through the same sparse editing session as scalar settings, and can
clear an override through the contextual default action. Invalid canonical
values visibly fall back to the event default, matching the runtime contract,
instead of pretending the policy is unbound.

The Hotkeys adapter derives 90 bindings and their aliases directly from the
runtime action registry. Each row renders multi-chord alternatives without
flattening them, captures supported keyboard or mouse input, adds and removes
individual alternatives as wrapping inline chips, supports `NONE` through
the empty binding state, and removes canonical overrides through a contextual
reset action. Binding parsing
is strict and normalized before staging. Generated trigger mode, input
phase/layer, action category, and
conflict-group metadata let the launcher mirror runtime conflict semantics:
same-trigger collisions in a real conflict group block Save, while deliberate
shared bindings in `ConflictGroup::None` remain valid. Invalid configured
bindings show the runtime default rather than presenting an unusable shortcut
as active.

NetniV provider selection is packaged, while NetniV schema capability remains
unknown until a NetniV-published schema or reviewed compatibility adapter is
available. Source-preserving tests prove provider switching and unrelated
edits retain legacy/unknown TOML without applying the Guffawaffle catalog.

## Accepted destination

The directional Settings design in
[UX_DIRECTION.md](UX_DIRECTION.md) remains the accepted product vision. The
remaining generic value rows are integration scaffolding, not a replacement
information architecture and not a pixel-polish target.

The next UI weave converges on:

- compact back navigation plus persistent major setting families;
- a borderless settings list with full-row hover/focus treatment, thin
  dividers, and one stable control column;
- category-specific, compact editors instead of one generic card shape or
  detached right-side half-card;
- grouped notification rows with event state and delivery policy visible at a
  glance;
- dedicated Hotkeys and Data Sync experiences;
- a contextual bottom action bar that reports unsaved changes and owns Discard
  and Save Changes without permanently consuming content space.

Schema-driven generation remains the implementation rule beneath that
category-specific presentation. The launcher must not turn the accepted design
back into a handwritten second configuration catalog.

## Next weave

1. Add the generated-and-validated presentation envelope defined in
   [CONFIG_SCHEMA.md](CONFIG_SCHEMA.md): player label, optional concise help,
   grouping, unit, friendly apply timing, stability, search terms, and
   accessibility text. Curated overrides may improve presentation but cannot
   redefine runtime behavior.
2. Introduce a semantic `AppIcon` layer backed initially by the monochrome
   regular Microsoft Fluent System Icons exposed through `FluentIcons.Wpf`.
   Filled variants are reserved for selected navigation or deliberately active
   state. Application XAML must not select package glyphs directly. Replace the
   current theme-cycle action with a keyboard-accessible selector whose closed
   state shows `System`, `Light`, or `Dark`; keep future visual styles such as
   LCARS separate from color mode.
3. Replace the right-side half-cards with a borderless full-row renderer.
   Borders belong to actual controls; rows receive a subtle full-width hover,
   a thin divider, and a distinct additive keyboard focus ring. Selection,
   hover, and keyboard focus are separate states.
4. Normalize value presentation: compact numeric fields with schema-backed
   units, grouped boolean state and switch, optional descriptions, neutral
   `Next launch`/`Experimental`/`Modified` tags, and a reset icon revealed only
   when an override exists. Raw keys, data types, literal defaults, and
   provenance move out of the primary reading path.
5. Finish the specialized inline editors. Hotkeys keep removable alternative
   chips and use `+ Add` without a detached minus action. Notifications retain
   direct system/audio toggles and sound selection. Group both families by
   runtime domain without replacing the schema-derived catalog.
6. Apply keyboard-accessible tooltips, accurate automation names and help
   text, row-to-control labeling, 44-DIP action targets, and the shared focus
   visual to every icon-only command.
7. Resolve deprecated aliases and expose canonical/default/alias provenance in
   the editing session, explicit technical details, and future diagnostic
   export.
8. Instantiate dynamic target templates with validated concrete target names;
   never pass a wildcard schema path to the TOML mutation API. Add
   purpose-specific editors and redaction policy for private endpoints, paths,
   and secret values.
9. Add restart/apply summaries, a recoverable conflict-reload flow, and
   `Custom`, `Unsaved`, `Experimental`, and hotkey-conflict filters once the
   presentation state exists. The conditional dirty bar must derive a visible
   apply-timing summary from the complete staged set.
10. Expand the curated real-world Guffawaffle and NetniV round-trip fixtures as
    new syntax families enter the editor.

The already accepted shell remains fixed during this weave: the conditional
non-overlapping save bar, compact category rail, remembered search toggle,
integrated title area, selected-mode theme dropdown, drag scrolling, and bounded workspace
geometry are not reopened. A permanent search field and a framework-wide
Fluent theme migration are deferred until evidence shows that the current
shell or toggle no longer scales.

The editor is not accepted as complete until unknown keys and comments survive
the manual save round-trip smoke on a disposable configuration copy and
invalid or unsupported input is proven never to receive a destructive rewrite.
