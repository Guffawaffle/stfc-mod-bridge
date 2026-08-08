[CmdletBinding()]
param(
  [string]$OutputDirectory = "artifacts/win-x64",
  [string]$Version = "",
  [string]$SourceRevisionId = "",
  [string]$ReleaseVerifierPath = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$project = Join-Path $repoRoot "src\STFCCommunityMod.Launcher\STFCCommunityMod.Launcher.csproj"
$updaterProject = Join-Path $repoRoot "src\STFCCommunityMod.Launcher.Updater\STFCCommunityMod.Launcher.Updater.csproj"
$outputRoot = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
  [System.IO.Path]::GetFullPath($OutputDirectory)
} else {
  [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
}
$payload = Join-Path $outputRoot "app"
$updaterPublish = Join-Path $outputRoot "updater-publish"
$verifierBuild = Join-Path $outputRoot "release-verifier"
$verifier = ""
if ($ReleaseVerifierPath) {
  if ([System.IO.Path]::IsPathRooted($ReleaseVerifierPath)) {
    $verifier = [System.IO.Path]::GetFullPath($ReleaseVerifierPath)
  } else {
    $verifier = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $ReleaseVerifierPath))
  }
} else {
  & (Join-Path $PSScriptRoot "verify-release-verifier.ps1") -OutputDirectory $verifierBuild | Out-Host
  $verifier = Join-Path $verifierBuild "STFCModBridge.ReleaseVerifier.exe"
}
if (-not (Test-Path -LiteralPath $verifier -PathType Leaf) `
    -or [System.IO.Path]::GetFileName($verifier) -cne "STFCModBridge.ReleaseVerifier.exe") {
  throw "The reviewed release verifier was not found at its canonical path: $verifier"
}
$verifierSha256 = (Get-FileHash -LiteralPath $verifier -Algorithm SHA256).Hash.ToLowerInvariant()
if ($verifierSha256 -cnotmatch '^[0-9a-f]{64}$') {
  throw "The release verifier SHA-256 is invalid."
}
$buildProperties = @()
if ($Version) {
  $buildProperties += "-p:Version=$Version"
}
if ($SourceRevisionId) {
  $buildProperties += "-p:SourceRevisionId=$SourceRevisionId"
}
$buildProperties += "-p:ReleaseVerifierSha256=$verifierSha256"

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
if (Test-Path -LiteralPath $payload) {
  Remove-Item -LiteralPath $payload -Recurse -Force
}

dotnet publish $project `
  -c Release `
  -r win-x64 `
  --self-contained true `
  --output $payload `
  -p:RestoreLockedMode=true `
  -p:PublishSingleFile=true `
  @buildProperties

dotnet publish $updaterProject `
  -c Release `
  -r win-x64 `
  --self-contained true `
  --output $updaterPublish `
  -p:RestoreLockedMode=true `
  -p:PublishSingleFile=true `
  @buildProperties
Copy-Item `
  -LiteralPath (Join-Path $updaterPublish "STFCModBridge.Updater.exe") `
  -Destination $payload `
  -Force
Copy-Item -LiteralPath $verifier -Destination $payload -Force
Remove-Item -LiteralPath $updaterPublish -Recurse -Force

$launcher = Join-Path $payload "STFCModBridge.exe"
if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) {
  throw "Self-contained Mod Bridge executable was not published: $launcher"
}

& (Join-Path $PSScriptRoot "package.ps1") -OutputDirectory $outputRoot
& (Join-Path $PSScriptRoot "build-msix.ps1") `
  -OutputDirectory $outputRoot `
  -Version $(if ($Version) { $Version } else { "0.1.0-rc.1" }) `
  -UpdateBaseUri "https://updates.invalid/stfc-mod-bridge"
$inspectionArguments = @{ OutputDirectory = $outputRoot }
if ($SourceRevisionId) {
  $inspectionArguments.ExpectedSourceRevisionId = $SourceRevisionId
}
& (Join-Path $PSScriptRoot "inspect-package.ps1") @inspectionArguments

Write-Host "Published Mod Bridge payload: $payload"
Write-Host "Paired release verifier SHA-256: $verifierSha256"
