# Windows Launcher Sync Workspace

This document records the LS-005 player surface and dogfood boundary for typed multi-target sync setup.

## Player model

The existing **Data Sync** settings destination now opens a dedicated workspace. It deliberately separates:

- global proxy, TLS, and feed defaults;
- concrete target instances and their wire contracts;
- target-local inherited, explicit-on/value, explicit-off, and explicit-clear states;
- Sidecar-only controls from external provider controls.

The add surface supports the singleton local Sidecar, Majel, the Spocks Club preset, and custom community/legacy
instances. External targets are enabled while present because the native contract has no canonical disabled field;
removal is the explicit disable operation. Sidecar retains its native enabled switch.

Each target card shows its stable identity, player-facing type, wire contract, validation state, effective enabled feeds,
endpoint, opaque token state, proxy provenance, TLS overrides, and supported feed overrides. Sidecar cards additionally
show battle-log enrichment and fleet-runtime mode. Unsupported controls are not rendered.

## Secret intent

Saved tokens are never placed in a text property or displayed. The password control begins empty and reports only
whether a saved token exists. Typing alone does not alter the desired topology; **Replace** explicitly adopts the new
secret, while **Clear** explicitly removes it. Persistence plan display remains redacted.

## Document coordination

Typed sync and ordinary settings use the same selected TOML and atomic store but keep separate typed edit sessions.
Only one session may save while the other owns a pending draft. A successful save refreshes the sibling baseline,
preventing two optimistic revisions from racing. Open and navigation never write the document, and successful sync save
does not mutate the startup runtime snapshot; restart is required.

Legacy root `sync.url` / `sync.token` loads as a virtual default target. Editing it remains blocked until the player
checks the explicit migration confirmation, after which the move into `sync.targets.default` occurs in the same atomic
save.

## Accessibility and scale

- Navigation, add actions, global controls, target actions, secret intent, override selectors, Save, and Discard have
  keyboard-focusable native controls and automation names/help.
- The target collection uses a recycling `VirtualizingStackPanel`.
- Long identities use trimming while their containing target card retains the complete automation name.
- Validation and operation status use polite live regions.
- Layout uses the launcher's dynamic theme brushes and scales with WPF DPI behavior.

## Automated evidence

- View-model tests cover byte-preserving open, mixed typed targets, Sidecar cardinality, inherited/effective feed state,
  secret replacement intent, proxy/TLS tri-state overrides, Sidecar-only controls, legacy migration confirmation,
  cross-workspace save exclusion, and atomic save/reload.
- Packaged UI Automation enters Data Sync, finds the typed workspace, global defaults, and virtualized target collection,
  then verifies both the active manual TOML and an isolated mixed-target fixture remain unchanged. The fixture run restores
  the real launcher selection byte-for-byte in `finally`.
- Packaging asserts that the signed MSIX contains exactly the reviewed launcher and paired release-verifier PEs and that its App Installer descriptor targets
  the immutable package URL. The ZIP remains a separately labeled standalone/self-update fallback, not a second
  installation entry point.
