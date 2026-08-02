# Launcher release withdrawal ledger

`release-withdrawals.jsonl` is the durable audit trail for launcher release
withdrawals. Each line is one JSON object committed through review. The ledger
does not mutate an immutable historical release manifest and is not consumed as
an executable configuration source.

A launcher release may be withdrawn only with a higher-version signed
replacement already published. The operator then removes the affected GitHub
release and tag and records the affected tag, replacement tag, reason,
operator, timestamp, and GitHub URLs here. Requiring a higher replacement keeps
new discovery on the monotonic path and prevents a yanked release from becoming
the newest eligible release again.

The v1 client additionally refuses a release whose numeric version does not
advance the running launcher. This blocks ordinary replay/downgrade after a
newer launcher has been installed. It does not claim protection from a
compromise of the launcher repository or GitHub release controls; a detached
authenticated index remains an explicit follow-up before issue #4 can close.
