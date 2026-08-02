[CmdletBinding()]
param(
  [string]$OutputDirectory = "artifacts/win-x64"
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

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
if (Test-Path -LiteralPath $payload) {
  Remove-Item -LiteralPath $payload -Recurse -Force
}

dotnet publish $project `
  -c Release `
  -r win-x64 `
  --self-contained true `
  --output $payload `
  -p:PublishSingleFile=true

dotnet publish $updaterProject `
  -c Release `
  -r win-x64 `
  --self-contained true `
  --output $updaterPublish `
  -p:PublishSingleFile=true
Copy-Item `
  -LiteralPath (Join-Path $updaterPublish "STFCCommunityMod.Launcher.Updater.exe") `
  -Destination $payload `
  -Force
Remove-Item -LiteralPath $updaterPublish -Recurse -Force

$launcher = Join-Path $payload "STFCCommunityMod.Launcher.exe"
if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) {
  throw "Self-contained launcher executable was not published: $launcher"
}

& (Join-Path $PSScriptRoot "package.ps1") -OutputDirectory $outputRoot
& (Join-Path $PSScriptRoot "publish-bootstrapper.ps1") -OutputDirectory $outputRoot

Write-Host "Published launcher payload: $payload"
