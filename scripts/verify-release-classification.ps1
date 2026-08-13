[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string]$ReleaseBodyPath
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$resolvedBodyPath = [System.IO.Path]::GetFullPath($ReleaseBodyPath)
if (-not (Test-Path -LiteralPath $resolvedBodyPath -PathType Leaf)) {
  throw "The published release body was not found."
}
$bodyFile = Get-Item -LiteralPath $resolvedBodyPath
if ($bodyFile.Length -gt 1MB) {
  throw "The published release body exceeds the reviewed 1 MiB limit."
}
$body = [System.IO.File]::ReadAllText($resolvedBodyPath)
if ([string]::IsNullOrWhiteSpace($body)) {
  throw "The published release body is empty."
}
if ([System.Text.RegularExpressions.Regex]::IsMatch(
    $body,
    "Qualification draft",
    [System.Text.RegularExpressions.RegexOptions]::IgnoreCase `
      -bor [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)) {
  throw "The published release still contains the Qualification draft placeholder."
}

$closedAlpha = "Closed-alpha approved"
$publicCanary = "Public canary — qualification is still in progress"
$allowed = @($closedAlpha, $publicCanary)
$occurrenceCount = 0
foreach ($classification in $allowed) {
  $occurrenceCount += [System.Text.RegularExpressions.Regex]::Matches(
    $body,
    [System.Text.RegularExpressions.Regex]::Escape($classification),
    [System.Text.RegularExpressions.RegexOptions]::CultureInvariant).Count
}
if ($occurrenceCount -ne 1) {
  throw "The published release must contain exactly one allowed qualification classification."
}

$lines = [System.Text.RegularExpressions.Regex]::Split($body, "\r?\n")
$classificationLines = [System.Collections.Generic.List[string]]::new()
$nonCodeLines = [System.Collections.Generic.List[string]]::new()
$inCodeFence = $false
foreach ($line in $lines) {
  if ($line -match '^[ ]{0,3}(?:>\s*)?(?:`{3,}|~{3,})') {
    $inCodeFence = -not $inCodeFence
    continue
  }
  if ($inCodeFence) {
    continue
  }
  $nonCodeLines.Add($line)
  foreach ($classification in $allowed) {
    $classificationLinePattern = '^[ ]{0,3}(?:>\s*)?\*{0,2}' `
      + [System.Text.RegularExpressions.Regex]::Escape($classification) `
      + '\.?\*{0,2}\s*$'
    if ([System.Text.RegularExpressions.Regex]::IsMatch(
        $line,
        $classificationLinePattern,
        [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)) {
      $classificationLines.Add($classification)
    }
  }
}
if ($inCodeFence) {
  throw "The published release body contains an unterminated Markdown code fence."
}
if ($classificationLines.Count -ne 1) {
  throw "The allowed qualification classification must appear on one standalone Markdown line."
}

$selected = $classificationLines[0]
if ($selected -ceq $publicCanary) {
  $openHeadingIndexes = @(
    0..($nonCodeLines.Count - 1) | Where-Object {
      $nonCodeLines[$_] -cmatch '^[ ]{0,3}##\s+Qualification still open\s*$'
    })
  if ($openHeadingIndexes.Count -ne 1) {
    throw "A public canary must contain exactly one 'Qualification still open' section."
  }
  $hasOpenCheck = $false
  for ($index = $openHeadingIndexes[0] + 1; $index -lt $nonCodeLines.Count; $index++) {
    if ($nonCodeLines[$index] -cmatch '^[ ]{0,3}#{1,6}\s+') {
      break
    }
    if ($nonCodeLines[$index] -cmatch '^[ ]{0,3}[-*+]\s+\S') {
      $hasOpenCheck = $true
      break
    }
  }
  if (-not $hasOpenCheck) {
    throw "A public canary must enumerate at least one open qualification check."
  }
}

Write-Host "PASS: published release classification is '$selected'."
