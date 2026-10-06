# Native dependency and producer ownership

Status: br-00 prerequisite contract, 2026-10-03. Related work is
[epic #231](https://github.com/Guffawaffle/stfc-mod-bridge/issues/231),
[br-00 #232](https://github.com/Guffawaffle/stfc-mod-bridge/issues/232) and the
[dependency graph](../plans/rust-tauri-cross-platform/work-packages.json).
The Rust rewrite may replace Bridge's implementation. Accepted shared identity,
data ownership, operation and trust contracts remain requirements.

## Repository boundaries

| Owner | Canonical checkout on this Windows workspace | Responsibility |
| --- | --- | --- |
| Bridge | `D:/dev/stfc-mod-launcher` | Frontend-independent application operations/projections, provider/runtime deployment transactions, settings/Data Sync workflows, diagnostics, its own packaging/update and replaceable clients |
| Profiles | `D:/dev/stfc-profiles` | Shared directory catalog, immutable IDs, protected preferences, archive lifecycle, writer/browser/data/installation exclusion, coordinated launch, game updating and recovery |
| Upstream mod | `D:/dev/netniv/stfc-mod` | Public mod implementation, its platform bootstrap/loader, produced runtime/configuration contracts and artifacts |
| Downstream mod | `D:/dev/stfc-mod` | Fork integration/build/runtime validation and its own producer artifacts; extracted shared TOML source remains owned by its producer repository |

These are existing checkouts, not instructions to clone, switch branches or
create worktrees. Cross-repo source, review, tests and continuity use the owning
canonical cwd and scope. Resolve actual native Mac checkout bindings separately.
The existing Swift Mac launcher remains reference implementation in its owner;
do not move it or game runtime source into Bridge.

Bridge may retain private UI preferences such as the last selected profile.
Those preferences are not a second catalog or account selector for another host.
Shuttle Bay and Engineering consume the same installation/profile/session
identities and application operations. Names, saved UI choices and a profile's
next-launch preference do not establish the target of an admitted operation.

Canonical behavior is defined by Profiles' accepted
[catalog contract](https://github.com/Guffawaffle/stfc-profiles/blob/aeef4861693613fab119edcd03117b4e8de01d4b/docs/PROFILE_CATALOG_CONTRACT.md)
and [installation contract](https://github.com/Guffawaffle/stfc-profiles/blob/aeef4861693613fab119edcd03117b4e8de01d4b/docs/GAME_INSTALLATION_CONTRACT.md).
Later accepted contract changes must be reflected in both producer and consumer
packets. Historical prose and test results are not authorization to restore a
removed installation-marker selector, central profile index or fallback account.

## Profiles consumer contract

br-05 supplies the Rust consumer of the existing versioned C allocation ABI:

| Export | Consumer obligation |
| --- | --- |
| `stfc_profiles_catalog_request_v1` | Supply bounded UTF-8 JSON; distinguish ABI failure from an allocated structured operation failure; use typed JSON `apiVersion: 2` where required |
| `stfc_profiles_free_v1` | Free every returned response/error with the originating module allocator |
| `stfc_profiles_acquire_data_lease_v1` / release | Retain the profile-data lease while reading/saving owned configuration; it does not reserve a game session |
| `stfc_profiles_acquire_installation_lease_v1` / release | Respect shared installation admission and retain required launch custody through the exact child lifetime |

The loaded native module stays alive until every originating buffer and lease
has been released. Validate API versions, JSON shape/size, non-ASCII paths,
module identity and errors at the boundary. Do not unload a module because a UI
request ended while a worker or native handle still uses it.

Profiles remains the single catalog/storage/game-update owner. Bridge must not
reimplement its authoritative index, account store, patch engine or writer lock.
Ordinary and isolated profile kinds dispatch explicitly. Ordinary launches keep
ordinary state; an explicit isolated request isolates the requested immutable ID
or fails. Isolation readiness does not establish logged-in account identity.

The current C ABI is synchronous and exports no cancellation operation.
A canceled await, client disconnect or closed webview does not stop a native
transaction. The Rust operation owner retains worker/module/resource lifetime,
reports actual journal progress/outcome and distinguishes cancellation request,
safe cancellation, completed commit and recovery required. Any new native
cancellation API requires a separately reviewed Profiles contract.

## Shared TOML consumer contract

br-05 also supplies an explicit native consumer of the producer-owned offline
TOML component. Its immutable source revision, component subdirectory, recipe
lock, API and per-target binary identities become reviewed dependency inputs.
Configuration schemas/capabilities remain produced by each mod distribution;
Bridge renders and validates the selected producer's supported contract.

Settings/Data Sync preserve staged Save/Discard, stale revision detection,
unknown data preservation and durable writes. Profile-owned settings use the
Profiles data lease; ordinary settings use the selected installation's scope.
Changing UI selection cannot retarget an already prepared or admitted save.
Do not substitute a new parser's successful round-trip for conformance to the
producer's configuration semantics and shared TOML contract.

## Observed inputs and adoption boundary

The implementation branch `feature/rust-bridge-231` started from Bridge
`origin/main` at `aad5fda716d9cb402965c294d7bc33b061ab1d8c`.
That base contains neither `dependencies/stfc-profiles-source-pin.json` nor
`dependencies/stfc-toml-source-pin.json`. The following immutable input
observations came from the earlier named-launch feature checkout at
`b46e6d1ba882386956a997fb888c33dd066787be`; they are not current Rust pins:

| Component | Observed source input | Observed source archive SHA-256 |
| --- | --- | --- |
| Profiles | `Guffawaffle/stfc-profiles`, `aeef4861693613fab119edcd03117b4e8de01d4b`, XMake `3.0.8` | `b556a158519275c47fdb275f288b567eb350baf305c68b9d89de2bc6fbaca49e` |
| TOML | `Guffawaffle/stfc-mod`, `f83c8d55b542cb4b8ac4a85ca5d69173c0cd58b0`, subdirectory `shared/toml`, XMake `3.0.8` | `0ce4b510eb6c6040c0974a6ce292596f33164ff46ae9b0314254eefc2dc3204c` |

br-05 reviews/adopts the exact compatible inputs or records a newer immutable
candidate. br-08/br-10/br-11 may produce new Profiles revisions; br-23 must pair
the final accepted dependency revisions with actual package bytes. Successful
tests of the observed old revision do not qualify changed native source.

Source pins identify the exact repository/revision/archive and recipe inputs.
Per-target build receipts additionally identify toolchain, mode, architecture,
dependency inputs and produced hashes. Final signed package receipts bind the
actual shipped bytes; signing can change a binary hash. Local source overrides
must be explicit, record revision/dirty state and remain development inputs.
No release pin may depend on an unspecified local checkout or moving branch.
Retain applicable GPL/source license text, attribution, notices and source
provenance in reviewed packaging.

## Producer and platform prerequisites

| Package | Owner | Required contract/work |
| --- | --- | --- |
| br-07 | Bridge | Native Mac services behind application ports; observe exact process, bundle/filesystem, permissions and signature behavior without duplicating Profiles authority |
| br-08 | Profiles | Mac physical installation registration/custody, ordinary setup kind/owner identity, Keychain foundation and shared exclusion semantics |
| br-09 | Upstream producer | Explicit installation/profile/config/log inputs, safe argv/environment forwarding, immutable Profiles integration and qualified loading/artifact contract |
| br-10 | Profiles | Coordinated Mac ordinary/isolated launch, exact session readiness and writer/browser/installation lifetime; depends on br-08 and br-09 |
| br-11 | Profiles | Mac official-game status/check/update/recovery route and admission; depends on br-08 |
| br-17 | Bridge | Application integration of qualified Mac session/game capabilities; depends on br-10/br-11 and its native consumer/platform packages |
| br-23 | Bridge | Per-target native module/companion pairing, Bridge package identity and signing/notarization |

Observed Profiles source explicitly refuses Mac physical registration and
ordinary setup (`platform_unavailable`), named coordinator launch
(`launch_unqualified`) and direct game update (`unsupported_platform`). Native
Keychain/browser source exists, but actual consent, loading and lifecycle
qualification remain separate. Current Upstream's loader selects the official
launcher INI target and executes an argument list containing only the game
executable; it does not satisfy the new explicit request contract.

Upstream's observed checkout has no `shared/profiles` integration directory.
br-09 must identify the actual producer bootstrap/patch/config/build changes and
immutable Profiles dependency rather than assume that directory already exists.
Reusable profile hooks remain in Profiles and are composed once into the owning
product bootstrap. The minimal profile-only runtime remains a Profiles product.
Full-mod and profile-only distributions remain mutually exclusive within one
installation; distribution switching preserves shared per-user data.

Profiles native work belongs to `src/macos/**`, shared `src/catalog*` /
`src/installation*`, `adapters/community_mod/**`, `bootstrap/**` and `cli/**`
as required. br-08/br-10/br-11 share composition, tests and qualification scripts.
Lease overlapping files/resources explicitly and serialize their writers;
dependency readiness does not authorize simultaneous edits. Windows and Mac
session/game integration likewise share application composition with one owner.

Each provider has independent platform/client/artifact evidence. A compatible
loader for one provider does not qualify another provider's runtime. Bind an
injected artifact to the actual game-process architecture; an arm64 Bridge shell
cannot authorize an x86_64 runtime for an arm64 game or claim Rosetta parity.

## Independent trust and handoff boundaries

Keep Bridge self-update, provider/runtime deployment and official-game update as
three independent trust domains. Tauri updater signatures and Bridge package
signatures do not establish mod source provenance or official-game integrity.
Embedded self-description is not independent artifact authentication.

An implementation packet names the owning repository/issue, exact inputs,
declared write scope, shared resources, commands/cwd, acceptance and required
receipts. Cross-repo dependencies transfer accepted immutable revisions,
contracts and artifact evidence, not writable checkout authority. Preserve
unrelated dirty work and retain continuity in each owner's existing scope.

Native CI is an available route for Mac compilation and synthetic checks.
Actual game/runtime/package/user-interface evidence follows
[NATIVE_QUALIFICATION.md](NATIVE_QUALIFICATION.md). Missing live target or signing
bindings block the relevant qualification/release packages while independent
mock UI, pure engine, contract and Windows work can continue.
