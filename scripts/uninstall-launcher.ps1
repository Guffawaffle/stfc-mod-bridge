[CmdletBinding()]
param([switch]$RemoveState)

$ErrorActionPreference = "Stop"
if (Get-Process -Name "STFCCommunityMod.Launcher" -ErrorAction SilentlyContinue) {
  throw "Close STFC Community Mod Launcher before uninstalling it."
}
$localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$programs = [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)
$target = Join-Path $localAppData "Programs\STFC Community Mod Launcher"
$state = Join-Path $localAppData "STFC Community Mod Launcher"
$shortcuts = @(
  (Join-Path $programs "STFC Community Mod\STFC Community Mod Launcher.lnk"),
  (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)) "STFC Community Mod Launcher.lnk")
)
foreach ($shortcut in $shortcuts) {
  if (Test-Path -LiteralPath $shortcut) {
    Remove-Item -LiteralPath $shortcut -Force
  }
}
if (Test-Path -LiteralPath $target) {
  Remove-Item -LiteralPath $target -Recurse -Force
}
if ($RemoveState -and (Test-Path -LiteralPath $state)) {
  Remove-Item -LiteralPath $state -Recurse -Force
}
Write-Host "Launcher removed. The game installation and community mod files were not changed."
