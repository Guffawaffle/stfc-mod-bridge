# Captured broad-attestation fixture

These exact public release assets were downloaded from
`Guffawaffle/stfc-mod-bridge` release `v0.1.0-rc.4` on 2026-08-06:

- `stfc-mod-bridge-release-manifest.json`
  (`f4eef6a26df203b3b67f9979a3c4668fee5de0c515ce7ca569318c1ff6d6a279`)
- `stfc-mod-bridge-release-selection-attestation.json` (the rc.4 broad bundle,
  renamed to the consumer basename so the fixture reaches cardinality policy;
  `23d2d0959d7c132b1aa3782be572f2659af255a092c30a65c386ac4d303800e5`)

The bundle is cryptographically valid for commit
`37c61305a553ec155c05186a0e6549c70b4ed489`, but it intentionally has six
subjects. The closed release-selection verifier must therefore reject it after
cryptographic verification. The first positive production-policy fixture can
only be captured after a protected tag produces the new manifest-only bundle.
