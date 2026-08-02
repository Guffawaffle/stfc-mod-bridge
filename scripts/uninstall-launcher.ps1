[CmdletBinding()]
param([switch]$RemoveState)

$ErrorActionPreference = "Stop"
$productName = "STFC Mod Control"
if (Get-Process -Name "STFCModControl" -ErrorAction SilentlyContinue) {
  throw "Close $productName before uninstalling it."
}
$localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$programs = [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)
$target = Join-Path $localAppData "Programs\STFC Mod Control"
$state = Join-Path $localAppData "STFC Mod Control"
$shortcuts = @(
  (Join-Path $programs "STFC Mod Control\$productName.lnk"),
  (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)) "$productName.lnk")
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
$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\STFCModControl"
if (Test-Path -LiteralPath $uninstallKey) {
  Remove-Item -LiteralPath $uninstallKey -Recurse -Force
}
Write-Host "$productName removed. The game installation and community mod files were not changed."
