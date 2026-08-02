# Distribution provider packs

## Decision

The launcher is one Windows product. Guffawaffle and NetniV are runtime/mod
distributions selected through data, not separately compiled launcher flavors.

A resolved provider definition must eventually supply:

| Concept | Purpose |
|---|---|
| Stable provider ID | Persistence, migrations, and capability lookup |
| Display name and description | User-facing source selection |
| Mod release repositories/channels | Bounded artifact discovery |
| Runtime manifest | Positive runtime identity and capability evidence |
| Configuration schema | Defaults, validation, presentation, and aliases |
| Artifact selection and trust | IDs, hashes, signatures, publisher, withdrawal |
| Migration metadata | Previewed source switching and rollback |

Provider display names are presentation only. WPF and core services must not
infer behavior from strings such as `Guffawaffle`, `NetniV`, `Sidecar`, or a
destination name.

## Authority boundary

The launcher may bundle a last-known-good provider pack for offline startup,
but each mod repository owns production of its runtime truth. Schema or
manifest changes are versioned inputs, not hand-maintained launcher defaults.
Unknown capability remains unknown; it is never invented in WPF.

Launcher self-update is a separate trust domain owned by this repository. A
provider can select and authenticate a mod artifact, but cannot redefine the
launcher executable's update authority.

## Transition

The extracted code initially carries the proven Guffawaffle schema and runtime
manifest as a bundled provider fixture. The first architecture milestone moves
release repositories, resources, capabilities, trust, and migration behavior
behind one resolved provider catalog, then adds a NetniV pack without a second
launcher build.
