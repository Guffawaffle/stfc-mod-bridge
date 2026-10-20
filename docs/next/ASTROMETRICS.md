# Astrometrics: optional local diagnostic companion

Accepted direction, 2026-10-20, under rewrite epic #231. Astrometrics is a separate
Bridge tab. Collection starts off on each Bridge launch, is explicitly started
or stopped there, stays active across tabs, and stops on normal host close. It
runs inside Bridge; no background service, widget, relay or remote deployment is
implemented. The separate Shuttle Bay simple-mode idea is in `IDEAS.md`.

## Producer and delivery

The mod adds `SyncConfig::Type::Diagnostics`: JSON type `diagnostic`, TOML flag
`diagnostics`, default false, with ordinary per-target URL/token/category routing.
The sender is still fire-and-forget, with no persistent outbox or retry. Diagnostic
queue admission is capped at 256 pending entries so a capture burst cannot grow
an offline destination's queue indefinitely. Drops are logged. Existing categories
and destinations retain their delivery behavior. No hook, probe, claim recovery,
Kirshara capture or game configuration is enabled by this category.

The first producers forward existing `claims/main`, `claims/selections` and
`claims/receipts` observations when those captures are already active. The envelope
is a JSON sync array of diagnostic objects with schema1, a process-scoped random
`source_id`, producer version, original session, stream, decimal-string sequence,
optional decimal-string UTC milliseconds, event type and original JSON payload.
Source + session + stream + sequence is event identity. Account identity and
relationships between independent streams are not inferred. Future producers can
use another stream without replacing the envelope. Schema evolution requires an
explicit supported version; future versions are refused, not guessed.

## Receiver, store and control

Bridge binds only `127.0.0.1:58237`, POST `/sync`, using the existing
`stfc-sync-token` header and JSON content type. A token is generated and retained
in its own local database; connection settings are revealed only on the explicit
Astrometrics page. The generated local target overrides an inherited sync proxy
with an explicit empty proxy setting. No CORS/browser-origin or remote bind is admitted. The bounded
HTTP1.1 reader accepts fixed-length bodies up to256KiB, at most16 events/batch,
8KiB headers and a two-second read deadline. No thread is created per connection.

The local SQLite file is under Bridge's application-local-data directory,
`astrometrics/events.sqlite`. Opening the tab while disabled creates no store or
listener; an existing owned database is opened read-only to inspect history.
History availability is separate from receiver availability. First activation initializes only an empty, app-owned database. The
application ID, schema version and schema hash must match on reopen; unrelated
or newer stores are refused. Batches commit atomically before HTTP200. Exact
redelivery is idempotent; conflicting identity returns409 and rolls the batch
back. Append-only evidence is retained; a256MiB database cap rejects further
writes rather than pruning. There is no automatic cleanup or server forwarding.

Start/stop and timeline reads cross the same versioned v1 dispatcher used by the
CLI and Tauri. Paths and listener selection belong to the native host, not the
renderer. Stop and normal host close join the receiver before releasing its
SQLite owner. This app-local collection switch admits no game/catalog writes or
configuration mutations and does not grant the future operation host's writer
capabilities. Lost UI responses do not imply a requested start never happened.

## First investigation view

Astrometrics shows the latest30 arrivals, stream-local sequences and session,
stored/rejected counts, conservative explanations and a bounded evidence preview.
The full payload remains in SQLite when its UI preview is truncated. Result
publication, presentation, grant observations and inventory balance remain
separate facts. A missing completion does not prove a failed redemption. This
first timeline is not the older specialized claim database and does not rewrite
or migrate that database. It provides a shared event store for later projections.

## Validation boundaries

Focused tests exercise real loopback framing/authentication and SQLite commit,
reopen, duplicate/conflict rollback, foreign-store refusal and bounded previews.
Protocol/client tests cover the new optional methods. Tests and Windows builds
do not establish Apple Silicon runtime or an installed live-game capture. Full
br-21 and release acceptance remain governed by the existing qualification gates.
