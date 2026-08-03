[CmdletBinding()]
param(
  [string]$GameDirectory = $env:STFC_BRIDGE_INTEGRATION_GAME_DIR
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

try {
  $env:STFC_BRIDGE_RUN_LOCAL_GAME_INTEGRATION = "1"
  $env:STFC_BRIDGE_INTEGRATION_GAME_DIR = $resolvedTarget

  Write-Host "Running the opted-in read-only game-install integration suite..."
  dotnet test $project `
    -c Release `
    --filter "TestCategory=LocalGameIntegration" `
    --logger "console;verbosity=normal"
  if ($LASTEXITCODE -ne 0) {
    throw "The local game-install integration suite failed with exit code $LASTEXITCODE."
  }
}
finally {
  $env:STFC_BRIDGE_RUN_LOCAL_GAME_INTEGRATION = $originalEnable
  $env:STFC_BRIDGE_INTEGRATION_GAME_DIR = $originalDirectory
}
