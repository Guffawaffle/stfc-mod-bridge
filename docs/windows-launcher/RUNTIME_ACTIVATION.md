# Windows Launcher Runtime Activation

Status: first compatibility gate implemented

The accepted [Battle Bridge activation decision](BATTLE_BRIDGE_ACTIVATION_ADR.md)
extends this seam per feature. Provider identity, runtime capability, product
feature policy, and player preference remain separate inputs; Guffawaffle is
the first expected Battle producer, not a permanent owner gate.

## Purpose

The launcher resolves runtime-dependent behavior once during startup. Runtime
lineage and version are detected facts; they are not feature flags. The
startup path normalizes those facts into capabilities, evaluates product
feature policy, records an immutable activation plan, and selects concrete
implementations before the settings workspace is created.

The first feature is:

```text
settings.semantic-grouping
```

It requires:

```text
settings.principal-taxonomy.v1
```

The feature is not named after Guffawaffle. A compatible NetniV or other
distribution can advertise the same capability later without adding owner
checks to the renderer.

## Startup flow

```text
runtime manifest
      |
      v
LauncherRuntimeManifestDetector
      |
      v
immutable LauncherRuntimeProfile
      |
      v
LauncherFeatureResolver + LauncherFeaturePolicy
      |
      v
immutable LauncherActivationPlan
      |
      v
LauncherSettingsLayoutComposer
      |
      +-- PrincipalCatalogSettingsLayoutProvider
      |
      `-- AlphabeticalSettingsLayoutProvider
```

`LauncherStartupComposition` is the composition root for this slice. The
settings view model receives the selected `ILauncherSettingsLayoutProvider`;
ordinary row and rendering paths do not inspect distribution IDs, versions, or
feature flags.

Feature activation is provider-session-owned. Changing release source replaces
the provider session inside the current process while preserving launcher-owned
window, theme, navigation, and preference state. Newly reviewed runtime-manifest
evidence refreshes the runtime composition within the current provider session;
it does not pretend a source switch occurred. Only updating the Bridge executable
itself uses the separate verified updater handoff and process relaunch.

## Native application and workspace composition

Each provider session owns one `LauncherApplicationComposition`. Its
`LauncherWorkspaceServices` instance remains the sole native foundation for
mod lifecycle, provider selection, launch, trust, transaction, Settings/TOML,
and Diagnostics behavior. The built-in Mod Bridge Home and any future optional
Home receive that same service instance; optional Homes do not copy or replace
those implementations.

`LauncherWorkspaceRegistry` is the registration and activation seam. The base
application registers only `home.mod-bridge`. An optional Home registration
must name an exact feature ID and selected implementation ID from the immutable
activation plan. The registry evaluates that typed decision before invoking a
lazy workspace factory, returns an explicit unavailable result for a missing or
inactive feature or an implementation mismatch, and reuses the single composed
workspace instance after activation.

Consequently, a normal Mod Bridge launch has no optional Home registration or
factory to invoke. Even a registered-but-ineligible optional Home performs no
factory construction, file I/O, service startup, timer, listener, or network
work. This seam does not add a Battle feature ID, preference, surface, database,
or legacy Sidecar dependency; those remain separately reviewed future work.
The composition boundary grants no general outbound Internet transport authority.
Any future networked Battle capability requires its own explicit feature-level
policy; the existing reviewed release and update transport remains separate.

Settings stays lazy and provider-session-owned. Both Home modes resolve the
same Settings and Diagnostics contracts, while runtime-evidence refresh resets
the lazy Settings instance through the existing shell path. Before that reset,
the registry revalidates every activated optional Home against the replacement
typed plan and disposes/removes any workspace whose feature became inactive or
whose selected implementation changed. The ungated Mod Bridge Home remains
alive. The shared Settings owner is authoritative for draft guards and reloads;
invalidation discards staged drafts, makes retained old view models unable to
start another write, and notifies consumers before a replacement is created. If
an atomic Settings or Data Sync save is already active, invalidation marks the
old editor unavailable immediately, waits for that save to finish, and only then
tears down its edit session; replacement composition remains blocked throughout.
Synchronous provider-session disposal initiates the same idempotent barrier
without blocking the UI thread, and the disposed owner cannot compose a
replacement. Current window sizing, accessibility, staged Save/Discard,
diagnostic recovery, and provider recomposition behavior therefore remain owned
by the existing native shell.

## Runtime manifest

The current Guffawaffle launcher embeds
`runtime-manifest.guffawaffle.v1.json`. It positively declares:

- distribution ID;
- numeric runtime version;
- source revision;
- normalized capabilities;
- settings-catalog schema and revision.

Missing, unreadable, malformed, or unsupported manifests fail closed to an
unknown runtime with no inferred capabilities. A compatible manifest whose
settings-catalog schema is unsupported retains its positively detected
distribution identity but loses the principal-taxonomy capability.

The embedded manifest represents the currently packaged release source. When
source switching and mod installation are implemented, the same detector
contract should consume the selected runtime's embedded or adjacent manifest.
Git remote, repository path, and directory-name guesses must remain
development evidence only and must never elevate an unknown runtime.

This manifest is a compatibility contract, not a security boundary. Signing it
is unnecessary unless a future capability must resist local tampering.

## Settings layouts

When semantic grouping is active, the principal catalog provider consumes the
packaged presentation taxonomy and retains the accepted category navigation.
Settings with no resolved group are placed in `Uncategorized`; the renderer
does not guess.

When the feature is inactive, the alphabetical provider exposes one Settings
section and sorts all available settings by player-facing label. It deliberately
does not consume principal grouping metadata.

The activation decision records:

- active or inactive state;
- human-readable reason;
- selected implementation;
- fallback implementation when inactive.

About exposes the detected runtime, semantic-grouping state, selected layout,
and decision reason for support evidence. The complete profile and activation
plan remain immutable after startup.

## Taxonomy authority

Stochastic grouping belongs only in development and CI:

```text
scanner proposal
      -> principal review
      -> accepted catalog committed
      -> runtime manifest advertises capability
      -> startup planner evaluates eligibility
      -> selected layout provider renders
```

The runtime never performs stochastic grouping. A scanner proposal does not
become product behavior until an authorized principal accepts and packages the
catalog.

## Deliberate non-goals

- no remote feature polling;
- no percentage rollouts;
- no mutable global feature-flag service;
- no scattered version comparisons;
- no owner checks in settings rows or views;
- no dynamic assembly loading;
- no treatment of player preferences as feature flags.

Additional compatibility gates, experiments, and temporary release flags can
reuse the resolver, but each must retain a reason, explicit dependencies, and a
removal or fallback contract appropriate to its kind.
