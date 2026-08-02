[CmdletBinding()]
param(
  [string]$OutputDirectory = "artifacts/win-x64"
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$outputRoot = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
  [System.IO.Path]::GetFullPath($OutputDirectory)
} else {
  [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
}
$payload = Join-Path $outputRoot "app"
$launcher = Join-Path $payload "STFCCommunityMod.Launcher.exe"
$archive = Join-Path $outputRoot "stfc-community-mod-launcher-win-x64.zip"
$checksum = "$archive.sha256"

Copy-Item -LiteralPath (Join-Path $PSScriptRoot "install-launcher.ps1") -Destination (Join-Path $payload "Install-Launcher.ps1") -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "uninstall-launcher.ps1") -Destination (Join-Path $payload "Uninstall-Launcher.ps1") -Force

if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) {
  throw "Launcher payload does not contain the expected executable: $launcher"
}

Compress-Archive -Path (Join-Path $payload "*") -DestinationPath $archive -CompressionLevel Optimal -Force
$archiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath $checksum -Value $archiveHash -Encoding utf8NoBOM

Write-Host "Packaged launcher archive: $archive"
Write-Host "SHA-256: $archiveHash"
