# STFC Mod Bridge documentation authority

Status: current anti-drift index

This index separates current contracts from migrated planning evidence. It is
an interpretation guide, not a substitute for exact source, protected CI,
signed artifacts, or candidate-specific qualification receipts.

## Current authority

| Question | Authority |
|---|---|
| Repository scope, branch policy, verification, and safety boundaries | [`AGENTS.md`](../../AGENTS.md) |
| Windows product behavior and ownership boundaries | [`CONTRACT.md`](CONTRACT.md) |
| Public name, package identity, state paths, and repository coordinate | [`PRODUCT_IDENTITY.md`](PRODUCT_IDENTITY.md) |
| Player-facing layout and interaction direction | [`UX_DIRECTION.md`](UX_DIRECTION.md) |
| Provider versus Bridge ownership and trust | [`PROVIDER_PACKS.md`](../PROVIDER_PACKS.md), [`MOD_DEPLOYMENT.md`](MOD_DEPLOYMENT.md), and [`SELF_UPDATE.md`](SELF_UPDATE.md) |
| Current v1 release decision and evidence checklist | [GitHub issue #30](https://github.com/Guffawaffle/stfc-mod-bridge/issues/30) |
| Battle feature activation and dormancy | [`BATTLE_BRIDGE_ACTIVATION_ADR.md`](BATTLE_BRIDGE_ACTIVATION_ADR.md) and [`BATTLE_BRIDGE_LOCAL_IPC.md`](BATTLE_BRIDGE_LOCAL_IPC.md) |

GitHub issue state changes over time. Read the live issue before starting a
candidate-specific action; a copied checklist or old release comment is not the
current release decision.

## Historical planning evidence

The following files preserve the former Windows Launcher Parity+ campaign and
must not be used as current repository, branch, path, gate, or next-action
instructions:

| Artifact | Historical content |
|---|---|
| [`ARCHITECTURE_SPIKE.md`](ARCHITECTURE_SPIKE.md) | WL-001 decision and preview evidence from the former nested launcher workspace |
| [`WORK_ITEMS.json`](WORK_ITEMS.json) | WL-001 through WL-010 work-item snapshot and captured `wl-004-active` state |
| [`HANDOFF_FRAMES.md`](HANDOFF_FRAMES.md) | Migrated Lex frame IDs, statuses, and next actions |
| [`LEXRUNNER_NODES.json`](LEXRUNNER_NODES.json) | Original dependency pyramid for `Guffawaffle/stfc-mod#182` |
| [`LEXRUNNER_PYRAMID.json`](LEXRUNNER_PYRAMID.json) | Original generated topological batches and promotion summary |
| [`LEXRUNNER_EXECUTION_PLAN.json`](LEXRUNNER_EXECUTION_PLAN.json) | Original parity-plus execution commands and gates |
| [`LEXRUNNER_SYNC_SETUP_PLAN.json`](LEXRUNNER_SYNC_SETUP_PLAN.json) | Original Data Sync campaign plan |
| [`LEXRUNNER_WL006_RENDERER_UPLIFT_PLAN.json`](LEXRUNNER_WL006_RENDERER_UPLIFT_PLAN.json) | Original WL-006 renderer uplift plan |
| [`DOGFOOD_FRICTION.md`](DOGFOOD_FRICTION.md) | Time-bound toolchain observations and issue-routing evidence |

The JSON artifacts carry an `artifactClassification` object. Its
`executionDisposition` is `do-not-execute`. Current tooling must reject or
ignore them as active plans; their old repository coordinates, nested paths,
branches, issues, and gates remain intact as captured evidence.

## Interpretation rules

- Do not globally replace `Guffawaffle/stfc-mod`. It remains the authoritative
  Guffawaffle mod-provider repository and the extraction-history source. Only
  Bridge product, self-update, and current project-management coordinates use
  `Guffawaffle/stfc-mod-bridge`.
- Evidence is scoped to the exact source revision, artifact bytes, route,
  package topology, operating-system boundary, and qualification procedure that
  produced it. One safe or failed crossing does not certify every crossing.
- A historical plan can explain why a decision was made, but cannot authorize a
  current branch, mutation, release, feature activation, or network route.
- When a current decision supersedes an older one, add an explicit supersession
  note and preserve the old evidence. Do not silently rewrite measurements or
  pretend the prior decision never existed.
- Before release work, reconcile current `main`, open issue state, dependency
  locks, signing identity, hosted channel state, and the exact candidate commit.

## Current release boundary

STFC Mod Bridge remains pre-release. `v0.1.0-rc.17` is the approved public
canary entry point while qualification continues. Later candidates are
qualification artifacts unless their release notes explicitly approve public
installation. Issue #30 owns the next immutable signed build and its
clean-install, update, dogfood, accessibility, and evidence decision.

Battle Bridge is not operationally activated by the base product. Its native
capability, IPC, lifecycle, and sink foundations remain dormant until their own
accepted composition and signed-package evidence gates close.
