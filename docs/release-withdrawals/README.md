# Launcher release withdrawal ledger

`release-withdrawals.jsonl` is the durable audit trail for launcher release
withdrawals. Each line is one JSON object committed through review. The ledger
does not mutate an immutable historical release manifest and is not consumed as
an executable configuration source.

Emergency containment never waits for a higher replacement. Freeze publication
and channel advancement, preserve the release, tag, assets, attestations, logs,
and transparency evidence, then record the affected tag and digests, reason,
operator, detection/containment times, advisory, and replacement tag when one
exists. Prefer an immutable historical record: deleting a GitHub release, tag,
or hosted bundle does not revoke copies already captured by users or a
transparency log and can destroy evidence needed for investigation.

A numerically higher independently verified replacement remains the normal
recovery path because v1 clients require advancement. It is not a prerequisite
to containment. The ledger is currently an operator audit record, not an
authenticated runtime denylist; issue #71 owns that enforcement. Follow
[`COMPROMISE_RESPONSE.md`](../windows-launcher/COMPROMISE_RESPONSE.md) for the
authority-specific response and communication procedure.

The v1 client additionally refuses a release whose numeric version does not
advance the running launcher. This blocks ordinary replay/downgrade after a
newer launcher has been installed. It does not claim protection from a
compromise of the launcher repository or GitHub release controls; authenticated
withdrawal and recovery policy remains issue #71.
