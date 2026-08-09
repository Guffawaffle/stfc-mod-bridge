# Battle Bridge Native Ingest Boundary

Status: native transport component implemented for Sidecar issue #62; dormant
until the activation and repository owners supply an eligible composition

## Component decision

Battle Bridge uses the Windows `HttpListener` surface and the operating system's
HTTP parser. It reserves only `http://127.0.0.1:<port>/`, then accepts exactly
`POST /api/sidecar/ingest` with no query or trailing slash. Every other method,
path, or query is rejected. A medium-integrity, non-elevated bind was proven on
the target Windows environment without a URL ACL. The signed MSIX package must
still repeat this bind/ingest gate because package policy remains a separate
compatibility boundary.

This choice adds no NuGet package or ASP.NET Core framework reference. It keeps
the transport on the existing .NET/Windows closure and prevents the updater
from inheriting an unused web-host runtime. It is not permission to add a
browser application server, static routes, public health endpoint, or HTTP
administration API.

## Activation and ownership

`BattleIngestActivation.Resolve` consumes only the normalized
`LauncherRuntimeProfile` and explicit per-family collection demand. It requires
`ingest.stfc-sidecar.v1` plus the exact requested family capability. It never
checks a provider ID, repository, display name, or version guess. Current
NetniV/unknown evidence and the current bundled Guffawaffle manifest therefore
remain dormant; a future compatible NetniV profile works without code changes.

The host does not own capability detection, player consent, the credential,
TOML, runtime leases, or storage. It starts no listener, queue, worker, timer, or
filesystem work when the resolved activation has no runnable family. The #132
lifecycle composition must supply the already-verified activation and protected
credential. The #63 repository supplies `IBattleIngestSink` and owns the SQLite
transaction. A successful HTTP response is written only after that sink reports
commit; request parsing and repository work are separated by a bounded
single-reader channel. The 15-second deadline covers the authenticated request
body, parse, queue, and durability path, not a separate timeout per phase.

## Accepted v1 surface

- Authentication is required on every request. The current producer-compatible
  `stfc-sync-token` header and `Authorization: Bearer` are accepted as mutually
  exclusive spellings of the same dedicated 32-byte, unpadded-base64url
  credential. Ambiguous, repeated, missing, malformed, or incorrect credentials
  fail before body parsing.
- `battle.events` accepts only `battle.capture` /
  `stfc.battle.capture.v1` when that exact family is eligible.
- `fleet.runtime` accepts only `stfc.fleet.runtime_snapshot.v1` when that exact
  family is eligible.
- `transport.chunk` preserves the producer's
  `stfc.sidecar.ingest.chunk.v1`/standard-base64 compatibility envelope and may
  reassemble only an otherwise eligible kind.
- Unknown outer versions, kinds, payload versions, supplemental families, and
  mixed batches fail the whole batch before storage. Additive fields inside an
  accepted family remain source evidence and are retained unchanged.

The current mod can batch `battle.report`, `catalog.snapshot`, and
`battle.analytics` beside a capture when Battle-log enrichment is enabled, but
the reviewed producer declaration currently advertises only ingest, capture,
and Fleet runtime. Default enriched Guffawaffle delivery is therefore **not
operationally enabled** by this work. Before activation, the producer must emit
capability-aligned batches or separately declare and qualify every supplemental
family. Bridge must not partially store the capture from a mixed undeclared
batch.

## Checked-in limits

| Boundary | Limit |
| --- | ---: |
| HTTP request body | 512 KiB |
| Events in one Battle batch | 256 |
| Chunks in one group | 512 |
| Reassembled envelope | 16 MiB |
| Incomplete and in-flight reassembly bytes | 32 MiB across at most 8 groups |
| Queued committed-work candidates | 24 MiB across at most 32 batches |
| Concurrent requests | 16 |
| Request rate | 240 per one-second fixed window |
| Request/durability deadline | 15 seconds |
| Incomplete chunk lifetime | 2 minutes, pruned on access without a timer |
| Shutdown drain | 5 seconds, then cancellation and terminal worker join |

The 16 MiB envelope limit is calibrated against the reviewed local corpus: the
largest same-battle capture/report/analytics/catalog family bundle is about
4.48 MiB before outer JSON. It leaves more than three times that observed size
without carrying forward the legacy 32 MiB reassembly allowance. The producer
switches to 64 KiB source chunks above 256 KiB; a 512 KiB request limit admits
those base64 chunk envelopes.

Queued event views are byte slices over one owned exact envelope buffer. They do
not copy each event, and queue accounting charges the complete retained buffer.
Chunk memory is charged as each decoded fragment arrives. During completion,
both fragments and the new reassembled buffer are charged; the resulting lease
stays charged through parse and queue/commit handoff. Memory is released on
commit, rejection, conflict, timeout, or shutdown. The 32 MiB limit permits one
maximum-size group's 16 MiB fragment/reassembly overlap without restoring the
older 64 MiB pending budget.

## Failure, replay, and diagnostics

Batch IDs are idempotency keys within one source and producer session in one
Bridge process. Repeating the exact bytes shares or reuses the original commit
result; reusing that scoped ID with different bytes is a conflict. Chunk groups
are likewise scoped by source, session, and group ID. A conflicting chunk
removes only its scoped incomplete group. These
transport rules complement, but do not replace, #63's durable occurrence and
receipt identities. A retry after process restart must be resolved durably by
the repository.

The transport validates the outer protocol, family eligibility, bounded
structure, exact byte boundaries, and atomic batch acceptance. It deliberately
does not replace #63's complete domain semantics, occurrence identity, or
transaction validation.

Port collision, generic start failure, rate pressure, queue pressure, timeout, protocol mismatch,
chunk conflict, batch conflict, and storage rejection have deterministic typed
results. Shutdown stops new accepts and gives work five seconds to drain before
requesting cancellation. It then joins the worker; five seconds is not a false
hard exit promise. An injected sink that ignores cancellation keeps shutdown
incomplete until it terminates. The #63 sink contract must prove cancellation
compliance so its SQLite transaction commits completely or rolls back before
returning.

`BattleIngestHealthSnapshot` contains only listener state, port, bounded counts,
byte totals, a typed last failure, and a stable transition. It never contains a
token, batch ID, payload, event, player/alliance identity, endpoint credential,
or local path. There is no public health route.
