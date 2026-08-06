# Security policy

Security support during pre-release testing applies only to the newest release
whose notes explicitly classify it as **Closed-alpha approved** or
**Public canary — qualification is still in progress**. Rejected or superseded
release candidates are not supported.

Report a potential vulnerability with GitHub's
[private vulnerability reporting](https://github.com/Guffawaffle/stfc-mod-bridge/security/advisories/new).
Do not open a public issue for exploitable authentication, path-handling,
credential-exposure, workflow, signing, or update-chain problems. Never include
tokens, cookies, private keys, or credentials in a report.

Ordinary defects and usability feedback belong in the repository's structured
issue forms.

Authenticode, exact hashes, and GitHub attestations establish particular
publisher or build-origin properties and byte integrity. They do not prove that
source code or dependencies are safe or free from malicious behavior.
Diagnostics remain local unless a user explicitly shares them. Redaction is
defense in depth, so review every export before attaching it.

The application bundles offline copies of the independent verification and
compromise-response guidance. The maintained repository copies are
[`INDEPENDENT_VERIFICATION.md`](docs/windows-launcher/INDEPENDENT_VERIFICATION.md)
and
[`COMPROMISE_RESPONSE.md`](docs/windows-launcher/COMPROMISE_RESPONSE.md).
