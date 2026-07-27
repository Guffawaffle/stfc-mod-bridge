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
$archive = Join-Path $outputRoot "stfc-community-mod-launcher-win-x64.zip"
$checksum = "$archive.sha256"
$manifestPath = Join-Path $outputRoot "launcher-spike-manifest.json"

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

Compress-Archive -Path (Join-Path $payload "*") -DestinationPath $archive -CompressionLevel Optimal -Force
$archiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath $checksum -Value $archiveHash -Encoding utf8NoBOM

$manifest = [ordered]@{
  schemaVersion = 1
  architecture = "x64"
  framework = "net8.0-windows"
  selfContained = $true
  package = [ordered]@{
    fileName = [System.IO.Path]::GetFileName($archive)
    sha256 = $archiveHash
    size = (Get-Item -LiteralPath $archive).Length
  }
  installOwnership = [ordered]@{
    scope = "current-user"
    requiresElevation = $false
    programDirectory = "%LOCALAPPDATA%\Programs\STFC Community Mod Launcher"
    stateDirectory = "%LOCALAPPDATA%\STFC Community Mod Launcher"
  }
  selfUpdateStrategy = "verified-replace-on-exit-bootstrapper"
}

$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM

Write-Host "Published launcher payload: $payload"
Write-Host "Published launcher archive: $archive"
Write-Host "SHA-256: $archiveHash"
