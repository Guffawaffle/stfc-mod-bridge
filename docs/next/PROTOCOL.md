# Bridge protocol v1

Owner: br-02, [issue #236](https://github.com/Guffawaffle/stfc-mod-bridge/issues/236).
Status: implementation in progress; codec checkpoints do not accept
the full protocol/scenario package or qualify any native capability.

Prepared plans are single-use on admission, including a lost admission reply.
An exact admitted Commit input replays the original durable operation before
host, expiry, preparation or close lookup; unavailable persistence can still
refuse an exact replay. A fresh key with a consumed current-host PlanRef refuses
PlanExpired on an open, healthy host, even after cancellation or completion;
obtain a new plan. PersistenceFailed and an armed host's OperationBusy precede
fresh-key plan lookup. Pre-admission refusal retains a live preparation. Expiry retires
only the exact preparation; foreign-host and forged refs do not evict it.
This intentionally tightens earlier fresh-key reuse without changing v1 DTOs.
The independent transcript checker enforces consumption and replay ordering,
including successful close requests scoped to their host epoch. A persistence
refusal preserves prior relationships and proves no admission or internal
poison state.

Rust DTOs in `crates/bridge-contracts/src/v1` own requests, replies, events and
their schemas. Schemars explicitly exports Draft 7 deserialize schemas. The
root-owned exporter and `scripts/next/generate-protocol.mjs` generate schemas,
one TypeScript declaration file and a byte-digest manifest. Generated output is
checked for exact drift; there is no handwritten frontend DTO/schema copy.
Schemas contain only local references and generation has no network retrieval.

The unreleased v1 client and dispatcher add `get_draft` together. Its input is
the immutable host epoch and draft ID; its output is the actual current draft
observation and actor cursor. It never opens another draft or allocates an ID.
Old closed-union decoders reject this query rather than interpreting it as an
existing command. This paired change does not promise compatibility with an
older draft client. A draft read watermark does not advance the global event
cursor: every operation and draft event must still be consumed in order.

## Framing and validation

One message is one UTF-8 JSON value, bounded to 256 KiB and 32 nested containers.
The root object has container depth 1; scalar children do not increase it.
Decoded duplicate keys, trailing input, malformed UTF-8, lone Unicode surrogates
and unknown fields/tags reject. Envelopes and nested DTO objects must be objects;
legal list fields remain arrays. `deny_unknown_fields` alone does not prevent
Serde's array-form struct decoding.

The Rust decoder first checks framing/duplicates/depth and version routing,
then validates against a cached schema derived from those same DTOs, then
deserializes and checks semantic relationships. It uses the pinned jsonschema
validator with default features disabled and explicit offline retrieval. Only
validated wrappers enter the future dispatcher. Schema error text, native error
text and raw requests are excluded from structured errors. An unsupported
version can report supported version 1 without interpreting that version's body.
A request ID is echoed only if it was independently validly established.

Close replies and `host_close_deferred` events allow at most 128 obligations:
one exact pending-operation revision and one retained session binding for each
of the 64 operations in a complete snapshot. Deferred projections cover every
noncompleted operation, including safe recovery alongside an unsafe worker or
session. They cannot omit work to fit a collection bound. Rust, browser goldens
and the engine's full-capacity regression verify this reply/event pairing.

The shared browser/Node parser is `contracts/codec/strict-json.mjs`. It preserves
object keys without prototypes and checks duplicate decoded spellings before
insertion. Browser schema validation uses the generated schema without coercion,
default injection, property removal or external reference loading. The pinned
`ajv-formats` validator checks full RFC3339 calendar/time values alongside the
Rust-derived UTC-only pattern; missing format support blocks validation.

JSON numeric tokens in v1 are lexical safe integers. Fractional/exponent tokens,
negative zero and integers outside JavaScript's safe range reject before numeric
conversion. Version/PID fields remain bounded numeric integers; u64 counters and
revisions use canonical decimal strings. Fractional provider values will use
explicit typed canonical strings. This prevents browser rounding/underflow from
silently converting a fractional token into a Rust integer field. Omission and
allowed optional null values normalize through the typed DTO before hashing.

## Identity and execution boundary

Installation selectors, ordinary/isolated selectors and exact session bindings
remain distinct from observed physical resources. A session includes PID, native
process generation and executable/installation identity. Registration preference,
UI view or first inventory entry cannot retarget captured work. DTO syntax does
not establish physical identity, access, exclusion, account identity or runtime
qualification; those checks belong to the engine/canonical native owner.

Preparation captures typed intent, target/preconditions and effects, binds a
host epoch and semantic review digest, and grants neither locks nor permission.
The semantic hash profile uses normalized typed values and deterministic object
keys; ordered arrays retain order. Any semantically unordered collection needs
explicit normalization and duplicate rejection rather than a blanket sort.

An admitted commit replay is looked up before expired/old-host preparation
lookup. Equal normalized input returns its existing operation; conflicting key
reuse rejects. An uncommitted old-host preparation remains invalid. The br-02
transcripts express that contract. br-04 must prove real durable admission,
custody and replay ordering with controlled ports; fixtures alone cannot do so.
CLI prepare and commit share an in-process session or workflow invocation.

Runtime, official-game and Bridge-update references retain separate trust
domains. Configuration/data-sync DTOs, all scenario fixtures and both
declared package suites are required before accepting br-02. Executable engine
port traits belong with their actual engine implementations; no wire payload
serializes native handles or pretends an unimplemented producer route exists.

The br-02 port deliverable is the typed data crossing those boundaries: canonical
resource references, observations, capabilities, captures, custody obligations
and owner-labelled recovery references. Executable Rust port traits belong to
the engine package that implements and tests their lifecycle. Wire DTOs cannot
hold native allocations, retained locks or module-owned handles.

Saved profile preferences accept registered installation IDs. Directory launch
overrides remain separate selections. A profile edit captures whether the prior
preference is kept, cleared or explicitly set; its result must account for that
choice. Resume/existing isolated store modes operate on the existing immutable
profile ID during launch. Profile creation cannot invent an account-copy or
setup-token route that the canonical catalog does not supply.

Protected endpoint/token entry and export-destination selection have shared
versioned commands. Their results echo the exact draft/field or reviewed preview
binding and distinguish captured opaque custody, cancellation and unavailability.
The renderer does not fabricate native references. Implementing those entry
ports, revision rechecks and custody belongs to the engine/native packages.
The protocol declarations alone do not implement native dialogs or capture data.

`set_draft_changes` acknowledges the exact accepted input, a checked successor
snapshot, and a closed list of protected-reference transfers. Its new draft
revision is exactly the previous revision plus one. Only explicitly transferred
private/secret references may change within the accepted edit set; their field,
purpose and native value revision remain bound. Replaying that exact staging
input returns the same acknowledgement and successor rather than advancing
again. Saved private references retain their captured document scope. Browser
correlation preserves Rust optional-None equivalence only in known binding DTOs;
public edit values and ordering remain exact.

Each Data Sync type declares its endpoint, secret and optional proxy field IDs.
Those roles must be distinct declared fields with the required sensitivity.
New destinations explicitly name their supported mode. The frontend does not
infer roles from labels, field ordering or legacy mode.

Before adopting a diagnostic preview, the browser client correlates its target,
disclosure and typed facts, and checks the Rust diagnostic digest profile
`bridge-diagnostic-preview-json-v1` with its NUL domain separator. Optional None
values normalize only within that closed DTO tree. Redacted previews cannot
contain disclosed paths. Hash completion after timeout, abort or disposal does
not revive an abandoned observation. Cancellation dispositions correlate exact
operation ID, expected revision and observed state. Recorded recovery references
also retain their original operation and prepared-capture relationship.

The canonical direct game updater restores the prior image after interruption.
Its recovery binding identifies the checked current client. Offered client bytes
are the successful update result, not a rollback result. Other native update
routes require their own recovery disposition and qualification.

Bridge update recovery validates its nested application binding at every wire
ingress. An interrupted update can retain the exact reviewed current or offered
application; a third application, channel or platform cannot replace that
binding. Standalone errors and close obligations validate internal consistency
without inventing a reviewed selection context that their payload does not hold.

Installation recovery projections require the same registered/directory kind,
physical installation and opaque native target. Registered projections also
require the same registration ID. A retained recovery may carry an older
registration metadata revision for that same identity; changing a name does not
make its journal foreign. Opaque references cannot prove physical equivalence
between different native resources or between a directory and registration.

## Package acceptance criteria

BR02-01: strict actual Rust decoding and the independent browser parser reject
malformed, unsupported and contradictory wire data, with generated schema/type
drift checks and adversarial Rust tests.

BR02-02: typed correlation IDs, operation revisions, snapshot watermarks and
event sequence scopes are validated. Synthetic transcript checks reject
contradictory complete snapshots and unannounced stream gaps.

BR02-03: paired transcript vectors require exact admitted commit input replay to
return its existing operation and reject conflicting idempotency-key reuse.
These vectors describe the contract; br-04 must implement durable admission.

BR02-04: preparations capture reviewed selectors, revisions, effects and source
identity. Fresh commits cannot reuse an old host preparation; admitted replay
is modeled before old-host lookup. Syntax and fixtures confer no native custody.

BR02-05: generated backend facts and action availability remain independent of
renderer controls and native window handles. Sensitive input and destination
commands return typed opaque references with explicit cancellation/unavailability.

BR02-06: the complete deterministic catalog supplies accepted paired exchanges,
domain refusals and relationship failures for all 18 declared scenarios,
including ready, missing, unavailable, stale, busy, failed and recovery states.
Both protocol-contract and scenario-catalog suites are required for package
acceptance. Engine execution and native/release qualification remain separate.

## Development utility

`bridge-protocol` exports schemas or decodes synthetic fixtures. It performs no
application dispatch, mutation, native/game operations or persistence and is
excluded from production packaging. It is not the later CLI/AXF application host.
Its decoder output is development data; production diagnostics still require
their reviewed redaction and disclosure boundary.

Protocol fixture/schema acceptance establishes wire conformance. Physical
target checks, concurrency, journals, native ABI, installed accessibility and
live game journeys keep the separate proof layers in SCENARIOS.md.
