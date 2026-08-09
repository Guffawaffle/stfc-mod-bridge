# Battle Bridge Local Storage Contract

Status: proposed SQLite v1 contract for Sidecar issue
[#61](https://github.com/Guffawaffle/stfc-mod-sidecar/issues/61); native
repository implementation remains in
[#63](https://github.com/Guffawaffle/stfc-mod-sidecar/issues/63)

## Decision

Battle Bridge stores current player data in one launcher-owned SQLite database
under `%LOCALAPPDATA%\STFC Mod Bridge`. The exact Battle subdirectory and file
name remain owned by the lifecycle/state-location contract in
[#59](https://github.com/Guffawaffle/stfc-mod-sidecar/issues/59); examples in
#63 must not turn a candidate `Battle\store-v1` layout into precedent. JSONL,
PostgreSQL, and MySQL are not normal runtime stores. A legacy JSONL or Sidecar
database import must stream accepted records through the same validation and
transaction boundary; the source is not copied into launcher state or kept as a
second live store.

For a battle record with successfully associated accepted capture evidence, the
v1 source of truth includes exactly one lossless, compressed complete
`battle.capture` as its canonical capture plus content-addressed catalog
documents. An unresolved/conflicting capture remains root evidence and does not
fabricate a canonical association. The event store also preserves partial
imported/supplemental evidence without fabricating a capture or battle row.
Reports, analytics, and complete catalog-snapshot events are treated as
versioned supplemental source evidence until #63 proves, field by field across
accepted fixtures, that they can be rebuilt exactly from the capture and catalog
document. Only evidence that passes that reviewed derivability gate may be
reclassified as a compact rebuildable projection and omitted on future ingest.
Any irreproducible field or family remains canonical compressed evidence or
returns to architecture review. A producer convenience envelope is not promoted
to source of truth merely because it exists, but neither is it discarded on an
unproven assumption.

Storage readability is independent from current producer capability. Losing or
switching the mod can stop collection, but a supported stored schema remains
readable. Database creation and provider initialization happen only after an
eligible Battle feature needs storage or the player explicitly starts an import.

## Non-negotiable invariants

- External identifiers are stored as `TEXT` and round-trip byte-for-byte after
  UTF-8 validation. They are never parsed through JavaScript or floating-point
  numbers. Each event stores normalized signed-64-bit UTC event and acceptance
  time in Unix milliseconds for deterministic ordering, while retaining the exact
  source timestamp text too. Overflow or an invalid required source timestamp
  rejects the event rather than clamping it.
- Event evidence records both SHA-256 of the exact uncompressed complete-event
  bytes and SHA-256 of the stored compressed BLOB. Reads verify the stored hash,
  then decompression length and raw hash, before parsing or projecting.
- Event identity has two levels. A source-namespaced logical event key correlates
  observations; an occurrence identity also includes the exact complete-event
  raw hash. Exact repeated bytes are one authoritative occurrence/no-op, but every
  accepting batch/import receipt is associated with it for retry provenance.
  Byte-different observations under one logical key coexist. Hash and length
  equality are checked before a byte comparison; a digest match alone does not
  define equality.
- Missing, corrupt, too-new, or unsupported storage never grants a runtime
  capability and never deletes or silently recreates player data.
- A retention action removes heavyweight evidence only through a visible,
  reason-bearing transaction. Long-lived metadata, byte counts, and both evidence
  hashes remain.
- Caches never become authoritative. Their key includes a deterministic ordered
  hash set of every source event used, projection schema, and implementation
  version, so stale values can be dropped and rebuilt.
- No whole-corpus materialization is required for ingest, history, detail, export,
  integrity, or cleanup. Input is bounded by the accepted ingest contract and
  processed one event at a time; corpus-wide integrity/export passes stream, and
  history is paged.

### Exact event byte boundary

For live `battle.capture`, and for every live supplemental family retained by the
derivability gate, the stored/hash input is the complete event object after:

1. outer `stfc.sidecar.ingest.v1` authentication and request-size enforcement;
2. bounded transport chunk reassembly;
3. outer protocol/kind and inner payload-protocol validation; and
4. duplicate-property, UTF-8, event-family, and schema validation.

It begins at the inner event object's opening `{` and ends at its matching `}`.
For a capture, that includes `protocolVersion`, `type`, `schemaVersion`, source
timestamp/session/mod/source fields, exact journal/battle IDs, battle type, and
the complete inner `capture` object. It excludes HTTP/chunk framing and the outer
batch envelope. The batch key, complete-envelope SHA-256, producer identity,
production time, and acceptance time remain in `ingest_batch`, so excluding the
duplicated wrapper does not erase provenance.

For import, the boundary is the complete event-entry byte slice inside the
reviewed, hash-verified source artifact after adapter/schema validation. It does
not require or invent a live ingest envelope. The import receipt owns source
artifact/adapter provenance. A legacy database adapter must read the stored event
JSON bytes when that store retained them. A PostgreSQL/JSONB exporter may have to
serialize a complete event into the reviewed export format; in that case the
hashed source is explicitly the export-entry bytes and any original producer-byte
claim is `unavailable`. It must not describe reconstructed bytes as the original
runtime bytes.

In both paths the validator must preserve the accepted event's source byte slice; hashing or
compressing only the inner `capture` member, a parsed object graph, or a
reserialized event is non-conforming. Whitespace and JSON lexemes are therefore
evidence. A semantically equal event with a different byte representation is a
distinct occurrence when delivered in a new valid batch; it is never an
overwrite. Reusing one derived live batch key with different complete-envelope bytes
remains a batch-level conflict that rejects the transaction.

For ordering, v1 retains the exact source `timestamp` string and also parses an
accepted RFC 3339 timestamp with an explicit `Z` or numeric offset into
`event_timestamp_unix_ms`, a signed 64-bit UTC Unix-millisecond integer.
Sub-millisecond precision remains in the exact source text; the normalized value
uses mathematical floor to the containing UTC millisecond. The repository
records its own UTC acceptance clock as signed 64-bit
`accepted_at_unix_ms`; receipt associations preserve later retry/import acceptance
times while the occurrence row retains first acceptance. Cleanup cursors order by
normalized event time, normalized acceptance time, then internal evidence ID; no
locale-formatted string participates in ordering.

### Event identity, retries, and imports

The current envelope has no event-level ID. V1 therefore stores both a logical
event key and an occurrence identity rather than inventing a producer field:

```text
LP(value) = uint32-big-endian(byte-length(UTF8(value))) || UTF8(value)
logical-input = LP("stfc.battle-logical-event.v1")
             || LP(type)
             || LP(schema-discriminator)
             || LP(key-kind)
             || LP(key-value)
logical_event_key = "logical-v1/" || lowercase-hex(SHA-256(logical-input))

occurrence-input = LP("stfc.battle-event-occurrence.v1")
                || LP(logical_event_key)
                || LP(raw-event-byte-count-decimal)
                || LP(raw-event-sha256)
occurrence_identity = "occurrence-v1/" || lowercase-hex(SHA-256(occurrence-input))
```

`raw-event-byte-count-decimal` and every other count used in an identity tuple is
unsigned ASCII base 10 with no leading zero except the single character `0`.
Every SHA-256 string passed to `LP` is exactly 64 lowercase hexadecimal
characters computed by Bridge over the declared bytes; a caller-supplied spelling
is never trusted, and uppercase or shortened spellings are not identity inputs.

The uniqueness constraint is `(source_namespace, occurrence_identity)`. The
logical key is indexed but is not unique: it deliberately groups byte-different
observations. Raw hash and byte count cover the exact complete-event boundary
above. On an apparent duplicate, Bridge verifies stored bytes before declaring a
retry while the payload remains present. A hash collision with different bytes is
a hard ingest conflict; an ordinary byte-different event has a different
occurrence identity and is retained. An authorized payload prune changes the
available proof and follows the explicit tombstone/rehydration contract below; it
is never silently labelled an exact-byte retry.

The live family matrix is closed in v1:

| Family | Schema discriminator | Logical discriminator and role |
| --- | --- | --- |
| `battle.capture` | Exact required `stfc.battle.capture.v1` | Required valid `journalId`; `key-kind = journal-id`. Optional `battleId` participates only in battle aliases. Occurrence is a canonical-capture candidate. |
| `battle.report` | Exact required `stfc.sidecar.battle-report.v0` | Required valid `journalId`; `key-kind = journal-id`. Every distinct occurrence is retained as supplemental evidence until derivability is proven. |
| `battle.analytics` | Exact required `stfc.battle.analytics.v0` | Required valid `journalId`; `key-kind = journal-id`. Every distinct occurrence is retained as supplemental evidence until derivability is proven. |
| `catalog.snapshot` | Exact required `stfc.catalog.snapshot.v0` | Required valid `journalId` under the current accepted source schema; `key-kind = journal-id`. Every distinct event occurrence and exact catalog object remain auditable. It may associate with an already-resolved battle but never creates one by itself. |
| Transitional `battle.event` | No source `schemaVersion`; adapter-owned `adapter/stfc.sidecar.battle-event.v0` | Accepted only by that reviewed legacy adapter. A valid `battleId` uses `key-kind = battle-id`; with no ID, `key-kind = raw-event-sha256`. It remains legacy supplemental evidence and cannot become a canonical capture. |
| Any other missing-schema or unknown family | None | Rejected from live v1 storage. A future reviewed adapter may import/export it without changing bytes, but must add an explicit matrix row/version before it becomes normal evidence. |

`schema-discriminator` is the exact required source `schemaVersion`, except for
the legacy row. The legacy adapter discriminator is stored as metadata outside
the unchanged complete-event bytes and never misrepresented as a source field. A
no-ID legacy event uses the lowercase raw complete-event SHA-256 as its logical
`key-value`; its occurrence identity still uses the same hash and byte count.

Current live `stfc.catalog.snapshot.v0` without `journalId` is rejected because
that field is required by the accepted producer schema; v1 does not quietly widen
it. A future truly session-only catalog needs a new schema/matrix row. A valid
current snapshot can still remain a source/session-scoped `catalog_observation`
with no battle association: its journal/battle aliases are looked up only against
an existing record, and the catalog never fabricates that record. The accepting
outer producer session is retained when the inner event omits `sessionId`.

Identifiers are exact non-empty accepted strings. No numeric parse, trimming,
normalization, or case folding occurs. A present ID that is empty, non-string,
invalid UTF-8, or outside the accepted family/bounds rejects the event; it is not
treated as missing. A missing schema-required ID also rejects. Length-prefixing
and domain/version labels make every tuple unambiguous. #63 must freeze golden
vectors for every family, legacy-adapter, discriminator, exact retry,
byte-different observation, and invalid/missing-ID case.

- At database creation, `store_meta` receives a cryptographically random UUIDv4
  `store_instance_id` in lowercase `D` form. It survives migration, backup, and
  restore. A live runtime namespace is
  `runtime-v1/` plus lowercase hex SHA-256 of
  `LP("stfc.battle-runtime-namespace.v1") || LP(store_instance_id) ||
  LP(distribution_id)`. The receipt retains the exact reviewed distribution ID;
  package/runtime upgrades do not change the namespace.

  Live v1 additionally requires exact non-empty outer `source`, `sessionId`, and
  producer `batchId`. The producer contract makes `sessionId` unique per producer
  process/session. Batch scope is unambiguous:

  ```text
  producer_scope = "producer-v1/" || lowercase-hex(SHA-256(
      LP("stfc.battle-live-producer.v1") || LP(source) || LP(sessionId)))
  live_batch_key = "batch-v1/" || lowercase-hex(SHA-256(
      LP("stfc.battle-live-batch.v1") || LP(source_namespace)
      || LP(producer_scope) || LP(batchId)))
  ```

  Distribution/authenticated runtime identity is already in `source_namespace`;
  source and session distinguish producer instances beneath it. `modVersion` and
  artifact version remain receipt provenance and never change retry identity.
  Another distribution, source, or session cannot collide merely by reusing a
  batch ID. Reusing all three producer values across distinct processes violates
  the accepted producer contract and is diagnosed rather than treated as a new
  producer. A transport retry preserves the values; occurrence identities are
  re-derived. The same live batch key plus complete-envelope byte count/SHA is a
  transport no-op; a different count/SHA rejects the entire batch. This is stated
  as cryptographic batch equivalence, not exact-byte proof of a wrapper Bridge
  deliberately does not retain. Accepted event occurrences still follow their
  stronger retained-byte/tombstone rules.
- A reviewed file/archive import namespace is `import-v1/` plus lowercase hex
  SHA-256 of `LP("stfc.battle-import-namespace.v1") || LP(format_id) ||
  LP(source_artifact_sha256) || LP(adapter_contract_version)`. The receipt retains
  every exact component. The adapter contract freezes one identity strategy:
  when it declares an optional trustworthy source-record key and that key is
  present, `key-kind = import-original-key` and `key-value = original-v1/` plus
  lowercase hex SHA-256 of
  `LP("stfc.battle-import-original-key.v1") || LP(original_key)`. Missing uses the
  entry locator below. Present-invalid or duplicate values reject the artifact;
  they never fall back. An adapter that cannot guarantee the key's exact,
  per-artifact uniqueness must use entry locators for every entry.

  Every source entry receives a zero-based unsigned ordinal in the adapter's
  reviewed deterministic enumeration order. Its canonical decimal follows the
  count rule above. A named entry uses
  `LP("named-entry") || LP(exact-entry-name) || LP(ordinal-decimal)`; an unnamed
  entry uses `LP("ordinal-entry") || LP(ordinal-decimal)`. In both cases,
  `key-kind = import-entry` and `key-value = entry-v1/` plus lowercase hex SHA-256
  of `LP("stfc.battle-import-entry.v1") || <locator-input>`. Locator kind and the
  always-present ordinal prevent a filename `12` from colliding with ordinal 12.
  Exact entry name, kind, and ordinal remain receipt metadata. Duplicate archive
  entry names are rejected before event ingest instead of relying on first/last
  library behavior; the adapter contract freezes decoding and exact-name
  comparison.

  Import identity overrides the live matrix's journal/battle logical
  discriminator only inside an `import-v1` namespace. Family/schema validation,
  unchanged-byte storage, and battle-alias extraction still use the event itself.
  The adapter derives the logical key from type, source/adapter schema
  discriminator, and its frozen original-key-or-entry discriminator, then derives
  occurrence identity from exact import-entry raw hash and byte count. Changing
  enumeration or key strategy requires a new adapter contract version and thus a
  new namespace. A repeated import of the same artifact through the same adapter
  is deterministic and idempotent. Any invalid component rejects the import; it
  is never omitted from or delimiter-concatenated into an identity.
- Import never impersonates live runtime evidence. Claimed original provider,
  database, host, session, or event identity is retained as untrusted provenance
  metadata. It cannot grant collection capability or provider trust.
- Every explicit import request that passes store readiness and reaches the
  repository creates `import-attempt-v1/<lowercase-UUIDv4>` and commits a
  synthetic `import_receipt` in `in-progress` state before event writes. It
  records source artifact hash/size/format, adapter contract/version, requested
  and completed UTC, counts, and bounded error summary. Terminal states are
  exactly `succeeded`, `partial`, `failed`, `cancelled`, or `interrupted`; each
  reports accepted/no-op/rejected counts. It stores no source path or credential.
  Imports commit accepted entries in bounded transactions linked to that receipt;
  a crash leaves an auditable receipt that startup marks `interrupted`, rather
  than an ambiguous partial transaction. Retry creates a new receipt, while the
  namespace/occurrence rules make already accepted entries no-ops. Receipt
  identity is separate from occurrence identity, so attempts remain auditable
  without duplicating evidence.

Within one namespace, the same occurrence and byte-for-byte comparison against an
active payload is a retry/no-op for the authoritative evidence row. A tombstone
uses the separately labelled rehydration semantics below. Each new live batch or
import attempt still gains an event-to-receipt association on acceptance, so retry
provenance is not discarded. Across namespaces, neither equal logical keys nor
equal battle/journal IDs authorize an overwrite or automatic semantic merge.
Exact event bytes may share a content-addressed BLOB, but each source occurrence
and every receipt association remains. Different cross-source bytes coexist and
may be correlated only through a versioned projection with visible provenance.

### Source-qualified battle identity and capture policy

Battle grouping uses exact aliases, not a delimiter-concatenated bare ID:

```text
alias_identity = "alias-v1/" || lowercase-hex(SHA-256(
    LP("stfc.battle-alias.v1") || LP(alias-kind) || LP(alias-value)))

battle_key = "battle-v1/" || lowercase-hex(SHA-256(
    LP("stfc.source-qualified-battle.v1") || LP(source_namespace)
    || LP(primary-alias-kind) || LP(primary-alias-value)))
```

Alias kinds are exactly `battle-id` and `journal-id`; exact values are retained.
Aliases are unique by `(source_namespace, alias_identity)`, and each battle record
may own at most one alias of each kind in v1. When both IDs are present, primary
precedence for a newly created record is `battle-id` then `journal-id`. The
initial `battle_key` is immutable even if a higher-precedence alias arrives later.

Resolution is deterministic:

1. No IDs means no battle record or association; a later ID-bearing occurrence
   does not retroactively attach the unkeyed evidence by time/session inference.
2. If no alias resolves for capture/report/analytics or another reviewed
   battle-bearing family, create one record from the highest-precedence alias and
   attach every supplied alias. `catalog.snapshot` is the explicit exception: it
   remains a session/source observation and creates no record or alias.
3. For a reviewed battle-bearing family, if all resolving aliases select one
   record and it lacks the other alias kind, attach the new alias. This covers
   journal-only then both-ID, and battle-only then both-ID, without renaming the
   record. A catalog may associate when its supplied aliases consistently resolve
   one record, but it never adds a missing alias.
4. If supplied aliases resolve different records, or a selected record already
   owns a different value of the same alias kind, preserve the occurrence as root
   evidence with `battle-association-conflict`, leave all records/aliases
   unchanged, and block affected derived battle projections. V1 never guesses,
   merges records, or creates one-to-many aliases automatically.

A later occurrence carrying only the other, previously missing alias cannot prove
association and follows rule 2, creating a separate record. If a subsequent
both-ID occurrence connects those aliases, rule 4 exposes the conflict; neither
earlier record is silently merged.

An exact retry only adds its new receipt association. The first accepted capture
occurrence successfully associated with a record fills its unique
`canonical-capture` relation. A later byte-different capture associated with that
record is retained as `conflicting-capture`; it does not replace the canonical
occurrence and blocks affected derived projections pending reviewed resolution.
Byte-different report, analytics, catalog, and legacy supplemental observations
coexist under their logical keys and do not become conflicts merely because they
are newer or different.

#63 golden cases must cover equal versus distinct alias values; equal text used as
both alias kinds; battle-only, journal-only, both IDs, neither ID, and each
later-ID direction; aliases resolving two records; a second value of either kind;
the other-only-later case; cross-source equal IDs; exact capture retry;
conflicting capture; and multiple byte-different supplemental occurrences.

Legacy PostgreSQL data crosses this boundary only through a separately reviewed,
hash-manifested export format and the import namespace above. Bridge does not
ship a PostgreSQL driver, accept a connection string, inspect a live server, or
copy PostgreSQL tables directly into SQLite.

## SQLite v1 logical schema

The production database uses `PRAGMA application_id = 1398030914`
(`0x53544242`, `STBB`) and `PRAGMA user_version = 1`. The application ID is not a
security boundary; it prevents an unrelated SQLite file from being mistaken for
Battle data. `store_meta.format_id` is the second positive format check.

The following columns and constraints are normative. #63 may change SQL spelling
or add indexes without changing the stored contract.

| Table | Required v1 content |
| --- | --- |
| `store_meta` | Singleton row with `format_id = 'stfc.battle-store.v1'`, immutable store-instance UUID, schema version, minimum reader version, creation/migration UTC, and active policy revision. |
| `schema_migration` | One immutable completed row per migration: from/to versions, implementation version, start/completion UTC, and verified pre-migration backup ID. No partial migration row is treated as success. |
| `ingest_batch` | Live source namespace, producer scope, producer batch identity and derived live-batch key; complete outer-envelope SHA-256/byte count; producer artifact/version; exact produced-time text; normalized signed-64-bit accepted UTC milliseconds; accepted/rejected result, counts, and bounded error. A successful batch row and all accepted receipt associations commit together; a rejected new batch may record no event association. |
| `import_receipt` | Synthetic attempt identity, import namespace, source artifact hash/size/format, adapter contract/version, exact requested/completed text plus normalized signed-64-bit UTC milliseconds, explicit in-progress/terminal result, counts, and bounded error summary. It never masquerades as producer evidence. |
| `event_blob` | Content-addressed compressed bytes keyed by codec, codec minimum-reader contract, compressed-byte SHA-256, raw-byte SHA-256, and byte counts. Sharing a BLOB never collapses source identities or receipts. |
| `event_evidence` | Root occurrence independent of any battle: internal ID; source namespace, indexed logical event key, and occurrence identity; family, exact source schema or adapter-owned discriminator, and role; protocol/source/provenance fields; exact source timestamp text plus normalized signed-64-bit event/first-accepted UTC milliseconds; exact session/battle/journal IDs when present; immutable original codec/minimum-reader, compressed/raw hashes and byte counts; nullable active event-BLOB key and payload state; evidence state; prune/reclassification/export disposition. Source namespace plus occurrence identity is unique; logical event key is not. |
| `event_ingest_receipt` | Many-to-many occurrence-to-`ingest_batch` association with batch event ordinal, exact `accepted`/`exact-retry`/`rehydrated-hash-identity` disposition, and normalized acceptance UTC. An occurrence in a later batch adds this row without duplicating `event_evidence`. |
| `event_import_receipt` | Many-to-many occurrence-to-`import_receipt` association with canonical entry kind/name/ordinal, exact `accepted`/`exact-retry`/`rehydrated-hash-identity` disposition, and normalized acceptance UTC. Repeated imports remain auditable without duplicating `event_evidence`. |
| `battle_record` | Optional versioned grouping/read-model row with internal battle-record ID, immutable source-qualified battle key, source namespace, normalized capture time/type, compact summary, and aggregate evidence state. It may exist without a capture for partial legacy/imported evidence. |
| `battle_alias` | Exact `battle-id` or `journal-id` value plus its collision-safe alias identity, source namespace, and battle-record ID. An alias resolves at most one record and a record owns at most one value of each kind; conflicts never rewrite this table automatically. |
| `event_battle` | Optional association from event evidence to a battle record and relation role. At most one `canonical-capture` relation is allowed per source-qualified battle record, and only an accepted `battle.capture` may fill it. Events without battle identity remain valid root evidence. |
| `catalog_blob` | Content-addressed catalog document with catalog schema, codec/minimum-reader contract, compressed/raw hashes, byte counts, and compressed BLOB. |
| `catalog_observation` | Catalog event evidence, catalog hash, source namespace/session, observation UTC, and optional battle association. Session-scoped catalogs do not require a fabricated battle record. |
| `battle_projection` | Optional battle-record ID, projection kind/schema/implementation version, deterministic ordered source-evidence hash set, compact payload, and update UTC. Its compound key prevents one algorithm version or incomplete source set from impersonating another. |
| `maintenance_ledger` | Completed backup, export, integrity, retention, rehydration, and compaction operations with operation ID, kind, completion UTC, affected record/byte counts, and original/new BLOB or manifest hashes where applicable. |

`event_evidence` is authoritative even when no `battle_record` can be formed.
Battle rows and associations are versioned query/read models. They may outlive
heavyweight BLOBs as honest summary/tombstones, but they must not claim that
pruned event bytes remain locally available. Reclassifying supplemental evidence
is a versioned maintenance operation, never an `UPDATE` that erases its prior
hash/disposition without a ledger entry.

Catalog addressing uses:

```text
SHA-256(UTF8(catalog-schema) || 0x00 || exact UTF-8 bytes of the accepted catalog object)
```

Duplicate JSON property names are rejected before addressing. Object-key order,
number lexemes, and string escapes are otherwise evidence: v1 does not parse and
reserialize them into a home-grown semantic canonicalization. Equivalent catalogs
with different source bytes may occupy two blobs; that costs space but cannot
collapse distinct evidence incorrectly. Unreferenced blobs are removed only in
the same cleanup transaction that removes their last link.

Required indexes are:

- unique `(source_namespace, occurrence_identity)`, logical-event lookup on
  `(source_namespace, logical_event_key, event_timestamp_unix_ms,
  evidence_id)`, and indexed exact event hashes;
- unique live-batch lookup on `(source_namespace, producer_scope, batch_id)` with
  the derived live-batch key and envelope hash/count checked on retry;
- receipt traversal by live batch/import attempt and reverse traversal from an
  occurrence to every accepting receipt;
- recent history on `(captured_at_unix_ms DESC, battle_record_id DESC)`;
- unique battle-alias lookup on `(source_namespace, alias_identity)` plus unique
  `(battle_record_id, alias_kind)`;
- session/family lookup on event evidence without a battle join;
- cleanup planning on `(evidence_state, event_timestamp_unix_ms,
  accepted_at_unix_ms, evidence_id)`;
- projection lookup on `(battle_record_id, projection_kind, projection_schema)`;
- catalog reverse lookup by source/session without a battle join and by optional
  battle record when an association exists.

#63 must freeze `EXPLAIN QUERY PLAN` assertions for recent-history pagination,
exact battle/journal alias lookup, logical-event and receipt traversal, detail
hydration, and cleanup selection. Offset-based whole-history paging is not
accepted; use the `(time, battle_record_id)` cursor.

## Compression candidate and bounded observation

`brotli-json-utf8-q5-w22-v1` is the v1 candidate codec: Brotli quality 5, window
22, exact UTF-8 JSON input. The codec string, compressed/raw byte counts, and both
SHA-256 values make the storage self-describing. An unknown codec with a valid
stored-BLOB hash is unsupported evidence, not proven raw evidence and not
automatically corruption. Bridge preserves it without normal parsing, projection,
or portable JSON export until a compatible reader exists.

The following is a bounded exploratory sizing observation, not a preregistered
benchmark or checked-in probe. It compared per-record Brotli quality 5, window 22,
with .NET `DeflateStream` `Optimal`, using no dictionary or cross-record buffer.
Every table value is the sum of independently compressed `payload_json` record
lengths, not one whole-set stream. The command streamed the `sidecar_events` table
from `.sidecar/smoke-live.sqlite` in the Sidecar checkout, ordered by
`sequence_id`, and retained no corpus in memory. It observed 306 rows in each of
the four listed families (1,224 total). The source database was observed at merged
Sidecar revision
`4493eb41446de26cf0103e643af516c3a09c5182`; its creation predates that revision,
so the revision is inventory context rather than build provenance. The file was
121,221,120 bytes with SHA-256
`a355dda56404921b6878f8cc61c8bf6e78a67f651114e81fed490de79d3f999c`.

| Payload family | Rows | UTF-8 bytes | Brotli q5 bytes | Deflate bytes |
| --- | ---: | ---: | ---: | ---: |
| `battle.capture` | 306 | 55,997,115 | 4,906,291 | 9,131,061 |
| `battle.report` | 306 | 39,226,556 | 2,131,456 | 3,260,011 |
| `battle.analytics` | 306 | 22,181,535 | 1,576,719 | 1,875,234 |
| `catalog.snapshot` | 306 | 1,334,248 | 371,470 | 390,376 |
| All duplicated families | 1,224 | 118,739,454 | 8,985,936 | 14,656,682 |

The 306 snapshots contained 87 distinct catalog objects after parse-and-serialize
extraction. Dedup reduced those objects to 394,206 raw bytes and 101,366 Brotli
bytes. Because the v1 address deliberately preserves the exact source object
bytes, #63 must repeat this count with its token-preserving extractor; the
measurement establishes scale, not a promise that semantically equal but
byte-different objects collapse. Retaining every measured event family plus those
catalog blobs is about 9,087,302 compressed bytes before SQLite overhead. The
post-derivability lower-bound candidate—306 captures plus the catalog blobs—is
5,007,657 bytes, but #63 is not authorized to drop the difference until every
accepted report, analytics, and catalog-snapshot field passes the gate above.
Neither figure is an on-disk SQLite or retention budget: both omit pages, indexes,
summaries, rollback-journal headroom, backups, and maintenance headroom.

A .NET 10.0.9 sizing probe using `BrotliEncoder` compressed the 306 captures in
approximately 1.11 seconds and decoded them in 0.18 seconds on the current
workstation; Deflate compression took 0.73 seconds but produced 86% more bytes.
All 306 captures round-tripped. A flipped compressed byte was rejected by the
decoder. These timings select the candidate, not the final performance gate:
#63 must repeat them inside the actual net8.0 application, include SHA-256 mismatch
cases where corrupted Brotli remains decodable, and record median/p95 latency and
allocations on the minimum supported Windows build.

## Connection and write contract

Every connection must positively enable/read back `foreign_keys = ON` and
`trusted_schema = OFF`. The initial #63 qualification candidates are:

```text
journal_mode = DELETE
synchronous = FULL
temp_store = FILE
busy_timeout = a bounded product constant
auto_vacuum = NONE
```

These are not accepted because they look conservative. #63 must crash-test them
with the selected provider; #78 must measure database, journal/temp, backup, and
compaction bytes written, fsync/commit latency, ingest throughput, and recovery
cost against the bounded corpus. An alternative may replace a candidate only if
it preserves the same durability/recovery contract and wins that reviewed gate.
No candidate is described as minimizing SSD writes before those measurements.

The store is local-disk only and shared cache remains off. Repository operations
provide a logical serialized-writer/maintenance boundary: ingest, migration,
backup setup, cleanup, replacement, and compaction cannot write or checkpoint
concurrently. #59 owns connection count/lifetime, queues, cross-process lock and
single-instance behavior, listener pause/drain, and runtime shutdown/restart. #61
does not require one forever-open connection or prescribe the physical lock.

`DELETE` rollback journaling is the conservative v1 qualification default, not
an accepted final pragma or an assertion that WAL is generally unsafe. The
system SQLite observed during this design is
3.51.1. SQLite documents a rare WAL-reset corruption race in versions 3.7.0
through 3.51.2 when two or more connections in different threads/processes write
or checkpoint concurrently; it is fixed in 3.51.3 and selected backports. See
[SQLite's WAL-reset bug description](https://www.sqlite.org/wal.html#the_wal_reset_bug).
The logical serialization contract excludes Bridge-created concurrent
write/checkpoint operations, but v1 also avoids depending on that exclusion for
its journal format. #63 may propose WAL only with a patched provider, #59's exact
coordination/locking proof, adversarial multi-connection/checkpoint tests, and
measured benefit under #78. Journal mode is operational and does not change the
stored v1 schema.

Validation, hashing, summary derivation, and compression occur before the logical
write lease. For live ingest, a bounded transaction inserts the batch, records,
new event occurrences where needed, every event-to-batch receipt association,
battle aliases/links, catalog links, and cache changes. An exact retry inserts its
new receipt association even though its authoritative occurrence row is a no-op.
A crash before commit leaves none of the batch; a crash after commit leaves all
of it. Import first
commits its in-progress receipt, then uses bounded entry transactions that link
accepted occurrences and their import-receipt associations and update counts; its
final transaction records the terminal result. Startup classifies a non-terminal
receipt as interrupted without deleting accepted evidence. `INSERT OR IGNORE` is
not sufficient by itself: code must read the existing length/hash and verify exact
bytes to distinguish a retry from a digest collision.

Collection does not buffer an unbounded batch. The repository exposes one-event
operations underneath a bounded envelope transaction and streams import/export.
Queries decompress only the selected detail record. Size counters use database,
active journal, backup, and pending-maintenance bytes rather than `COUNT *
average` guesses.

## Readability, migration, and recovery states

These are storage states, not runtime capabilities, provider identities, feature
policy, or player preferences.

| Evidence | State and permitted behavior |
| --- | --- |
| No database | `Absent`: history may show an empty state; no file is created until an eligible feature or explicit import needs it. |
| Expected database exists but access, sharing, or transient I/O prevents a read-only open | `Unavailable`: do not label bytes corrupt, mutate, copy over, or recreate. Report the concrete OS error and offer retry/lifecycle guidance. |
| Correct application/format IDs and supported v1 schema | `Readable`: reads are allowed; writes additionally require an active collection/import owner. |
| Older recognized schema with a complete migration | `MigrationRequired`: read/write activation pauses until a verified backup and transactional migration succeed. Cancel leaves the original untouched. |
| Schema version or `store_meta.minimum_reader_version` newer than this reader | `TooNew`: open read-only only far enough to report identity/version. Do not inspect rows as if compatible, migrate down, copy over, vacuum, or recreate. Offer Bridge update guidance and preserve the store. |
| Unknown application/format ID or unsupported old schema | `Unsupported`: fail closed without mutation and offer import/update guidance only when a reviewed adapter exists. |
| Known schema with an unsupported event/catalog codec, or a codec minimum-reader version newer than this reader, and a valid stored-BLOB hash | `UnknownCodec`: preserve the row/BLOB and continue unrelated readable evidence. Do not invoke even a known decoder when its declared minimum reader is too new. Normal detail/projection and portable JSON export for that evidence are unavailable. Offer Bridge update guidance or an explicitly opaque rescue package. |
| SQLite malformed/integrity failure, foreign-key failure, stored-BLOB hash failure, decompression-length failure, or raw-hash failure | `Corrupt`: stop affected dependent reads and writes, preserve the primary and any active journal, and offer verified-backup recovery or independently validated readable-record export. Never silently skip a bad event. |
| Verified backup exists and the player authorizes restore | `RecoveryReady`: restore to a new candidate file, validate it, then perform a journalled atomic replacement while collection is stopped. Keep the corrupt set until success is confirmed. |

Startup performs constant-work header, application/format ID, schema, journal, and
unfinished-maintenance checks. It does not scan a multi-gigabyte database on
every Bridge launch. `quick_check` runs after an unclean storage shutdown and for
explicit diagnostics. Full `integrity_check`, `foreign_key_check`, and a sampled
or complete evidence-hash pass run before/after migration, before/after restore,
and before destructive retention. SQLite's integrity check does not cover foreign
keys or application-level compressed hashes, so all three checks are required.

Every migration has forward-only SQL/code, golden old/new fixtures, a verified
pre-migration backup, and crash injection before backup completion, before commit,
and after commit. `user_version` changes in the same transaction as the schema.
The application never edits a database whose version is too new.

## Backup, export, compaction, and retention

Backup and export are different operations:

- A recovery backup is a consistent SQLite backup-API snapshot made before
  migration, restore, or destructive cleanup. It is written to a temporary name,
  integrity/foreign-key checked, recorded through the lifecycle-owned recovery
  marker, and renamed only after verification. #59 owns physical placement;
  automatic backups are bounded, with final count a #78 budget decision.
- A player export is a streamed, portable archive containing every selected
  decoded and verified complete-event JSON object, its canonical/supplemental
  role, decoded catalog objects, metadata, and a hash manifest. Unknown codecs or
  failed raw hashes cannot enter this format. It writes to a player-selected
  destination through temp/verify/rename. An export is not considered protection
  until every selected record is present and its manifest verifies. A rehydrated
  occurrence carries its `rehydrated-hash-identity` disposition; export never
  upgrades that into a claim that deleted original bytes were compared.
- An opaque rescue package is a separate recovery artifact for structurally
  readable but undecodable evidence. It contains the unchanged stored BLOB,
  declared codec/minimum-reader value, stored-BLOB hash, unverified declared raw
  hash/length, source identity/receipt metadata, and a rescue manifest. It is
  labelled non-portable and unverified; it must never be named JSON export or
  treated as accepted event evidence by an older reader.

A durable lifecycle-owned recovery marker records each in-flight replacement or
destructive maintenance operation, its exact primary/candidate/backup identities,
and expected stored-byte hashes. It lets startup resolve a crash between file
replacement and the database's completed-operation record without choosing by
timestamp or deleting either candidate. #59 owns the marker's physical location,
file/record format, connection lifetime, locking mechanism, and process/runtime
coordination. #61 requires the recovery semantics, not a second lifecycle design.

Compaction is explicit maintenance. Under a #59-owned exclusive maintenance
lease, no repository transaction or journal is active, a verified backup is made,
sufficient free space is confirmed, and SQLite `VACUUM`
runs with progress/cancel semantics only where SQLite can cancel safely. The store
then passes full integrity, foreign-key, count, and evidence-hash checks before
the backup becomes eligible for bounded aging. No timer performs periodic full
VACUUM. `VACUUM INTO` may replace this path only if the selected provider/minimum
OS qualification proves it; it is not assumed by v1.

Retention proceeds through visible states:

```text
retained -> cleanup-candidate -> previewed -> export-required/export-verified
         -> pruning -> summary-only
summary-only -> rehydrating -> retained-rehydrated
retained-rehydrated -> cleanup-candidate
```

Cancel before `pruning` makes no change. The prune transaction marks each selected
event-evidence row `summary-only`, retains its source identity, metadata, both
hashes, byte counts, every ingest/import receipt association, and export receipt,
removes its BLOB reference and affected rebuildable projections, and updates any
associated battle summary. Shared event/catalog BLOBs are garbage-collected only
after no evidence/observation references remain. A crash commits all of that or
none of it.

The summary-only occurrence is a retention tombstone, not an empty duplicate. It
keeps source namespace; logical and occurrence identities; family; exact source
schema or adapter discriminator; exact timestamp/session/battle/journal/source
metadata; normalized times; raw SHA/byte count; original codec/minimum reader;
original compressed SHA/byte count; prune operation/reason; verified-export
receipt where required; and every ingest/import receipt association. The nullable
active-BLOB link is separate from those immutable original values. A rehydrated
BLOB may have different compressed bytes from the original encoder while retaining
the same codec contract and exact raw content identity; the maintenance ledger
keeps both compressed representations.

A later event that derives a tombstoned occurrence identity first checks whether
the same content-addressed raw bytes still exist through another occurrence or a
verified backup/export supplied for comparison. An exact byte comparison permits
an atomic relink plus `exact-retry` receipt association. When the last comparable
payload is gone, the event is not silently accepted as an exact retry; it follows
this narrower rehydration path:

1. Bridge fully validates the incoming exact bytes and re-derives namespace,
   logical key, occurrence identity, raw SHA, and byte count. Every immutable
   extracted field above must match the tombstone. A byte-different event with a
   different raw identity remains an ordinary distinct occurrence.
2. The tombstone codec/minimum-reader contract must be supported. Bridge encodes
   the incoming bytes with that codec and verifies decode, length, raw SHA, and an
   in-memory byte-for-byte round trip against the incoming bytes.
3. One bounded transaction inserts/reuses the new content-addressed BLOB, restores
   the active link on the existing occurrence, records a `rehydration` maintenance
   entry with original/new compressed hashes, and adds the accepting receipt
   association with disposition `rehydrated-hash-identity`.

That disposition asserts a match to the retained cryptographic raw identity, not
byte-for-byte equality with payload the player authorized Bridge to delete. If the
same derived occurrence has any tombstone metadata/raw-length/raw-hash mismatch,
the codec is unavailable, or round-trip verification fails, Bridge leaves the
tombstone/BLOB state unchanged, adds no authoritative event association, and
records a bounded
`rehydration-conflict` or `rehydration-unsupported` result on the rejected
batch/import receipt. It never overwrites the tombstone or treats hash+length
alone as exact-byte proof.

Until #78 sets measured byte/age gates and decides the export rule, v1 performs
no automatic raw-evidence deletion. A soft limit reports exact usage and offers
Review storage, Export, or Delete selected evidence. Low disk pauses new
collection before threatening the machine; it does not make old data disappear.
The UI must state how many evidence events, associated battles, and bytes each
action affects and whether detail evidence will remain available.

## SQLite provider and package trust

The first #63 spike should qualify `Microsoft.Data.Sqlite.Core` with the
`SQLitePCLRaw.bundle_winsqlite3` provider line compatible with the net8.0
application. Microsoft documents that this combination uses Windows 10's system
`winsqlite3.dll` instead of shipping the default `e_sqlite3` native library:

- [Microsoft.Data.Sqlite custom versions](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/custom-versions)
- [SQLitePCLRaw bundle_winsqlite3 package](https://www.nuget.org/packages/SQLitePCLRaw.bundle_winsqlite3/2.1.11)

This is a candidate, not an accepted dependency. On the current Windows NT build
10.0.26200 workstation, direct system-library probing found `winsqlite3` 3.51.1
with serialized/thread-safe support. #63 still must prove the exact pinned managed
package set on the MSIX minimum 10.0.19041 build and current Windows 11.

The provider gate is:

- no SQLite native DLL in publish, MSIX, standalone ZIP, single-file extraction,
  release inventory, or writable LocalAppData;
- no SQLite managed initialization, database file, or loaded `winsqlite3.dll`
  before an eligible storage-backed feature/import activates;
- no app-directory, current-directory, PATH, or writable LocalAppData
  `winsqlite3.dll` can shadow the system component in either package topology;
- after activation, Bridge loads the System32 component through an explicit
  system-only path/search policy, verifies the resulting module path, binds the
  provider's P/Invoke resolver to that retained module handle, and keeps the
  handle for the provider lifetime. A bare-name DllImport that happens to resolve
  correctly on the test machine does not pass;
- the v1 schema, candidate journal/durability modes, backup API, cancellation,
  corruption, and required query-plan suite pass on the minimum OS;
- mandatory `foreign_keys`/`trusted_schema` settings are positively read back and
  candidate pragmas report the requested effective mode; an older system library
  that silently ignores `trusted_schema = OFF` does not pass by assumption;
- MSIX and standalone builds use the same provider and stored format.

The default `Microsoft.Data.Sqlite`/`bundle_e_sqlite3` path is rejected for v1
unless later evidence closes the package-topology contract. It ships a native
library; the current single-file build can extract native libraries before Battle
composition, and a loose DLL in the writable standalone layout reopens the
verify-to-load race. A package-adjacent MSIX DLL alone would not solve standalone
trust. If the system provider cannot pass the gate, #63 returns to architecture
review instead of quietly adding a native binary.

Using the system provider also delegates native SQLite security servicing to
Windows Update. #63 must record that consequence and test only supported, fully
patched Windows 10 and 11 baselines. The schema targets the oldest qualified
surface: SQLite 3.31.0 or newer, basic SQL/transactions, rollback journals,
foreign keys, backup API, and `trusted_schema`. It does not depend on JSON1, FTS, `STRICT`, `RETURNING`,
generated columns, `VACUUM INTO`, or partial integrity checks. The current
workstation's 3.51.1 surface must not leak into DDL or query assumptions. A
known-unremediated system-library issue applicable to v1 operations is a provider
qualification failure, not permission to download or side-load a DLL.

MSIX package integrity protects package files, not the external player database.
The database uses hashes, SQLite recovery, backups, and current-user storage for
integrity and privacy; it is not evidence of mod or launcher authenticity and is
not promised to be encrypted at rest.

## Exact #63 acceptance handoff

#63 may begin implementation after this contract is reviewed. It is complete
only when it supplies:

1. The v1 DDL and golden schema fixture with event evidence independent of battle
   rows; occurrence-to-live/import-receipt associations; collision-safe battle
   aliases; optional battle/session-catalog associations; canonical/conflicting
   capture constraints; application/format IDs; migrations; and query-plan
   assertions.
2. Lossless complete-event round trips for capture and every accepted supplemental
   family, including adapter-owned/no-ID legacy `battle.event`; exact
   large-ID/source-timestamp preservation and signed-64-bit normalized UTC event
   and acceptance times; inner-event byte-boundary assertions; logical-key versus
   occurrence vectors; exact retries linked to multiple receipts; byte-different
   supplemental coexistence; canonical/conflicting capture behavior; every
   battle-alias precedence/conflict case; cross-source coexistence/BLOB dedup;
   producer-source/session-scoped batch collisions/retries; catalog-with-journal
   session scope without a fabricated battle and rejected live no-journal catalog;
   stable import namespaces/receipts; original-key versus entry-locator precedence;
   named/ordinal locator vectors including filename `12` versus ordinal 12;
   duplicate archive-name/original-key rejection; zero/multidigit canonical count
   encodings; lowercase-64-hex SHA inputs; repeated imports; rejected direct
   PostgreSQL input; and bounded reviewed-export import/portable export tests.
3. Transaction crash injection for batch/import receipt ingest, migration,
   backup, restore, cleanup, last-BLOB prune, active-BLOB exact relink,
   tombstone rehydration, rehydration conflict, and catalog garbage collection,
   exercised through a #59-owned fake lifecycle/maintenance lease and
   recovery-marker seam.
4. Too-new schema and minimum-reader fixtures; unknown codec with valid stored
   hash; unknown codec with corrupt stored bytes; invalid Brotli;
   valid-Brotli/wrong-raw-hash; foreign-key/corrupt SQLite; missing backup; opaque
   rescue manifest; proof that portable JSON export refuses undecodable evidence;
   retained-payload exact retry versus summary-only tombstone hash-identity
   rehydration; mismatch/unsupported-codec rejection with no association; receipt
   dispositions and immutable original/new compressed metadata; and proof that a
   later compatible reader recovers unknown-codec evidence.
5. A checked-in bounded streaming measurement probe and machine-readable result
   manifest for actual net8.0 Brotli/Deflate timing and allocation results plus
   database/index/journal/temp/backup/compaction bytes written, fsync/commit
   latency, and recovery cost for the candidate pragmas against the 306-battle
   corpus. A field-by-field
   derivability matrix and exact golden-output comparison must pass before any
   report/analytics/catalog-snapshot event is reclassified as a cache; every
   irreproducible family remains versioned canonical evidence or returns to review.
6. Pinned provider/dependency review, license/SBOM updates, vulnerability review,
   oldest-supported-OS query/journal/backup qualification, hostile app-directory
   DLL shadow tests, and MSIX/standalone safe/lazy module-loading proof. Connection
   lifetime and cross-process coordination remain supplied by #59, not #63.
7. Zero-cost evidence required by the package-topology gate: no database, files,
   provider initialization, native module, factory, worker, or maintenance timer
   while Battle storage is inactive.
8. Player-visible exact size, health, backup, export, and manual cleanup evidence.
   Automatic pruning remains disabled until #78 supplies accepted defaults.

## Decisions intentionally left to #78 or later review

- soft/hard byte thresholds, age windows, backup count, and soak budgets;
- whether a verified export is mandatory before any future automatic prune;
- whether privacy-oriented post-prune compaction is worth its SSD write cost;
- final net8.0 compression latency/allocation gate and whether Brotli q5 remains
  the selected codec;
- which supplemental families must permanently remain canonical because fields
  are proven impossible to reproduce from capture/catalog evidence;
- encryption-at-rest or cloud synchronization, each of which requires a separate
  privacy, key-management, recovery, and threat-model decision.

This contract does not authorize a listener, repository, native dependency,
retention default, background timer, or release packaging change by itself.
