# Closed release-selection verifier

This internal helper verifies exact local release-manifest bytes against a
manifest-only GitHub/Sigstore bundle. It is groundwork for issue #71 and does
not authorize discovery, download, installation, or update in this slice.

## Boundary

The executable accepts one JSON request on standard input and emits one JSON
receipt on standard output. It accepts no command-line authorities, URLs,
regular expressions, trust-root paths, or network mode. Repository, numeric
owner/repository IDs, workflow, OIDC issuer, tag-ref grammar, event, runner,
SLSA predicate/build type, subject basename, and public-good trust root are
compiled into the helper.

Inputs are limited to an 8-KiB request and two absolute, exactly named, regular
local files of at most 1 MiB each. JSON depth, trailing content, unknown request
fields, links/devices, file replacement during read, multiple DSSE signatures,
multiple Rekor entries, and multiple statement subjects fail closed. The
receipt is bounded to 64 KiB by the .NET process boundary and is parsed as a
closed schema with fixed-time digest comparisons.

The helper performs no network request and its own source imports no network,
process-launch, plugin, cgo, or syscall package. `sigstore-go` is a
general-purpose library, so its compiled dependency graph contains transport
packages even though this helper exposes no call path to them. The v1 package
qualification in #97 must review that final call graph and packaging boundary;
this component cannot enroll a downloaded root.

## Locked inputs

- Go `1.26.5`
- `github.com/sigstore/sigstore-go` `v1.3.0`
- embedded public-good root trust epoch `1`
- normalized root-document SHA-256
  `844a1c6de3986c9f02070266b25e0d1a2fa99ceccc89f6b9ad90aae47b62a16e`
- 71 compiled third-party modules, each version and module checksum recorded in
  `dependencies.v1.txt`

The root was captured on 2026-08-06 as the first, public-good document emitted
by `gh attestation trusted-root`. The separate GitHub private-instance root was
discarded because this is a public repository. The source file carries one
terminal LF for repository text hygiene; the helper requires that exact
single-line shape and hashes the normalized JSON document without the LF.

Run `scripts/verify-release-verifier.ps1` from the repository root. The script
requires the exact Go version, proves `go.mod`/`go.sum` are tidy, compares the
compiled dependency closure to the reviewed inventory, runs tests, and emits a
trimmed Windows x64 executable plus a matching 71-module SPDX inventory under
`artifacts/release-verifier`.
CI also runs `govulncheck` v1.6.0. The 2026-08-06 audit found no reachable
symbol or package vulnerability. It reported the module-only advisory
GO-2026-5932 for the transitive, uncalled `golang.org/x/crypto/openpgp`
package; this residual is recorded rather than mislabeled as reachable code.

The captured rc.4 fixture is intentionally a negative production fixture: it
is valid public GitHub/Sigstore evidence under the closed identity policy, but
predates the dedicated manifest-only attestation and therefore has six
subjects. The helper must reach and reject subject cardinality. The first
positive production-policy fixture can only be captured after a protected tag
runs the integrated release producer.
