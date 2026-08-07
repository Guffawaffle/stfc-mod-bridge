# Independently verify an STFC Mod Bridge release

This procedure verifies downloaded release bytes without trusting the running
Bridge UI. It separates four different claims:

- **local integrity:** the file size and SHA-256 match the authenticated
  manifest;
- **publisher evidence:** Authenticode validates the reviewed Windows publisher
  and timestamp;
- **build origin:** GitHub/Sigstore evidence binds the bytes to this repository,
  release workflow, tag, and commit;
- **release authorization and freshness:** the selected tag is the intended,
  non-withdrawn advancing release for the chosen channel.

None of those claims proves that the source or dependencies are free from
vulnerabilities or malicious behavior.

## Clean verification directory

Use current Windows, PowerShell 7, GitHub CLI, and the Windows SDK
`signtool`/`makeappx` tools. Record their versions. Create an empty directory,
then download exact asset names rather than accepting similarly named files:

```powershell
$repository = "Guffawaffle/stfc-mod-bridge"
$tag = "v0.1.0"
$root = Join-Path $PWD "bridge-verification-$($tag.TrimStart('v'))"
New-Item -ItemType Directory -Path $root -ErrorAction Stop | Out-Null

$assets = @(
  "STFCModBridge.appinstaller",
  "STFCModBridge.msix",
  "stfc-mod-bridge-win-x64.zip",
  "stfc-mod-bridge-release-manifest.json",
  "stfc-mod-bridge-release-attestation.json",
  "stfc-mod-bridge-release-selection-attestation.json",
  "stfc-mod-bridge-sbom.spdx.json",
  "STFCModBridge.ReleaseVerifier.spdx.json"
)
foreach ($asset in $assets) {
  gh release download $tag --repo $repository --dir $root --pattern $asset
  if ($LASTEXITCODE -ne 0) { throw "Download failed for $asset" }
}
```

Reject a missing, duplicate, unexpected, or renamed asset. Resolve and record the
immutable tag commit:

```powershell
$sourceCommit = (gh api "repos/$repository/commits/$tag" --jq .sha).Trim()
if ($sourceCommit -notmatch '^[0-9a-f]{40}$') { throw "Unexpected tag commit" }
```

## Authenticate the manifest before using it

The dedicated bundle must verify only the raw manifest bytes. GitHub CLI 2.89.0
supports this command shape; record the installed CLI version and JSON output:

```powershell
$workflow = "$repository/.github/workflows/release.yml"
$manifest = Join-Path $root "stfc-mod-bridge-release-manifest.json"
$selectionBundle = Join-Path $root "stfc-mod-bridge-release-selection-attestation.json"

gh attestation verify $manifest `
  --repo $repository `
  --signer-workflow $workflow `
  --source-ref "refs/tags/$tag" `
  --source-digest $sourceCommit `
  --deny-self-hosted-runners `
  --bundle $selectionBundle `
  --format json
if ($LASTEXITCODE -ne 0) { throw "Manifest attestation verification failed" }
```

Success requires exit code zero and exactly one verification result containing
exactly one statement subject named
`stfc-mod-bridge-release-manifest.json`, whose SHA-256 equals the local file.
The protected producer workflow enforces the same cardinality policy before
draft staging.

## Compare manifest identities and bytes

Parse the now-authenticated manifest. Require the exact repository, tag, full
source commit, channel, release state, closed schema, supported artifact IDs,
filenames, sizes, and SHA-256 values. For each declared artifact:

```powershell
$path = Join-Path $root $artifact.fileName
$actualSize = (Get-Item -LiteralPath $path).Length
$actualSha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualSize -ne $artifact.size -or $actualSha256 -cne $artifact.sha256) {
  throw "Manifest byte identity mismatch for $($artifact.fileName)"
}
```

Do not treat an unauthenticated manifest as the authority for its own hash.

## Verify signatures and package identity

Run Windows SDK verification against the signed MSIX and each extracted PE:

```powershell
signtool verify /pa /all /v /debug .\STFCModBridge.msix
signtool verify /pa /all /v /debug .\STFCModBridge.exe
signtool verify /pa /all /v /debug .\STFCModBridge.ReleaseVerifier.exe
signtool verify /pa /all /v /debug .\STFCModBridge.Updater.exe
```

Every signature must chain successfully and have a valid RFC 3161 timestamp.
Inspect each with `Get-AuthenticodeSignature` and require the complete publisher
DN, not only a display name:

```text
CN=Joseph Gustavson, O=Joseph Gustavson, L=Dousman, S=Wisconsin, C=US, PostalCode=53118
```

The certificate must contain code-signing EKU `1.3.6.1.5.5.7.3.3` and the
reviewed durable Azure Artifact Signing identity EKU
`1.3.6.1.4.1.311.97.664386437.910814316.510550690.722133748`.

Unpack a copy of the MSIX with `makeappx unpack`, then require the immutable
package Name, Publisher, Version, architecture, full-trust declarations,
content enforcement, and exactly the reviewed launcher and release-verifier
executables. Confirm the launcher's `ProductVersion` embeds the adjacent
verifier's exact SHA-256. Parse the
`.appinstaller` XML separately and require the same package Name, Publisher,
Version, and the expected immutable versioned MSIX URI. The App Installer file
is a mutable channel index; it is not independent release authorization.

## Verify every final subject

Use the broad bundle with the same repository/workflow/tag/commit/hosted-runner
constraints for the MSIX, App Installer descriptor, ZIP, manifest, both SBOMs,
and the launcher/release-verifier/updater extracted from the ZIP:

```powershell
gh attestation verify $subjectPath `
  --repo $repository `
  --signer-workflow $workflow `
  --source-ref "refs/tags/$tag" `
  --source-digest $sourceCommit `
  --deny-self-hosted-runners `
  --bundle (Join-Path $root "stfc-mod-bridge-release-attestation.json") `
  --format json
```

Changing one byte or substituting another repository, workflow, tag, commit,
runner class, subject name, or bundle must fail.

## Inspect embedded descriptive identity

For the extracted Bridge executable, record `FileVersionInfo.ProductVersion`
and confirm its embedded source revision agrees with the attested commit. For a
managed community-mod `version.dll`, inspect `FileVersionInfo.Comments` and the
canonical `stfc-identity-v1` marker. That marker is self-declared descriptive
provenance; it is never authenticity proof.

## Offline verification

Before disconnecting, capture the current trusted root without PowerShell's
ambiguous text redirection and record its hash, capture time, and CLI version:

```powershell
$rootJson = gh attestation trusted-root
if ($LASTEXITCODE -ne 0) { throw "Trusted-root capture failed" }
[IO.File]::WriteAllLines(
  (Join-Path $root "trusted_root.jsonl"),
  [string[]]$rootJson,
  [Text.UTF8Encoding]::new($false))
Get-FileHash (Join-Path $root "trusted_root.jsonl") -Algorithm SHA256
gh version
Get-Date -AsUTC -Format o
```

Move the subjects, both bundles, trusted-root snapshot, and compatible CLI to
the offline machine. Add
`--custom-trusted-root .\trusted_root.jsonl` to the verification commands.
Offline verification proves only the captured evidence against the captured
root. It cannot learn of later withdrawal, certificate/profile compromise,
root rotation, log compromise, or revocation.

## Evidence receipt

Retain the release URL, tag and commit, acquisition time, exact filenames,
sizes and SHA-256 values, Authenticode output, package/App Installer identities,
GitHub CLI version and JSON verification output, trusted-root hash/time when
used, negative-test results, and the final pass/fail decision. Never include
tokens, private paths, cookies, credentials, or raw user configuration.
