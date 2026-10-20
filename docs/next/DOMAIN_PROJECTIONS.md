# Pure capability and observation projections

`bridge-domain::projections` accepts immutable observations and explicit policy
and returns the existing v1 projections. It has no filesystem, process,
native consumer, engine, Tauri or frontend dependency. `platform` contains
backend-only value types; its native adapters remain separate crates.

| Criterion | Implementation and evidence |
| --- | --- |
| BR13-01 | Recognized producer manifests and stable-ID policy intersect feature declarations. Missing, invalid, unknown, mismatched and duplicate declarations cannot grant support. Declared capability is separate from a live native route and operational availability. |
| BR13-02 | Provider/distribution IDs and exact runtime/client/host binding choose policy. Display-name copies and renames have no operational effect. |
| BR13-03 | Actions and sessions retain exact captured installation/profile/session identity. Preference is not a retargeting input. Unknown or historical live facts stay unknown; session conflicts are surfaced as partial/conflicting observations. |
| BR13-04 | Pure projections never bootstrap a catalog, store or configuration, change preferences or launch a process. Redacted diagnostic facts retain provenance; bounded overflows refuse rather than silently omit facts. |

Launch ordinary/isolated and focus have conservative rules for explicit native
routes, matching profile/runtime facts, complete current session inventories,
contention and recovery. Other action families remain unavailable until their
own semantic rules are implemented. `Available` grants neither a lock nor
permission. Preparation and admission still revalidate actual native resources.

Run `node scripts/next/domain-projections.mjs` for the direct suite, or the
candidate-bound package dispatcher for br-13. The suite compiles and identifies
the current native library test executable, verifies all 45 named semantic
tests execute with no failures/ignored tests, retains its hash, and checks
formatting, strict Clippy and the dependency boundary. The synthetic facts do
not qualify platform services, a runtime artifact or a release. Package
acceptance additionally requires current br-02 prerequisite evidence through
the owning LexRunner gate projection.
