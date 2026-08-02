# Windows Launcher Runtime Activation

Status: first compatibility gate implemented

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

Feature activation is startup-latched. Changing release source, runtime
manifest, or policy requires a launcher restart.

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
