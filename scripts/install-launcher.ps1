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
$launcher = Join-Path $source "STFCCommunityMod.Launcher.exe"
$updater = Join-Path $source "STFCCommunityMod.Launcher.Updater.exe"

foreach ($file in @($launcher, $updater)) {
  if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
    throw "The launcher package is incomplete: $file"
  }
  $signature = Get-AuthenticodeSignature -LiteralPath $file
  if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
      $signature.SignerCertificate.Subject -notmatch '(?:^|,\s*)CN=Joseph Gustavson(?:,|$)') {
    throw "Refusing to install an unsigned or unexpected-publisher executable: $file"
  }
}

if (Get-Process -Name "STFCCommunityMod.Launcher" -ErrorAction SilentlyContinue) {
  throw "Close STFC Community Mod Launcher before installing or updating it."
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
  $shortcut = $shell.CreateShortcut((Join-Path $startMenuDirectory "STFC Community Mod Launcher.lnk"))
  $shortcut.TargetPath = Join-Path $target "STFCCommunityMod.Launcher.exe"
  $shortcut.WorkingDirectory = $target
  $shortcut.IconLocation = "$($shortcut.TargetPath),0"
  $shortcut.Save()
  if ($DesktopShortcut) {
    $desktop = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
    $desktopShortcut = $shell.CreateShortcut((Join-Path $desktop "STFC Community Mod Launcher.lnk"))
    $desktopShortcut.TargetPath = $shortcut.TargetPath
    $desktopShortcut.WorkingDirectory = $target
    $desktopShortcut.IconLocation = $shortcut.IconLocation
    $desktopShortcut.Save()
  }
  if ($hadPrevious -and (Test-Path -LiteralPath $backup)) {
    Remove-Item -LiteralPath $backup -Recurse -Force
  }
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
