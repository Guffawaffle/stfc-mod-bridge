# Launcher configuration schema

`config-schema.guffawaffle.v1.json` is the generated launcher-facing contract
for the Guffawaffle release stream. It is derived from the mod's runtime-owned
C++ defaults and catalogs; do not edit it by hand.

Its authoritative generator lives in the `Guffawaffle/stfc-mod` repository,
not this launcher repository. Generate or validate it from the root of that
mod checkout:

```powershell
node scripts/generate_config_schema.mjs
node scripts/generate_config_schema.mjs --check
node --test scripts/test_config_schema.mjs
```

That repository's CI runs both the fixture tests and the stale-artifact check
before its C++ test suite.

The launcher separately owns the reviewed, version-bound NetniV adapter at
`providers/netniv/configuration-schema-set.v1.json`. Its presentation profile
is derived from the exact upstream example/configuration evidence named by the
catalog revisions. It may classify and explain reviewed settings, but cannot
change runtime types, defaults, aliases, status, sensitivity, feature gates,
or persistence semantics. A presentation profile is accepted only when its
paths are unique, catalog-owned, directly player-editable, and complete for
the selected exact revision.

## One settings model, three adapters

Every setting has the same launcher metadata envelope: canonical path, title,
description, type, runtime default, platform support, apply behavior,
sensitivity, stability, source support, aliases, and provenance.

The `control` field selects one of three value adapters:

- `scalar` for booleans, numbers, strings, enums, and dynamic sync-target
  fields;
- `keybinding` for values generated from the input action and compatibility
  alias registries;
- `notification-policy` for the boolean-or-inline-table notification union.

These are not separate player-facing configuration systems. The launcher uses
one search, category, validation, changed-state, and persistence experience,
then delegates value-specific behavior to the matching adapter.

## Presentation metadata

The runtime schema is complete enough to validate and persist a value, but its
technical title and description are not automatically suitable as primary
player copy. The launcher therefore derives a presentation envelope for each
visible setting:

- player-facing label and optional consequence-oriented help;
- display group and search terms;
- optional approved family metadata for repeated members of one conceptual
  control set;
- unit or suffix, compact editor-width hints, and an optional numeric
  `sliderStep` for a deliberately bounded adjustment;
- friendly apply timing such as `Next launch`;
- stability and modified-state tags;
- accessible control name and help text.

This is a generated-and-validated view over the authoritative schema, not a
second settings catalog. Most presentation data is derived mechanically.
Small curated overrides may improve copy or units for canonical paths, but the
generator must reject stale paths, duplicate entries, invalid adapter hints,
and visible settings without a usable player label. Overrides cannot redefine
types, defaults, constraints, aliases, sensitivity, or persistence behavior.

`sliderStep` is opt-in rather than inferred from a setting name or raw numeric
range. Numeric scalars may derive slider bounds from finite hard constraints or
declare a softer `sliderMinimum` and `sliderMaximum` presentation range. Soft
bounds must remain inside any hard constraints but do not reject direct text
entry outside the slider range. This supports approachable controls such as a
one-second timing slider while retaining larger accessibility values through
the textbox. The step must divide the presented range into 1–5,000 equal
positions. The slider is an adjustment surface; the textbox remains the precise
entry and validation surface. Non-linear or excessively granular numeric
settings remain textbox-only.

`group` answers where a setting belongs. Optional `family` metadata answers
which independently persisted settings should be presented together. A family
declares a stable ID, matching parent group, shared label and optional help,
display order, constrained presentation hint, and each setting's member label
and order. The current `compact-binding-list` hint is valid only for
keybindings. The generator validates every family member and the renderer
consumes this metadata directly; it must not infer families from path prefixes
or numbered names at runtime.

Canonical keys, exact defaults, value types, aliases, and provenance remain
available to search, accessibility help, diagnostics, and an explicit
technical-details affordance. They are not the normal row title or description.

## Authoritative producers

- Ordinary runtime defaults and descriptions come from
  `mods/src/defaultconfig.h`. The public surface comes from the reference TOML,
  while literal `get_config_or_default` reads and canonical custom-reader paths
  are scanned as a release-drift guard. Hidden runtime settings remain in the
  schema with `internal` or `experimental` stability so the player UI can omit
  them without making provenance incomplete.
- Input defaults come from `ActionSpecs()`. Legacy and deprecated
  `[shortcuts]` paths come from `ShortcutConfigAliases()`.
- Notification names, legacy paths, sounds, and stability come from
  `notification_event_catalog()`. A canonical inline table replaces the whole
  event policy; it never partially inherits a deprecated value.
- Dynamic `sync.targets.*` fields are generated from `SyncOptions` and the
  runtime sync defaults.

The checked-in JSON is a build artifact, not another defaults catalog. A source
change that alters the generated result must update the artifact in the same
change.

## Release source and persistence boundary

The generated Guffawaffle schema declares `source.id = "guffawaffle"` and
describes Guffawaffle artifacts only. NetniV uses the repository-owned
`providers/netniv/configuration-schema-set.v1.json`, derived from reviewed
upstream source evidence. Its loader requires an exact provider, track, release
version, and full commit SHA before materializing a catalog. Shared metadata and
explicit stable/dev deltas do not authorize adjacent or future releases. The
launcher must never apply either provider's catalog to another release source.

Release-source selection belongs to launcher state, not the mod TOML. Switching
sources is a migration transaction with compatibility preview, confirmation,
backup, and rollback.

TOML remains the current runtime and interchange boundary. Launcher writes are
sparse and must preserve unknown keys and comments. A future richer
Guffawaffle-only profile store may compile to this model, but it must retain
deterministic TOML import/export while the C++ runtime consumes TOML and while
NetniV compatibility is supported.

## Sensitivity and provenance

`sensitivity` is mandatory so diagnostics and support bundles can redact
secrets and private endpoints/paths. `provenance.defaultSource` identifies the
runtime catalog that supplied the default; `provenance.runtimePath` gives the
corresponding runtime-vars location or wildcard template.

Runtime provenance can add the effective value source (`default`, canonical
TOML path, compatibility alias, or runtime override) without redefining any
schema metadata.
