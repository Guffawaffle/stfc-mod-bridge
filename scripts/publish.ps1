[CmdletBinding()]
param(
  [string]$OutputDirectory = "artifacts/win-x64",
  [string]$Version = "",
  [string]$SourceRevisionId = ""
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
$buildProperties = @()
if ($Version) {
  $buildProperties += "-p:Version=$Version"
}
if ($SourceRevisionId) {
  $buildProperties += "-p:SourceRevisionId=$SourceRevisionId"
}

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
Remove-Item -LiteralPath $updaterPublish -Recurse -Force

$launcher = Join-Path $payload "STFCModBridge.exe"
if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) {
  throw "Self-contained Mod Bridge executable was not published: $launcher"
}

& (Join-Path $PSScriptRoot "package.ps1") -OutputDirectory $outputRoot
& (Join-Path $PSScriptRoot "publish-bootstrapper.ps1") `
  -OutputDirectory $outputRoot `
  -Version $Version `
  -SourceRevisionId $SourceRevisionId
& (Join-Path $PSScriptRoot "inspect-package.ps1") -OutputDirectory $outputRoot

Write-Host "Published Mod Bridge payload: $payload"
