# Battle Bridge Feature-Capability Activation Decision

Status: accepted architecture; feature inventory and evidence pending

## Context

Battle Bridge is the Battle-focused mode of the native STFC Mod Bridge, not a
separate Electron product and not a provider-specific launcher build. It keeps
the existing Bridge implementation for discovery, launch, mod lifecycle,
trust, configuration, Data Sync, diagnostics, recovery, accessibility, and
Windows servicing, then composes Battle features where the installed runtime
provides compatible evidence.

Guffawaffle is the first runtime expected to publish that evidence. That fact
does not make Guffawaffle identity an activation condition. NetniV, or another
reviewed provider, can enable any compatible feature later by publishing the
same accepted capability contract.

## Decision

Battle features extend the existing runtime-activation seam in
[`LauncherRuntimeActivation.cs`](../../src/STFCCommunityMod.Launcher.Core/LauncherRuntimeActivation.cs).
The product will not add a second feature-flag framework.

Four independent inputs remain distinct:

| Input | Meaning | Authority |
|---|---|---|
| Provider identity | Artifact origin, release and trust policy, migration, and support evidence | Resolved provider data and positively detected runtime identity |
| Runtime capability | A concrete behavior or versioned contract the installed runtime can provide | Positively validated runtime evidence normalized into `LauncherRuntimeProfile` |
| Product feature policy | Whether this Bridge release permits an eligible feature to compose | `LauncherFeatureCatalog` and `LauncherFeaturePolicy` |
| Player preference | Whether the player chooses an available optional experience | Launcher-owned preference state consumed after eligibility is resolved |

`LauncherFeatureResolver` combines the immutable runtime profile, catalogued
requirements and dependencies, and product policy into an immutable
`LauncherActivationPlan`. The plan answers whether Bridge can safely compose a
feature implementation. A player preference cannot invent a capability,
override product policy, or turn an ineligible feature on. Likewise,
`LauncherFeatureDefault` and policy overrides are product policy, not player
preferences.

```text
provider/runtime evidence
        |
        v
immutable LauncherRuntimeProfile (identity + normalized capabilities)
        |
        +---- LauncherFeatureCatalog (requirements, dependencies, fallbacks)
        |
        +---- LauncherFeaturePolicy (product eligibility)
        v
immutable LauncherActivationPlan
        |
        +---- optional launcher-owned player preference
        v
selected native implementation or explicit fallback
```

The distribution ID remains available for provenance, diagnostics, release
selection, trust, and migration. Application workspaces and renderers must not
use it, a provider display name, or a repository name to decide whether a
Battle feature is available.

## Feature contract

Each Battle feature added to `LauncherFeatureCatalog` must have:

- a stable feature ID describing the player outcome rather than its producer;
- explicit required capability IDs and accepted schema/version evidence;
- explicit feature dependencies;
- a feature kind, activation mode, and default product policy;
- one active implementation and one safe fallback implementation;
- a deterministic activation or fallback reason suitable for diagnostics;
- a documented removal or graduation contract for temporary gates and flags;
- a separately modelled player preference only when the feature is optional.

Capability IDs describe compatible behavior, not ownership. Compatibility
versions belong in the capability contract when they materially change the
consumer boundary. A producer claim is accepted only through the reviewed
runtime-evidence path; repository proximity, source selection, version guesses,
and display names do not elevate an installed runtime.

Missing, unknown, malformed, or unsupported evidence fails closed for only the
features that require it. For example, a runtime may qualify a Battle history
feature while lacking an alert capability: history composes, alerts select
their explicit fallback, and ordinary Mod Bridge behavior remains available.
Feature dependencies apply the same rule transitively and deterministically.

The feature decision reason must remain visible in support evidence. UI code
may present a friendly summary, but it must not recalculate eligibility.

## Provider and source-transition behavior

Current provider support is evidence in the capability matrix, not a durable
feature rule. A future compatible NetniV runtime manifest must be able to
activate a feature without changes to its workspace or renderer.

Selecting a provider, installing a provider artifact, and enabling an optional
Battle feature remain different actions. If the player requests a feature the
installed runtime cannot provide, Bridge may explain the missing capability and
offer a reviewed provider transition when one is available. It must not switch
providers silently or imply that every feature requires the same provider.
Existing protected TOML backup, atomic deployment, rollback, trust, and source
selection contracts continue to govern that transition.

## Sidecar disposition

The legacy Sidecar is a behavioral donor and migration source, not the target
runtime architecture. Accepted parsers, domain outcomes, fixtures, and stored
history may be migrated into the native Bridge design. Electron, Node, browser
UI, duplicate mod management, and unrelated experimental surfaces do not cross
the boundary by default.

No Sidecar behavior is retired merely because a replacement is planned. Each
retained outcome must first have native behavioral fixtures, an accepted
migration or preservation path where data exists, and qualification evidence
for the replacement. Retirement follows that proof.

## Consequences

- Partial producer support is a normal, testable state rather than an error.
- Guffawaffle can ship first without becoming a permanent owner gate.
- NetniV can gain features independently as its runtime publishes compatible
  positive evidence.
- Base Mod Bridge stays usable when all Battle capabilities are absent.
- Battle code remains subject to the same native trust, packaging,
  accessibility, diagnostics, and lifecycle contracts as the rest of Bridge.
- Remote polling, percentage rollout, mutable global flags, and
  preference-as-feature-flag behavior remain out of scope.

## Reserved decisions and evidence owners

This ADR deliberately does not pre-assign contracts that still require
inventory or measurement:

| Decision | Owner and evidence gate |
|---|---|
| Exact Battle feature/capability IDs and the minimum Battle Home baseline | [Sidecar #55](https://github.com/Guffawaffle/stfc-mod-sidecar/issues/55), accepted against the cross-provider fixtures in [#75](https://github.com/Guffawaffle/stfc-mod-sidecar/issues/75) |
| Integrated MSIX versus a separately signed optional package | The [package-topology evidence](BATTLE_BRIDGE_PACKAGE_TOPOLOGY.md) accepts one integrated package as the v1 default. [Sidecar #66](https://github.com/Guffawaffle/stfc-mod-sidecar/issues/66) remains open for the real Battle delta and zero-cost base-mode measurements. |
| Runtime lifetime after the Bridge window closes | [Sidecar #59](https://github.com/Guffawaffle/stfc-mod-sidecar/issues/59), with lifecycle, locking, crash-recovery, and player-expectation evidence |
| Storage budget and retention defaults | [Sidecar #61](https://github.com/Guffawaffle/stfc-mod-sidecar/issues/61) and [#78](https://github.com/Guffawaffle/stfc-mod-sidecar/issues/78), after corpus-size, query, cleanup, and soak measurements |
| Deferred research, cloud, and static-modifier surfaces | Their deferred issues, reconsidered only with a bounded player outcome, privacy model, and delivery evidence |

Until those gates close, implementations must preserve the extension points
without selecting an answer implicitly.
