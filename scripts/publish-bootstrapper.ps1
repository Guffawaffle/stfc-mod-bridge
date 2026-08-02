[CmdletBinding()]
param(
  [string]$OutputDirectory = "artifacts/win-x64",
  [string]$PayloadArchive,
  [string]$Version = "",
  [string]$SourceRevisionId = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$outputRoot = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
  [System.IO.Path]::GetFullPath($OutputDirectory)
} else {
  [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
}
$archive = if ($PayloadArchive) {
  [System.IO.Path]::GetFullPath($PayloadArchive)
} else {
  Join-Path $outputRoot "stfc-community-mod-launcher-win-x64.zip"
}
$project = Join-Path $repoRoot "src\STFCCommunityMod.Launcher.Setup\STFCCommunityMod.Launcher.Setup.csproj"
$setupOutput = Join-Path $outputRoot "setup"
$buildProperties = @()
if ($Version) {
  $buildProperties += "-p:Version=$Version"
}
if ($SourceRevisionId) {
  $buildProperties += "-p:SourceRevisionId=$SourceRevisionId"
}

if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
  throw "Packaged launcher payload was not found: $archive"
}
if (Test-Path -LiteralPath $setupOutput) {
  Remove-Item -LiteralPath $setupOutput -Recurse -Force
}

dotnet publish $project `
  -c Release `
  -r win-x64 `
  --self-contained true `
  --output $setupOutput `
  -p:PublishSingleFile=true `
  -p:RequireLauncherPayload=true `
  -p:LauncherPayloadPath=$archive `
  @buildProperties

$setup = Join-Path $setupOutput "STFCCommunityMod.Launcher.Setup.exe"
if (-not (Test-Path -LiteralPath $setup -PathType Leaf)) {
  throw "Single-file launcher setup was not published: $setup"
}
Get-ChildItem -LiteralPath $setupOutput -File |
  Where-Object { $_.FullName -ne $setup } |
  Remove-Item -Force

Write-Host "Published one-file launcher setup: $setup"
