[CmdletBinding()]
param(
  [string]$SourceDirectory = $PSScriptRoot,
  [switch]$DesktopShortcut
)

$ErrorActionPreference = "Stop"
$source = [System.IO.Path]::GetFullPath($SourceDirectory)
$localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$programs = [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)
$target = Join-Path $localAppData "Programs\STFC Community Mod Launcher"
$state = Join-Path $localAppData "STFC Community Mod Launcher"
$productName = "STFC Mod Control"
$legacyProductName = "STFC Community Mod Launcher"
$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\STFCModControl"
$launcher = Join-Path $source "STFCCommunityMod.Launcher.exe"
$updater = Join-Path $source "STFCCommunityMod.Launcher.Updater.exe"

foreach ($file in @($launcher, $updater)) {
  if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
    throw "The Mod Control package is incomplete: $file"
  }
  $signature = Get-AuthenticodeSignature -LiteralPath $file
  if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
      $signature.SignerCertificate.Subject -notmatch '(?:^|,\s*)CN=Joseph Gustavson(?:,|$)') {
    throw "Refusing to install an unsigned or unexpected-publisher executable: $file"
  }
}

if (Get-Process -Name "STFCCommunityMod.Launcher" -ErrorAction SilentlyContinue) {
  throw "Close $productName before installing or updating it."
}

$transaction = [Guid]::NewGuid().ToString("N")
$transactionRoot = Join-Path $state "install\$transaction"
$stage = Join-Path $transactionRoot "stage"
$backup = Join-Path $transactionRoot "backup"
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Copy-Item -Path (Join-Path $source "*") -Destination $stage -Recurse -Force

$hadPrevious = Test-Path -LiteralPath $target -PathType Container
try {
  if ($hadPrevious) {
    Move-Item -LiteralPath $target -Destination $backup
  }
  Move-Item -LiteralPath $stage -Destination $target

  $shell = New-Object -ComObject WScript.Shell
  $startMenuDirectory = Join-Path $programs "STFC Community Mod"
  New-Item -ItemType Directory -Path $startMenuDirectory -Force | Out-Null
  $legacyStartShortcut = Join-Path $startMenuDirectory "$legacyProductName.lnk"
  if (Test-Path -LiteralPath $legacyStartShortcut) {
    Remove-Item -LiteralPath $legacyStartShortcut -Force
  }
  $shortcut = $shell.CreateShortcut((Join-Path $startMenuDirectory "$productName.lnk"))
  $shortcut.TargetPath = Join-Path $target "STFCCommunityMod.Launcher.exe"
  $shortcut.WorkingDirectory = $target
  $shortcut.IconLocation = "$($shortcut.TargetPath),0"
  $shortcut.Save()
  $desktop = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
  $legacyDesktopShortcut = Join-Path $desktop "$legacyProductName.lnk"
  if (Test-Path -LiteralPath $legacyDesktopShortcut) {
    Remove-Item -LiteralPath $legacyDesktopShortcut -Force
  }
  if ($DesktopShortcut) {
    $desktopShortcut = $shell.CreateShortcut((Join-Path $desktop "$productName.lnk"))
    $desktopShortcut.TargetPath = $shortcut.TargetPath
    $desktopShortcut.WorkingDirectory = $target
    $desktopShortcut.IconLocation = $shortcut.IconLocation
    $desktopShortcut.Save()
  }
  if ($hadPrevious -and (Test-Path -LiteralPath $backup)) {
    Remove-Item -LiteralPath $backup -Recurse -Force
  }
  New-Item -Path $uninstallKey -Force | Out-Null
  $uninstallScript = Join-Path $target "Uninstall-Launcher.ps1"
  $windowsPowerShell = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Windows)) "System32\WindowsPowerShell\v1.0\powershell.exe"
  $uninstallCommand = "`"$windowsPowerShell`" -NoProfile -ExecutionPolicy Bypass -File `"$uninstallScript`""
  Set-ItemProperty -Path $uninstallKey -Name DisplayName -Value $productName
  Set-ItemProperty -Path $uninstallKey -Name DisplayIcon -Value "$(Join-Path $target 'STFCCommunityMod.Launcher.exe'),0"
  Set-ItemProperty -Path $uninstallKey -Name DisplayVersion -Value "0.1.0"
  Set-ItemProperty -Path $uninstallKey -Name Publisher -Value "Joseph Gustavson"
  Set-ItemProperty -Path $uninstallKey -Name InstallLocation -Value $target
  Set-ItemProperty -Path $uninstallKey -Name UninstallString -Value $uninstallCommand
  Set-ItemProperty -Path $uninstallKey -Name QuietUninstallString -Value $uninstallCommand
  New-ItemProperty -Path $uninstallKey -Name NoModify -Value 1 -PropertyType DWord -Force | Out-Null
  New-ItemProperty -Path $uninstallKey -Name NoRepair -Value 1 -PropertyType DWord -Force | Out-Null
  Start-Process -FilePath (Join-Path $target "STFCCommunityMod.Launcher.exe") -WorkingDirectory $target
}
catch {
  if (Test-Path -LiteralPath $target) {
    Remove-Item -LiteralPath $target -Recurse -Force
  }
  if ($hadPrevious -and (Test-Path -LiteralPath $backup)) {
    Move-Item -LiteralPath $backup -Destination $target
  }
  throw
}
