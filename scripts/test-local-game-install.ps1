[CmdletBinding()]
param(
  [string]$GameDirectory = $env:STFC_BRIDGE_INTEGRATION_GAME_DIR,
  [switch]$AllowRestorableMutation,
  [switch]$UseLiveProviderReleases
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($GameDirectory)) {
  throw "Supply -GameDirectory or set STFC_BRIDGE_INTEGRATION_GAME_DIR."
}

$resolvedTarget = (Resolve-Path -LiteralPath $GameDirectory -ErrorAction Stop).Path
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$project = Join-Path `
  $repoRoot `
  "tests\STFCCommunityMod.Launcher.LocalGameIntegration.Tests\STFCCommunityMod.Launcher.LocalGameIntegration.Tests.csproj"
$originalEnable = $env:STFC_BRIDGE_RUN_LOCAL_GAME_INTEGRATION
$originalDirectory = $env:STFC_BRIDGE_INTEGRATION_GAME_DIR
$originalMutation = $env:STFC_BRIDGE_ALLOW_RESTORABLE_MUTATION
$originalLiveProviders = $env:STFC_BRIDGE_USE_LIVE_PROVIDER_RELEASES

try {
  $env:STFC_BRIDGE_RUN_LOCAL_GAME_INTEGRATION = "1"
  $env:STFC_BRIDGE_INTEGRATION_GAME_DIR = $resolvedTarget

  if ($UseLiveProviderReleases -and -not $AllowRestorableMutation) {
    throw "-UseLiveProviderReleases requires -AllowRestorableMutation."
  }

  $filter = "TestCategory=LocalGameIntegration"
  if ($AllowRestorableMutation) {
    $env:STFC_BRIDGE_ALLOW_RESTORABLE_MUTATION = "1"
    $filter = "(TestCategory=LocalGameIntegration|TestCategory=LocalGameMutation)"
  }
  if ($UseLiveProviderReleases) {
    $env:STFC_BRIDGE_USE_LIVE_PROVIDER_RELEASES = "1"
  }

  $profile = if ($AllowRestorableMutation) { "Inspect and restorable mutation" } else { "Inspect" }
  Write-Host "Running opted-in local game-install profile: $profile"
  dotnet test $project `
    -c Release `
    --filter $filter `
    --logger "console;verbosity=normal"
  if ($LASTEXITCODE -ne 0) {
    throw "The local game-install integration suite failed with exit code $LASTEXITCODE."
  }
}
finally {
  $env:STFC_BRIDGE_RUN_LOCAL_GAME_INTEGRATION = $originalEnable
  $env:STFC_BRIDGE_INTEGRATION_GAME_DIR = $originalDirectory
  $env:STFC_BRIDGE_ALLOW_RESTORABLE_MUTATION = $originalMutation
  $env:STFC_BRIDGE_USE_LIVE_PROVIDER_RELEASES = $originalLiveProviders
}
