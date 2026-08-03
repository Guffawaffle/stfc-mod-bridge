# Windows mod binary provenance

Mod Control inspects `version.dll` as data. Passive Home and Diagnostics refreshes never load the DLL and never make a
network request.

## Evidence order

1. Compute a bounded SHA-256 for a DLL no larger than 128 MiB.
2. Match the exact size and SHA-256 against the bundled reviewed-artifact catalog.
3. Read the PE version resource with `FileVersionInfo`.
4. Parse the bounded `stfc-identity-v1` comments marker when present.
5. Preserve malformed, unavailable, and unmarked states explicitly.

An exact reviewed hash can identify a provider artifact that predates the marker. A valid marker establishes only
self-declared distribution lineage. It does not prove an official release. An unmarked or unknown-hash DLL remains a
runnable custom installation; Mod Control does not infer its provider from the selected release source.

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

## Reviewed artifact snapshot

`providers/known-windows-artifacts.v1.json` is code-reviewed release data embedded into Mod Control. As of 2026-08-02
it contains:

| Provider | Track | Version/source | Windows DLL SHA-256 | Signature evidence |
|---|---|---|---|---|
| Guffawaffle | Stable | `v2.1.0-guffa.8` | `6D0E32E0D431144B75BB8632B7A3972BEDBEAF2D30019E66397D82A55B535BA9` | Valid expected Authenticode publisher |
| NetniV | Stable | `v1.1.4` | `020C975FD2391DF1814897B9D5F03A55443F99367EA6ACC4065AF7E240D9547A` | Upstream artifact is unsigned; identity is exact reviewed bytes only |
| NetniV | Dev | commit `7f0536bebc20d0d30bca44e89bfef56b0fb85ebc` | `CBEEDA425DB044D8E2D7CAE1B45408434DE437F3526FB08E1906994463E4D8A5` | Upstream artifact is unsigned; identity is exact reviewed bytes only |

The bundled snapshot is not silently refreshed at runtime. New hashes require review and a Mod Control build. This
prevents a mutable GitHub release or Actions artifact from becoming trusted merely because it was newest when the
launcher ran. NetniV Actions artifacts also require GitHub authentication and expire, so the dev entry records the
source commit and observation time.

## Update boundary

Network discovery begins only after **Check for updates**. The existing deployment transaction downloads to a staged
file, checks response and size bounds, verifies the release-provided SHA-256, validates embedded numeric version and
any declared distribution identity, applies the provider authenticity policy, and only then atomically replaces the
target. A failed verification removes the stage and leaves the installed DLL untouched.

Guffawaffle release manifests plus the expected Authenticode publisher provide the supported installation path.
NetniV identification is implemented, but automatic NetniV replacement remains unavailable until a reviewable update
contract can supply pinned inner-DLL bytes; upstream's current zip/checksum and unsigned DLL do not independently
provide that contract.
