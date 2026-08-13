# Windows mod binary provenance

Mod Bridge inspects `version.dll` as data. Passive Home and Diagnostics refreshes never load the DLL and never make a
network request.

## Evidence order

1. Compute a bounded SHA-256 for a DLL no larger than 128 MiB.
2. Match the exact size and SHA-256 against the bundled reviewed-artifact catalog.
3. Read the PE version resource with `FileVersionInfo`.
4. Parse the bounded `stfc-identity-v1` comments marker when present.
5. Preserve malformed, unavailable, and unmarked states explicitly.

An exact reviewed hash can identify a provider artifact that predates the marker. A valid marker establishes only
self-declared distribution lineage. It does not prove an official release. An unmarked or unknown-hash DLL remains a
runnable custom installation; Mod Bridge does not infer its provider from the selected release source.

The schema-one marker is:

```text
stfc-identity-v1;distribution=<id>;source=<source-state>;base=<commit>;build=<invocation>;mode=<mode>;channel=<channel>
```

Every key is required exactly once. Unknown keys, repeated keys, unsupported schema prefixes, unsafe characters, and
oversized values resolve as malformed identity rather than partially trusted data.

Producers emit the fields in the canonical order shown above. The reader remains order-tolerant while requiring the
exact schema-one key set; field order is serialization parity, not authenticity evidence.

This evidence describes the file currently present on disk. It does not attest that the same image is already mapped
into a running game process; runtime activation remains a separate health dimension.

Provider-aware health and update routing uses stable IDs in this order: required launcher-managed attribution, exact
reviewed artifact identity, recognized runtime-distribution lineage, then the user-selected source only for an
unrecognized custom build. Display names never select provider behavior. A prepared operation also carries its stable
provider ID and cannot execute through another provider endpoint.

Installed provenance and preferred update source are independent state axes.
The selected source may route an explicit Check-for-updates request for an
otherwise unknown custom build, but it never retroactively attributes those
installed bytes. An unknown, unmarked, malformed-marker, or developer DLL
remains external/custom and runnable when bounded file-safety checks pass. If a
previously managed DLL changes, Mod Bridge suspends its managed-integrity claim
and must preserve the changed bytes; it does not transfer attribution to the
preferred source or overwrite the file until the player explicitly chooses a
repair or source-switch transaction. See the
[source lifecycle matrix](MOD_DEPLOYMENT.md#stateaction-matrix).

## Reviewed artifact snapshot

`providers/known-windows-artifacts.v1.json` is code-reviewed release data embedded into Mod Bridge. As of 2026-08-13
it contains:

| Provider | Track | Version/source | Windows DLL SHA-256 | Signature evidence |
|---|---|---|---|---|
| Guffawaffle | Stable | `v2.1.0-guffa.9` | `FF1DE2F6BD17E54760C75F7E94CA3FA6F01A380AD6C03DDFD98C0AF84910B80A` | Valid expected Authenticode publisher and timestamp |
| NetniV | Stable | `v1.1.4` | `020C975FD2391DF1814897B9D5F03A55443F99367EA6ACC4065AF7E240D9547A` | Upstream artifact is unsigned; identity is exact reviewed bytes only |
| NetniV | Dev | commit `7f0536bebc20d0d30bca44e89bfef56b0fb85ebc` | `CBEEDA425DB044D8E2D7CAE1B45408434DE437F3526FB08E1906994463E4D8A5` | Upstream artifact is unsigned; identity is exact reviewed bytes only |

The bundled snapshot is not silently refreshed at runtime. New hashes require review and a Mod Bridge build. This
prevents a mutable GitHub release or Actions artifact from becoming trusted merely because it was newest when the
launcher ran. NetniV Actions artifacts also require GitHub authentication and expire, so the dev entry records the
source commit and observation time.

## Update boundary

Latest-release network discovery begins only after **Check for updates**. The
existing deployment transaction downloads to a staged file, checks response
and size bounds, verifies the release-provided SHA-256, validates embedded
numeric version and any declared distribution identity, applies the provider
authenticity policy, and only then atomically replaces the target. A failed
verification removes the stage and leaves the installed DLL untouched.

Install/update/switch execution may download the exact artifact named by a
fresh prepared observation, but it must not query `latest` again or silently
cross provider/channel/runtime identity. Passive refresh, source selection,
Diagnostics, and launch perform no release discovery.

Guffawaffle release manifests plus the expected Authenticode publisher are the canonical installation path. The current
stable manifest is still constrained to the exact reviewed repository, tag, source commit, DLL, and runtime-manifest
bytes because schema v1 declares no manifest authenticity. Its narrow fallback ZIP must contain exactly that certified
DLL/runtime-manifest pair. NetniV stable installation is likewise available only for the exact reviewed ZIP and
inner-DLL bytes. Either provider fails closed when GitHub's latest release differs from the bundled certification; these
shims are replaced only by an accepted provider-published provenance contract.
