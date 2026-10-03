# Bridge TOML editing engine

This is the accepted implementation contract for replacing the limited managed
TOML grammar. Delivery and artifact qualification are recorded separately; read
the current source and dependency pin before claiming the engine is deployed.

The authoritative shared contract belongs to the mod repository:
[Shared TOML edit contract](https://github.com/Guffawaffle/stfc-mod/blob/f83c8d55b542cb4b8ac4a85ca5d69173c0cd58b0/docs/SHARED_TOML_EDIT_CONTRACT.md).
The mod and Bridge consume the same pure preparation engine. Bridge bundles a
standalone hash-paired native adapter from an immutable producer source archive.
Editing works with the game stopped and does not call a provider's runtime DLL.
Profiles retains catalog/session ownership; TOML parsing is not a profile service.

## Read and prepare

Accept complete valid TOML, including quoted tables/keys, decoded Unicode and
literal dots in keys, inline/dotted tables, multiline values and unrelated arrays
of tables. Preserve raw value spelling separately from normalized semantic values.
Use decoded path segments and unambiguous canonical rendering for identity;
never treat a literal dot in a quoted key as a namespace separator. Existing
schema identities and the Data Sync target-name domain policy remain unchanged.

Validate document syntax independently of whether a particular edit can be
prepared. Refuse malformed or duplicate definitions, injected TOML values,
conflicting source revisions, or operations whose complete intended meaning
cannot be verified. Array traversal without an explicit index is an operation
limitation, not a reason to block editing unrelated fields. Native unavailability,
byte-hash mismatch and ABI mismatch must report an unavailable editor, never an
unsafe user document or an implicit fallback to the retired managed grammar.

Supported host operations set/clear an override and remove/rename a table subtree.
Preparation preserves unrelated bytes, including comments, ordering, whitespace,
BOM and line endings. Every proposed result is reparsed and compared with the
complete intended semantic document before any bytes can reach a commit.

## Commit ownership

Bridge's existing ConfigurationWorkspace, repository and atomic store continue to
own staged Save/Discard, immutable profile/path/runtime binding, expected-revision
checks, verified protected backups, mutation admission and atomic promotion. The
native component never opens files or grants authority to write them. Runtime mod
queuing and typed expected-value conflicts remain separate host responsibilities.

Unavailable edits preserve drafts. Browsing, scrolling and help remain available.
Read-only projections must distinguish saved values from provider defaults.
Raw TOML remains a source-bound escape hatch, with the same selection/revision
rules. Opening either editor never normalizes or saves the user's document.

## Qualification

Exercise synthetic documents containing valid unusual syntax, exact decoded-key
identity, full semantic-change verification and source-preserving scalar,
collection, removal and structural edits. Preserve existing backup/CAS/conflict,
profile/runtime binding and Data Sync transaction tests. Tests explicitly bind
native path and hash; production resolves only its bundled exact-byte adapter.
Package and release allowlists, signing order, dependency notices and source/recipe
SBOM inventory must include the actual shared native bytes. No compatibility
parser, capability shim or whole-document rewrite is retained.

String consumers use native typed decoding: TOML basic-string escapes such as `\U0001F680` are not JSON syntax. The native `decode_string` operation validates exactly one string and returns decoded Unicode in its JSON response; Bridge does not maintain another TOML escape parser.
