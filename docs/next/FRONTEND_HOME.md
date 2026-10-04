# Shuttle Bay and shared navigation

The real application composes Home, workspace navigation, one Save/Discard/Stay
decision and one generic action review over the same facade and WorkContext.
There is one applied installation/profile selector. Session focus captures the
exact observed PID/start/executable/session rather than the next-launch selector.
Preparation, explicit confirmation, admission and observed completion remain
separate. A lost commit response retains its exact replay until reconciliation.

| Criterion | Implemented behavior and evidence |
| --- | --- |
| BR18-01 | Ready ordinary/isolated, unknown/partial, complete missing, offline, running/multiple/recycled sessions and recovery are represented by strict schema-validated shared fixture compositions. Browser controls submit only their fixed scripted requests. |
| BR18-02 | View navigation preserves the draft. Target changes queue Save/Discard/Stay. Save retains the old target until its exact reviewed operation completes; Discard requires the old draft receipt; Stay restores the opener without mutation. |
| BR18-03 | Primary actions and warnings use scoped backend availability and reason codes. Immutable captures survive later selection changes, stale reads and observer disposal. Confirmation, replay and outcome refresh are explicit. Game, Community Mod and Bridge maintenance have separate entry destinations. |
| BR18-04 | Ordinary screens hide private paths, account material and protected references. Only the separate development preview exposes synthetic request/provenance diagnostics. Development entries, mocks and fixtures are refused by the production dependency graph. |

`ui/home-preview/` mounts the real App with a keyed, disposable synthetic facade.
It is a separate development entry. The accepted fixture captures and review
digests are unchanged; request correlation and observed selector assertions,
query envelopes and documented availability variants are development composition.
This is a strict scripted demonstration, never a frontend policy oracle.

Run `node scripts/next/frontend-home.mjs` for 32 Home assertions and nine preview
composition regressions, Svelte checking, 25 pinned-browser criteria, 18 retained
images and production graph inspection. The 87 shared component/facade assertions
remain separately bound in br-12. Package acceptance also requires current br-12
prerequisite evidence through the canonical candidate-bound LexRunner gate.

The private browser proof covers actual HTML modal focus, explicit controls,
draft navigation, sampled dark/forced-color/reduced-motion presentation and
200% root text scale at desktop and compact widths. Images are reviewed alongside
interaction assertions. It does not establish whole-product usability, native
webview behavior, Narrator/VoiceOver, actual Apple Silicon execution or a release.
Management and Engineering data views are separate later work packages.
