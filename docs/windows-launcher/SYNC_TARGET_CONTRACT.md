# Windows Launcher Sync Target Contract

Status: locked for the `Windows Launcher: Sync Setup` campaign

Contract artifact: `sync-target-contract.guffawaffle.v1.json`

Compatibility corpus: `sync-target-corpus/cases.json`

## Purpose

This contract fixes the vocabulary and resolution rules shared by the native mod and Windows launcher before the
launcher grows a multi-target editor. It separates what the user wrote from what the runtime can use, and prevents the
UI, persistence layer, and native parser from independently inventing sync semantics.

This slice is descriptive and executable evidence. It does not add target editing, migration writes, or new native
transport behavior.

## Topology

The launcher owns two related views:

- **Desired topology** is a source-faithful projection of the TOML. It includes global defaults, the fixed local
  sidecar slot, named external targets, explicit values, and invalid or unsupported entries with diagnostics.
- **Resolved topology** contains only runnable targets after validation, inheritance, safety policy, and legacy
  compatibility have been applied. It records the source provenance of every effective value.

Loading configuration never changes the desired topology or the TOML. A resolved target synthesized from legacy
`[sync].url` and `[sync].token` is compatibility state, not permission to rewrite the source.

## Target kinds and presets

| Kind | Persistence | Cardinality | Wire contract | Notes |
|---|---|---:|---|---|
| Local Sidecar | `[sidecar.sync]` | Zero or one | Local sidecar ingest | Existing-configuration-only in the ordinary UI; fixed identity and no external-network-policy inheritance. |
| Ordinary Sync (compatibility kind `legacy_community`) | `[sync.targets.<name>]` | Zero or more | Established STFC sync JSON | Creatable; persisted `mode` is absent, empty, or `legacy`. |
| Majel advanced mode | `[sync.targets.<name>]` | Zero or more | `majel.ingest.v1` envelopes | Hidden from the ordinary UI and preserved as an advanced TOML-only wrapper. |

Provider names are presets, not protocol types:

- **Spocks Club** creates an ordinary destination named `spocksclub`, prefills
  `https://spocks.club/sync/ingress/`, and applies its nine documented feed defaults.
- **Next Spocks Club** creates an ordinary destination named `spocksclub-next`, prefills
  `https://next.spocks.club/sync/ingress/`, and applies its thirteen documented feed defaults.
- **Custom sync** creates the same ordinary kind without provider branding.

The local Sidecar appears only when `[sidecar.sync]` is present. It is never synthesized by opening or saving the page
and is not represented as `[sync.targets.sidecar]`.

## Field and capability matrix

`I` means the external target may inherit the global value when absent. `E` means the setting is explicit on that
target kind. `F` means forbidden. A dash means the field is not part of that target kind.

| Field/capability | Global `[sync]` | Local Sidecar | Legacy/community | Majel ingest |
|---|---:|---:|---:|---:|
| Target identity | - | Fixed | E | E |
| Enabled state | - | E | Launcher desired state | Launcher desired state |
| URL | Compatibility only | E | E | E |
| Token | Compatibility only | E | E | E |
| Mode | - | - | E (`legacy`) | E (`majel`) |
| Proxy | Default | E, never inherited | I | I |
| Verify TLS | Default | E, never inherited | I | I |
| Unsafe TLS opt-in | Default | E, never inherited | I | I |
| Standard sync categories | Defaults | - | I | I, transport-filtered |
| Realtime battle logs | Default | E, never inherited | I | I |
| Fleet runtime | Not routable externally | E, never inherited | F | F |
| Battle-log enrichment | - | E, never inherited | - | - |
| Fleet runtime mode | - | E, never inherited | - | - |

External `enabled` is launcher desired-state metadata until the native config grows a canonical persisted field. The
launcher must not invent an unrecognized TOML key. In the initial editor, disabling an existing external target is a
pending edit that requires an explicitly selected supported persistence strategy; merely viewing or loading it does
nothing.

Majel targets use the same category inputs as legacy targets, followed by the native transport capability filter.
Majel-only capability and fleet-assignment snapshots are transport behavior and are not new inherited user fields.

## Inheritance and provenance

Inheritance is field-scoped, not table-scoped. For each resolved field the launcher records one of:

- `inherited`: the target omitted the field. External targets resolve it from `[sync]`; non-inheriting target kinds
  resolve it from their own safe type default. Resolved values retain that source distinction.
- `explicit_value`: the target supplied a value, including `true` and non-empty strings.
- `explicit_false`: the target explicitly supplied boolean `false`; it overrides global `true`.
- `explicit_empty`: the target explicitly supplied an empty string where empty is meaningful; it overrides a non-empty
  global value. This is permitted for `proxy` and means no proxy.

URL, token, target identity, enabled state, mode, and type-only settings never inherit. An absent required URL or token
is not equivalent to an inherited credential. Secrets must not flow between targets.

The local sidecar never inherits external proxy, TLS, unsafe-TLS, or category defaults. Its values come only from
`[sidecar.sync]` and native sidecar defaults. This prevents an internet proxy or relaxed external TLS policy from being
silently applied to loopback ingest.

## Validation and compatibility rules

Resolution uses these stable outcomes:

- `error`: target is retained in desired topology but excluded from resolved topology.
- `warning`: target may resolve, but a field is ignored, normalized, or deprecated.
- `info`: compatibility behavior was applied without changing the source.

Rules:

1. An external target requires string `url` and string `token` assignments. Missing or invalid credentials produce an
   error and the target is not runnable. Empty credentials are displayed as incomplete and are never inherited.
2. External loopback sidecar ingest URLs, `[sync.targets.sidecar]`, and `mode = "sidecar_broker"` are errors. The
   corrective destination is `[sidecar.sync]`.
3. External `fleet_runtime = true` produces a warning and resolves to false. Fleet runtime is sidecar-only.
4. Unknown target modes warn and resolve as legacy for native compatibility. The launcher must not silently rewrite the
   unknown source value; an explicit user correction is required.
5. When both non-empty legacy `[sync].url` and `[sync].token` are present and the URL is not a rejected loopback sidecar
   endpoint, resolved topology synthesizes a legacy/community target named `default`. The source remains untouched.
6. One missing legacy root credential produces a warning and no synthesized target.
7. If both a named `sync.targets.default` and legacy root credentials exist, the named target wins and the failed
   conversion is diagnosed.
8. Unknown keys, comments, ordering, line endings, and supported formatting survive all unrelated edits.

## Migration and mutation policy

- Reads, validation, preview, target selection, and resolved-value display are non-mutating.
- Existing valid TOML is never normalized merely because the launcher opened it.
- Legacy root conversion is virtual until the user requests migration, reviews a source diff, and confirms one atomic
  write.
- Invalid and unsupported entries remain in the source unless the user explicitly edits or removes them.
- Sparse writes touch only the selected assignment or target block and preserve unknown content.
- A stale revision, duplicate table, unsupported TOML construct, failed backup, or failed verification aborts the
  entire write.
- Presets create a desired draft. They do not send traffic or persist credentials before confirmation.

## Security and secret display

- Tokens are secrets. List and summary views show only configured/missing state, never token contents.
- A detail editor may reveal a token only through a deliberate transient action; it must not place the value in logs,
  diagnostics, telemetry, error text, screenshots generated by automation, or campaign fixtures.
- Proxy user information is masked in summaries and logs.
- Copying or exporting diagnostics always redacts tokens and proxy credentials.
- `verify_ssl = false` is effective only with the explicit unsafe-TLS opt-in accepted by native policy. The launcher
  presents this as a high-risk pair, not as an ordinary convenience toggle.
- Sidecar loopback credentials remain secret even though the endpoint is local.
- Presets contain no real URLs or credentials unless the provider contract publishes a stable public endpoint.

## Corpus authority

`sync-target-corpus/cases.json` enumerates the compatibility cases required by issue #223. Each TOML fixture is valid,
source-preserving input. Its expected target kinds, provenance states, and diagnostic codes are declarative inputs for
the LS-003 domain resolver and LS-004 persistence tests.

The LS-002 test suite currently enforces:

- every required case and fixture exists;
- every fixture is readable by the launcher's conservative TOML surface without byte mutation;
- declared paths exist exactly as written;
- diagnostic identifiers and provenance states come from the locked machine contract;
- security, non-inheritance, and sidecar-only invariants cannot drift unnoticed.

Later slices must consume this corpus rather than copying the examples into new private test data.
