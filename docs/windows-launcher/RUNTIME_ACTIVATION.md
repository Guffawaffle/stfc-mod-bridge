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
      +--> LauncherSettingsLayoutComposer
      |          |
      |          +-- PrincipalCatalogSettingsLayoutProvider
      |          |
      |          `-- AlphabeticalSettingsLayoutProvider
      |
      `--> LauncherBattleFeatureComposer + persisted per-feature preference
                 |
                 `-- immutable LauncherBattleFeatureSnapshot
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

The same provider-session slot owns the immutable Battle feature snapshot. It
recomposes when either the exact reviewed runtime evidence identity or the
persisted Battle/Fleet preference changes. Capability and policy are resolved
first; preference only projects an eligible feature to available, enabled, or
disabled. A retained enabled preference therefore becomes unavailable when
capability is revoked and cannot keep an old implementation active. This
session projection is state only: it does not register the dormant runtime
coordinator, open the database, load SQLite, create a credential or runtime
lock, start a named pipe, create a timer/thread, or grant network authority.

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

The embedded manifest is the packaged base fallback. A launcher-certified,
installed exact DLL/runtime-manifest pair can replace that base evidence for
the current provider session; local health revalidates and revokes it when the
adjacent bytes change. The exact-pair candidate review uses the same detector
and resolver before live mutation. Git remote, repository path, and
directory-name guesses remain development evidence only and never elevate an
unknown runtime.

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

## Exact-pair feature remediation review

An unavailable feature can start provider remediation only from an explicit
feature action. The remediation coordinator first uses the existing provider
switch preview for the target release, then the session-owned reviewed
candidate acquirer downloads and verifies the exact DLL/runtime-manifest pair.
Passive health, startup, Settings, Diagnostics preview, and source selection do
not acquire or inspect candidate bytes.

The immutable review retains the current and target typed eligibility and
implementation-selection evidence, checked-in catalog and product-policy
source identities, provider/channel/runtime attribution, repository, tag,
source revision, and both exact SHA-256 identities. The current and target
decisions come from the existing resolver; provider names, display copy, remote
flags, and player-facing reason strings are not resolver inputs. A target plan
may truthfully remain inactive because of policy, dependency, or implementation
availability even when its runtime advertises additional capability.

Confirmation is bound to that exact evidence and transfers the same single-use
candidate lease into the existing atomic provider transaction. Deployment
restages and re-verifies those locked bytes without another download. Cancel,
validation failure, or a stale/replayed receipt invokes exact candidate cleanup
before any provider selection, TOML, journal, game file, preference, or runtime
composition mutation. Player preference is intentionally outside this review.
The #132 composition surface considers it only after a successful transaction
and after the typed runtime capability and checked-in product-policy decision.
A retained player preference cannot elevate an inactive feature.

This reviewed release acquisition is narrow launcher update infrastructure. It
does not grant Bridge or Battle Bridge general outbound Internet authority, add
a provider-name gate, or establish a local HTTP service.

The first per-feature collection projections and their local-only transport
boundary are documented in
[BATTLE_BRIDGE_ACTIVATION_ADR.md](BATTLE_BRIDGE_ACTIVATION_ADR.md) and
[BATTLE_BRIDGE_LOCAL_IPC.md](BATTLE_BRIDGE_LOCAL_IPC.md). They remain dormant;
no runtime capability, policy decision, or preference starts a listener.

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
