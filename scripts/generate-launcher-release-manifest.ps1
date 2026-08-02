[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string]$Tag,
  [Parameter(Mandatory = $true)]
  [string]$TargetCommit,
  [string]$Repository = "Guffawaffle/stfc-mod-launcher",
  [string]$OutputDirectory = "artifacts/win-x64",
  [string]$OutputPath = "artifacts/win-x64/stfc-mod-control-release-manifest.json"
)

$ErrorActionPreference = "Stop"

if ($Tag -notmatch '^v(?<version>\d+\.\d+\.\d+(?:-rc\.\d+)?)$') {
  throw "Mod Control tags must use vX.Y.Z or vX.Y.Z-rc.N."
}
$version = $Matches.version
if ($TargetCommit -cnotmatch '^[0-9a-f]{40}$') {
  throw "TargetCommit must be exactly 40 lowercase hexadecimal characters."
}
if ($Repository -cnotmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
  throw "Repository must use owner/name coordinates."
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

$archiveName = "stfc-mod-control-win-x64.zip"
$setupName = "STFCModControl.Setup.exe"
$archive = Join-Path $outputRoot $archiveName
$setup = Join-Path (Join-Path $outputRoot "setup") $setupName
$manifest = [ordered]@{
  schemaVersion = 1
  releaseVersion = $version
  tag = $Tag
  channel = $channel
  releaseState = "active"
  minimumLauncherVersion = "0.1.0"
  source = [ordered]@{ repository = $Repository; targetCommit = $TargetCommit }
  manifestAuthenticity = [ordered]@{ scheme = "none" }
  artifacts = @(
    New-Artifact `
      -Id "windows-mod-control-archive-x64" `
      -Kind "windows-mod-control" `
      -Path $archive `
      -FileName $archiveName `
      -MediaType "application/zip" `
      -Scope "contents" `
      -SignedFiles @("STFCModControl.exe", "STFCModControl.Updater.exe")
    New-Artifact `
      -Id "windows-mod-control-setup-x64" `
      -Kind "windows-mod-control-setup" `
      -Path $setup `
      -FileName $setupName `
      -MediaType "application/vnd.microsoft.portable-executable" `
      -Scope "artifact"
  )
}

$parent = Split-Path -Parent $manifestPath
New-Item -ItemType Directory -Path $parent -Force | Out-Null
$temporaryPath = "$manifestPath.tmp"
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temporaryPath -Encoding utf8NoBOM
Move-Item -LiteralPath $temporaryPath -Destination $manifestPath -Force

Write-Host "Generated Mod Control release manifest: $manifestPath"
