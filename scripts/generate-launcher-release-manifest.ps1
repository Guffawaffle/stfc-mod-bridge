[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string]$Tag,
  [Parameter(Mandatory = $true)]
  [string]$TargetCommit,
  [Parameter(Mandatory = $true)]
  [long]$ReleaseSequence,
  [Parameter(Mandatory = $true)]
  [string]$IssuedAtUtc,
  [string]$Repository = "Guffawaffle/stfc-mod-bridge",
  [string]$OutputDirectory = "artifacts/win-x64",
  [string]$OutputPath = "artifacts/win-x64/stfc-mod-bridge-release-manifest.json",
  [string]$WithdrawalsPath = "docs/release-withdrawals/release-withdrawals.jsonl"
)

$ErrorActionPreference = "Stop"

if ($Tag -notmatch '^v(?<version>\d+\.\d+\.\d+(?:-rc\.\d+)?)$') {
  throw "Mod Bridge tags must use vX.Y.Z or vX.Y.Z-rc.N."
}
$version = $Matches.version
if ($TargetCommit -cnotmatch '^[0-9a-f]{40}$') {
  throw "TargetCommit must be exactly 40 lowercase hexadecimal characters."
}
if ($Repository -cnotmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
  throw "Repository must use owner/name coordinates."
}
if ($ReleaseSequence -le 0) {
  throw "ReleaseSequence must be a positive integer."
}
$issuedAt = [DateTimeOffset]::MinValue
if (-not [DateTimeOffset]::TryParseExact(
    $IssuedAtUtc,
    "yyyy-MM-dd'T'HH:mm:ss'Z'",
    [Globalization.CultureInfo]::InvariantCulture,
    [Globalization.DateTimeStyles]::AssumeUniversal,
    [ref]$issuedAt) -or $issuedAt.Offset -ne [TimeSpan]::Zero) {
  throw "IssuedAtUtc must be whole-second UTC RFC 3339."
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$outputRoot = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
  [System.IO.Path]::GetFullPath($OutputDirectory)
} else {
  [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
}
$manifestPath = if ([System.IO.Path]::IsPathRooted($OutputPath)) {
  [System.IO.Path]::GetFullPath($OutputPath)
} else {
  [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputPath))
}
$channel = if ($version.Contains('-rc.', [System.StringComparison]::Ordinal)) { "preview" } else { "stable" }
$validity = if ($channel -eq "preview") { [TimeSpan]::FromDays(14) } else { [TimeSpan]::FromDays(45) }
$expiresAt = $issuedAt.Add($validity)

$withdrawalLedgerPath = if ([System.IO.Path]::IsPathRooted($WithdrawalsPath)) {
  [System.IO.Path]::GetFullPath($WithdrawalsPath)
} else {
  [System.IO.Path]::GetFullPath((Join-Path $repoRoot $WithdrawalsPath))
}
if (-not (Test-Path -LiteralPath $withdrawalLedgerPath -PathType Leaf)) {
  throw "Release withdrawal ledger was not found: $withdrawalLedgerPath"
}
$withdrawalAllowedProperties = @(
  "schemaVersion", "channel", "kind", "value", "withdrawnAt", "reason",
  "operator", "detectedAt", "containedAt", "advisory", "replacementTag"
)
$withdrawals = @()
$lineNumber = 0
foreach ($line in Get-Content -LiteralPath $withdrawalLedgerPath) {
  $lineNumber++
  if ([string]::IsNullOrWhiteSpace($line)) {
    continue
  }
  $document = $null
  try {
    $document = [Text.Json.JsonDocument]::Parse($line)
    $root = $document.RootElement
    if ($root.ValueKind -ne [Text.Json.JsonValueKind]::Object) {
      throw "entry must be an object"
    }
    $entry = @{}
    foreach ($property in $root.EnumerateObject()) {
      if ($withdrawalAllowedProperties -cnotcontains $property.Name) {
        throw "entry contains unknown property '$($property.Name)'"
      }
      if ($entry.ContainsKey($property.Name)) {
        throw "entry contains duplicate property '$($property.Name)'"
      }
      $entry[$property.Name] = $property.Value.Clone()
    }
    foreach ($required in @("schemaVersion", "channel", "kind", "value", "withdrawnAt", "reason")) {
      if (-not $entry.ContainsKey($required)) {
        throw "entry is missing '$required'"
      }
    }
    if ($entry.schemaVersion.ValueKind -ne [Text.Json.JsonValueKind]::Number `
        -or $entry.schemaVersion.GetInt32() -ne 1) {
      throw "entry schemaVersion must be 1"
    }
    foreach ($name in @("channel", "kind", "value", "withdrawnAt", "reason")) {
      if ($entry[$name].ValueKind -ne [Text.Json.JsonValueKind]::String `
          -or [string]::IsNullOrWhiteSpace($entry[$name].GetString()) `
          -or $entry[$name].GetString().Length -gt 512) {
        throw "entry '$name' must be a non-empty string"
      }
    }
    if ($entry.ContainsKey("operator") `
        -and ($entry.operator.ValueKind -ne [Text.Json.JsonValueKind]::String `
          -or [string]::IsNullOrWhiteSpace($entry.operator.GetString()) `
          -or $entry.operator.GetString().Length -gt 128)) {
      throw "entry operator must be a bounded non-empty string"
    }
    foreach ($timestampName in @("detectedAt", "containedAt")) {
      if (-not $entry.ContainsKey($timestampName)) {
        continue
      }
      if ($entry[$timestampName].ValueKind -ne [Text.Json.JsonValueKind]::String) {
        throw "entry $timestampName must be whole-second UTC RFC 3339"
      }
      $optionalTimestamp = [DateTimeOffset]::MinValue
      if (-not [DateTimeOffset]::TryParseExact(
          $entry[$timestampName].GetString(),
          "yyyy-MM-dd'T'HH:mm:ss'Z'",
          [Globalization.CultureInfo]::InvariantCulture,
          [Globalization.DateTimeStyles]::AssumeUniversal,
          [ref]$optionalTimestamp) `
          -or $optionalTimestamp.Offset -ne [TimeSpan]::Zero) {
        throw "entry $timestampName must be whole-second UTC RFC 3339"
      }
    }
    if ($entry.ContainsKey("advisory") `
        -and $entry.advisory.ValueKind -ne [Text.Json.JsonValueKind]::Null) {
      $advisoryUri = $null
      if ($entry.advisory.ValueKind -ne [Text.Json.JsonValueKind]::String `
          -or $entry.advisory.GetString().Length -gt 512 `
          -or -not [Uri]::TryCreate($entry.advisory.GetString(), [UriKind]::Absolute, [ref]$advisoryUri) `
          -or -not $entry.advisory.GetString().StartsWith("https://", [StringComparison]::Ordinal)) {
        throw "entry advisory must be null or a bounded absolute HTTPS URI"
      }
    }
    if ($entry.ContainsKey("replacementTag") `
        -and $entry.replacementTag.ValueKind -ne [Text.Json.JsonValueKind]::Null `
        -and ($entry.replacementTag.ValueKind -ne [Text.Json.JsonValueKind]::String `
          -or $entry.replacementTag.GetString() -cnotmatch '^v\d+\.\d+\.\d+(?:-rc\.\d+)?$')) {
      throw "entry replacementTag must be null or a canonical Mod Bridge tag"
    }
    $entryChannel = $entry.channel.GetString()
    $kind = $entry.kind.GetString()
    $value = $entry.value.GetString()
    $reason = $entry.reason.GetString()
    if ($entryChannel -cnotin @("stable", "preview")) {
      throw "entry channel is unsupported"
    }
    if ($kind -ceq "release-sequence") {
      $parsedSequence = 0L
      if (-not [long]::TryParse(
          $value,
          [Globalization.NumberStyles]::None,
          [Globalization.CultureInfo]::InvariantCulture,
          [ref]$parsedSequence) `
          -or $parsedSequence -le 0 `
          -or $value -cne $parsedSequence.ToString([Globalization.CultureInfo]::InvariantCulture)) {
        throw "release-sequence value must be a canonical positive integer"
      }
    } elseif ($kind -cin @("manifest-sha256", "artifact-sha256")) {
      if ($value -cnotmatch '^[0-9a-f]{64}$') {
        throw "$kind value must be a lowercase SHA-256 digest"
      }
    } else {
      throw "entry kind is unsupported"
    }
    if ($reason -cnotin @("security", "integrity", "operator-error", "policy")) {
      throw "entry reason is unsupported"
    }
    $withdrawnAt = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParseExact(
        $entry.withdrawnAt.GetString(),
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::AssumeUniversal,
        [ref]$withdrawnAt) `
        -or $withdrawnAt.Offset -ne [TimeSpan]::Zero `
        -or $withdrawnAt -gt $issuedAt.AddMinutes(10)) {
      throw "entry withdrawnAt is invalid or postdates the manifest"
    }
    if ($entryChannel -ceq $channel) {
      $withdrawals += [ordered]@{
        kind = $kind
        value = $value
        withdrawnAt = $withdrawnAt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", [Globalization.CultureInfo]::InvariantCulture)
        reason = $reason
      }
    }
  } catch {
    throw "Release withdrawal ledger line $lineNumber is invalid: $($_.Exception.Message)"
  } finally {
    if ($null -ne $document) {
      $document.Dispose()
    }
  }
}
$withdrawals = @($withdrawals | Sort-Object -Property @{ Expression = { $_.kind } }, @{ Expression = { $_.value } })
for ($index = 1; $index -lt $withdrawals.Count; $index++) {
  if ($withdrawals[$index - 1].kind -ceq $withdrawals[$index].kind `
      -and $withdrawals[$index - 1].value -ceq $withdrawals[$index].value) {
    throw "Release withdrawal selectors must be unique."
  }
}

function New-Artifact {
  param(
    [string]$Id,
    [string]$Kind,
    [string]$Path,
    [string]$FileName,
    [string]$MediaType,
    [string]$Scope,
    [string[]]$SignedFiles = @()
  )

  if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
    throw "Release artifact was not found: $Path"
  }
  $authenticity = [ordered]@{ scheme = "authenticode"; scope = $Scope }
  if ($Scope -eq "contents") {
    $authenticity.signedFiles = $SignedFiles
  }
  return [ordered]@{
    id = $Id
    kind = $Kind
    platform = "windows"
    architecture = "x64"
    fileName = $FileName
    mediaType = $MediaType
    size = (Get-Item -LiteralPath $Path).Length
    sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    authenticity = $authenticity
  }
}

$archiveName = "stfc-mod-bridge-win-x64.zip"
$packageName = "STFCModBridge.msix"
$archive = Join-Path $outputRoot $archiveName
$package = Join-Path (Join-Path $outputRoot "package") $packageName
$manifest = [ordered]@{
  schemaVersion = 2
  releaseSequence = $ReleaseSequence
  issuedAt = $issuedAt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", [Globalization.CultureInfo]::InvariantCulture)
  expiresAt = $expiresAt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", [Globalization.CultureInfo]::InvariantCulture)
  releaseVersion = $version
  tag = $Tag
  channel = $channel
  releaseState = "active"
  minimumLauncherVersion = "0.1.0"
  source = [ordered]@{ repository = $Repository; targetCommit = $TargetCommit }
  manifestAuthenticity = [ordered]@{ scheme = "github-sigstore-build-provenance-v1" }
  artifacts = @(
    New-Artifact `
      -Id "windows-mod-bridge-archive-x64" `
      -Kind "windows-mod-bridge" `
      -Path $archive `
      -FileName $archiveName `
      -MediaType "application/zip" `
      -Scope "contents" `
      -SignedFiles @("STFCModBridge.exe", "STFCModBridge.Updater.exe")
    New-Artifact `
      -Id "windows-mod-bridge-msix-x64" `
      -Kind "windows-mod-bridge-package" `
      -Path $package `
      -FileName $packageName `
      -MediaType "application/msix" `
      -Scope "artifact"
  )
  withdrawals = $withdrawals
}

$parent = Split-Path -Parent $manifestPath
New-Item -ItemType Directory -Path $parent -Force | Out-Null
$temporaryPath = "$manifestPath.tmp"
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temporaryPath -Encoding utf8NoBOM
Move-Item -LiteralPath $temporaryPath -Destination $manifestPath -Force

Write-Host "Generated Mod Bridge release manifest: $manifestPath"
