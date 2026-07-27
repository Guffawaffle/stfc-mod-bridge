[CmdletBinding()]
param(
  [string]$OutputDirectory = "windows-launcher/artifacts/win-x64"
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$project = Join-Path $repoRoot "windows-launcher\src\STFCCommunityMod.Launcher\STFCCommunityMod.Launcher.csproj"
$outputRoot = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
  [System.IO.Path]::GetFullPath($OutputDirectory)
} else {
  [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
}
$payload = Join-Path $outputRoot "app"

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

$launcher = Join-Path $payload "STFCCommunityMod.Launcher.exe"
if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) {
  throw "Self-contained launcher executable was not published: $launcher"
}

& (Join-Path $PSScriptRoot "package.ps1") -OutputDirectory $outputRoot

Write-Host "Published launcher payload: $payload"
