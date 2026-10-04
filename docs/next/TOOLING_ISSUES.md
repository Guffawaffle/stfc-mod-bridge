# Rewrite tooling observations

Observed during the Rust Bridge campaign on 2026-10-03. The original observations
below used installed LexRunner 2.1.0. They remain historical evidence; current
local adoption is recorded separately and does not establish an upstream release.

| Owning issue | Observed problem | Campaign handling |
| --- | --- | --- |
| [LexRunner #1015](https://github.com/SmarterGPT/lexrunner/issues/1015) | MCP gate execution derives the candidate repository from the shared, non-Git caller root and lacks an owning-root argument. Absolute plan and gate cwd do not repair the service's earlier Git lookup. | Run the installed integration-gate CLI from the canonical owning checkout. Record its exact plan, command, manifest and inputs. |
| [LexRunner #1016](https://github.com/SmarterGPT/lexrunner/issues/1016) | The installed CLI does not expose the item/gate filters needed for the complete scheduling plan. | Generate a single real candidate projection, retaining authoritative dependency validation in the Bridge dispatcher. No passing ancestor placeholders are added. |
| [LexRunner #1017](https://github.com/SmarterGPT/lexrunner/issues/1017) | A dependent gate started before its prerequisite completed, despite declared dependencies and a one-worker limit. The Bridge dispatcher refused the stale prerequisite. | Run current prerequisite projections sequentially. The dispatcher validates prerequisite receipts and source inventories before and after each dependent gate. |
| [Bridge #233](https://github.com/Guffawaffle/stfc-mod-bridge/issues/233) | The retained review helper routes through obsolete local memory/persona assumptions rather than Bridge's scoped PostgreSQL LexSona binding. | Use explicitly scoped Bridge continuity/constraint evidence and independent source review. Do not report the obsolete helper as repaired or a shadow persona as applied to a Run. Formal committed-head review remains required. |
| [Bridge #235](https://github.com/Guffawaffle/stfc-mod-bridge/issues/235) | Development native-automation dependencies have advisories whose stated fixed versions are unavailable in the registry. | Keep instrumentation outside production, retain audit evidence and leave native automation qualification pending. A clean production audit does not close the development dependency issue. |

The coordination database is explicitly
`D:/dev/stfc-workspace/.smartergpt/runner/coordination.db` when a coordination
tool is used. Integration gate runs are not ADR-010 Runs or Attempts. Destructive
Runner mutations and automatic Lex frame emission remain disabled. Continuity
writes use the explicit owning-repository AXF route.

One failed AXF handoff was caused by this campaign supplying an unregistered
module label, `bridge-contracts`. A canonical scoped dry run exposed the module
validation failure; registered `docs/architecture` and `workspace/tooling`
labels restored the write. That caller error is not evidence of a store outage
and was not bypassed with a policy override.

A later handoff repeated this caller mistake with `bridge/rust-rewrite`,
`bridge/native-consumers` and `bridge/frontend`. The installed Lex 4.4.1 module
validator refused those exact labels and accepted `docs/architecture` plus
`workspace/tooling` in a read-only validation. The shared router forwarded the
payload to the correct canonical Bridge root and reported the child's exit 1
as `SCOPED_INVOCATION_FAILED`. This observation does not establish a store outage
or require a tooling issue; the campaign must use registered module labels.

The original LexRunner also warned that the checkout had no command whitelist and selected
its permissive fallback, then increased requested timeouts. Those observations
remain explicit; this campaign did not silently repair configuration, enable
additional mutation permissions, upgrade the Runner, or migrate memory stores.

## Local candidate adoption after restart

[LexRunner PR #1020](https://github.com/SmarterGPT/lexrunner/pull/1020) contains
the signed local candidate `f318225f0a1ab10302ba14b51bcc46a0eb57afc0`, identified
by its package version as unreleased 2.4.0. It addresses routing/ordering and
adds durable gate operations and a supported local installer. The candidate was
installed under the existing npm prefix. This is local adoption, not an upstream
release or permission to promote the draft PR.

[Issue #1019](https://github.com/SmarterGPT/lexrunner/issues/1019#issuecomment-5976623638)
retains the long-gate qualification: a single producer completed after 506744 ms,
and a replacement observer read its completed descriptor. After the actual Codex
restart, the live MCP connection exposed `gates_start`, `gates_status` and
`gates_cancel`; `gates_status` observed that same completed operation with the
same descriptor and terminal hashes. Source, installed CLI/MCP/worker bytes,
dependency inventory and retained evidence were verified separately.

The successful aggregate adoption receipt has SHA-256
`0e3b44a76ed4fbcd20d6949b863b9552dd219bc64d216aa95ac61be3bf661e64`.
The earlier diagnostic which exceeded its final inventory timeout remains a
failed receipt. Read-only continuation did not replay gates or installation.
[Issue #1018](https://github.com/SmarterGPT/lexrunner/issues/1018#issuecomment-5976594033)
retains the local-installation evidence. Broad suite, protected host and release
holds remain distinct from the focused local qualification.

Use the new durable gate route with an explicit canonical Bridge `repoRoot`,
frozen plan, unique output directory and idempotency key. Retain the returned
operation file and its SHA-256 for status observation. Observer timeout is not
producer failure and must not cause a fresh producer to be started. Existing
Bridge prerequisite validation and sequential package projections still apply.

The shared AXF continuity context read all four scoped stores after restart with
its configured 900-token budget. A preliminary 500-token probe was below Lex's
required envelope and was a caller error, not a store or routing outage.
