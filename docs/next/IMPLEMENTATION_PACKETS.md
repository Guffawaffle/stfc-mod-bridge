# Current implementation packets

The completed local protocol/client/kernel checkpoint is staged tree
`3ee50ac769a4d650689ac3c56e5ef69d1c42eba7`, based on
`aad5fda716d9cb402965c294d7bc33b061ab1d8c`, with patch SHA256
`243810a5f09d82e9e5e74db5a8297333fa02418f953bf8562f47f61fbd55491b`.
General, hostile and domain correction reviews are clear. Its sequential
LexRunner br-00 through br-04 integration gates passed. This is a staged local
engineering checkpoint; signed-head, canonical native custody, Mac execution
and release qualification remain outstanding.

## br-05 / issue 239

Bridge owns the Rust consumers. Profiles and TOML remain independent producer
authorities and independent consumers; neither consumer depends on the other.
The packet explicitly adds `crates/bridge-native/**` to the planned br-05 scope
for a small common loader. It owns module identity and loading only, not native
catalog, configuration, process or transaction policy.

| Writer | Scope |
| --- | --- |
| Root | `crates/bridge-native/**`, all Cargo manifests/lock, `dependencies/next-*.json`, native fixture/gate scripts, CI and documentation |
| Profiles worker | `crates/bridge-profiles/src/**`, `crates/bridge-profiles/tests/**` |
| TOML worker | `crates/bridge-toml/src/**`, `crates/bridge-toml/tests/**` |

Foreign-code adoption and exact symbol binding are explicit unsafe boundaries.
Closed backend manifests select reviewed producer bytes; a renderer supplies
neither native paths, symbol signatures nor digests. Verify regular physical
files, confinement, hash, native-process architecture and actual symbol origin.
Disk hashes and origin observations do not attest the in-memory image, code
signature, game process or release. Loading runs producer initializers.

Originating function tables retain an `Arc<LoadedModule>` through every buffer
and lease. Public module/client/lease handles are deliberately not Send or Sync;
no unsafe thread-safety implementation is allowed. Later dedicated actors must
create, invoke and drop the handles on their owning thread. Synchronous native
work and its retained custody survive an observer's loss or timeout.

Profiles JSON remains API 2 through the existing v1 C allocation ABI. Its
response has no length export or producer-side size cap. Bounded scanning still
depends on a trusted producer's readable allocated memory through the NUL.
Malformed-response tests use genuinely allocated memory, never invalid pointers.
Its data lease is shared BrowserLease access, not a new exclusive configuration
writer. Its installation lease has no authoritative physical/session getter.
Positive installation-lease probes use a unique private physical fixture but
create a narrow lock in the actual user's canonical catalog `.locks` directory;
an explicit temporary catalog root does not isolate that footprint. Record the
exact fixture and lock, release the lease and never wipe the shared catalog.

Historical receipt-backed Windows producer modules can be explicitly adopted
for consumer ABI probes. Their missing resolved-recipe evidence and any native
Mac input remain qualification gaps. Source-tree equality does not establish
new binary bytes. No game, configuration, deployment or lifecycle action belongs
to this packet. Cargo and producer XMake writes are serialized by root.

## br-12 / issue 240

Reuse the accepted client, WorkContext and ObservationStore as the sole selected
target/draft/operation/navigation model. Frontend layout and copy remain editable
in a browser without Rust, native modules or a game.

| Writer | Scope |
| --- | --- |
| Primitives worker | `ui/src/components/**`, `ui/src/styles/**`, its new component tests |
| Facade worker | `ui/src/state/**`, `ui/src/app/**` except root gallery composition, its new controller tests |
| Root | `ui/src/App.svelte`, gallery HTML/entry/composition, package/lock/Vite, actual browser qualification scripts, CI and documentation |

The facade packet narrowly adds `ui/src/client/work-context.ts` for guarded
acknowledgment of staged edits. A current immutable review captures the old draft
and local edits. Only an exact, current, same-binding backend acknowledgment may
advance its scoped revision while retaining queued navigation and busy ownership.
Refused, foreign, stale or uncertain replies preserve the edits. Preparation and
explicit commit use the acknowledged backend draft; admission is not completed
Save. Only the actual completed old-draft result permits queued navigation.
Exact replay, local review and edit intent comparisons remain strict.

Require native semantic controls, actual modal keyboard/focus/restoration
behavior, accurate confidence/progress, theme/contrast/reduced-motion/text-scale
and long-label evidence. The gallery and synthetic fixtures remain development
inputs outside the production graph. Browser evidence cannot qualify installed
native window conventions, Narrator or VoiceOver.
