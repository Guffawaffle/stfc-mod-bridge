# Launcher release withdrawal ledger

`release-withdrawals.jsonl` is the durable audit trail for launcher release
withdrawals. Each line is one JSON object committed through review. The ledger
does not mutate an immutable historical release manifest. The protected release
producer strictly validates it and projects only same-channel machine selectors
into the next authenticated manifest.

Each non-empty line uses schema 1 and requires these machine fields:

```json
{"schemaVersion":1,"channel":"stable","kind":"artifact-sha256","value":"<64 lowercase hex>","withdrawnAt":"2026-08-06T00:00:00Z","reason":"security"}
```

- `channel` is `stable` or `preview`;
- `kind` is `release-sequence`, `manifest-sha256`, or `artifact-sha256`;
- `value` is a canonical positive decimal sequence or lowercase SHA-256;
- `withdrawnAt` is whole-second UTC RFC 3339 and cannot postdate the manifest
  that first publishes it;
- `reason` is `security`, `integrity`, `operator-error`, or `policy`.

The closed ledger additionally permits `operator`, `detectedAt`, `containedAt`,
`advisory`, and `replacementTag` as operator evidence. Those fields remain in
Git history but are not projected into runtime authority. Unknown or duplicate
properties, duplicate selectors, and malformed entries stop the release.

Emergency containment never waits for a higher replacement. Freeze publication
and channel advancement, preserve the release, tag, assets, attestations, logs,
and transparency evidence, then record the affected identity and incident
evidence. Prefer an immutable historical record: deleting a GitHub release, tag,
or hosted bundle does not revoke copies already captured by users or a
transparency log and can destroy evidence needed for investigation.

A higher independently verified manifest remains the normal way to publish an
authenticated withdrawal to clients. It is not a prerequisite to containment.
The ledger alone is not an authenticated runtime denylist or other authority:
its selectors become machine-enforceable only
after inclusion in a schema-v2 manifest whose exact bytes pass the closed
Sigstore policy. Issue #96 owns that activation. Follow
[`COMPROMISE_RESPONSE.md`](../windows-launcher/COMPROMISE_RESPONSE.md) for the
authority-specific response and communication procedure.

Healthy installed payloads remain usable when discovery or freshness fails.
The state floor blocks ordinary replay/downgrade after a newer authenticated
release has been observed, but cannot claim protection from compromise of the
launcher repository or reviewed release workflow.
