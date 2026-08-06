[CmdletBinding()]
param(
  [Parameter(Mandatory)]
  [string]$VerificationJsonPath,

  [Parameter(Mandatory)]
  [string]$SubjectPath,

  [string]$ExpectedSubjectName = "stfc-mod-bridge-release-manifest.json"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $VerificationJsonPath -PathType Leaf)) {
  throw "The release-selection verification result is missing."
}
if (-not (Test-Path -LiteralPath $SubjectPath -PathType Leaf)) {
  throw "The release-selection manifest is missing."
}

$results = @(Get-Content -LiteralPath $VerificationJsonPath -Raw |
    ConvertFrom-Json -Depth 100)
if ($results.Count -ne 1) {
  throw "Release-selection evidence must contain exactly one verified attestation result; found $($results.Count)."
}

$subjects = @($results[0].verificationResult.statement.subject)
if ($subjects.Count -ne 1) {
  throw "Release-selection evidence must contain exactly one statement subject; found $($subjects.Count)."
}

$subject = $subjects[0]
if ([string]$subject.name -cne $ExpectedSubjectName) {
  throw "Release-selection evidence names unexpected subject '$($subject.name)'."
}

$expectedDigest = (Get-FileHash -LiteralPath $SubjectPath -Algorithm SHA256).Hash.ToLowerInvariant()
$attestedDigest = [string]$subject.digest.sha256
if ($attestedDigest -cne $expectedDigest) {
  throw "Release-selection evidence does not match the exact manifest SHA-256."
}

Write-Host "Release-selection attestation policy passed for '$ExpectedSubjectName' ($expectedDigest)."
