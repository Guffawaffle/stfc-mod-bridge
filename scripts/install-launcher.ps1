[CmdletBinding()]
param(
  [string]$SourceDirectory = $PSScriptRoot,
  [switch]$DesktopShortcut
)

$ErrorActionPreference = "Stop"
$source = [System.IO.Path]::GetFullPath($SourceDirectory)
$localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$programs = [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)
$target = Join-Path $localAppData "Programs\STFC Mod Bridge"
$state = Join-Path $localAppData "STFC Mod Bridge"
$productName = "STFC Mod Bridge"
$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\STFCModBridge"
$launcher = Join-Path $source "STFCModBridge.exe"
$updater = Join-Path $source "STFCModBridge.Updater.exe"
$expectedPublisherName = [System.Security.Cryptography.X509Certificates.X500DistinguishedName]::new(
  "CN=Joseph Gustavson, O=Joseph Gustavson, L=Dousman, S=Wisconsin, C=US, PostalCode=53118")
$expectedArtifactSigningIdentityEku = "1.3.6.1.4.1.311.97.664386437.910814316.510550690.722133748"

foreach ($file in @($launcher, $updater)) {
  if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
    throw "The Mod Bridge package is incomplete: $file"
  }
  $signature = Get-AuthenticodeSignature -LiteralPath $file
  $publisherMatches = $null -ne $signature.SignerCertificate -and [System.Linq.Enumerable]::SequenceEqual(
    $signature.SignerCertificate.SubjectName.RawData,
    $expectedPublisherName.RawData)
  $hasCodeSigningEku = $null -ne $signature.SignerCertificate -and @(
    $signature.SignerCertificate.EnhancedKeyUsageList | Where-Object {
      $_.ObjectId -eq "1.3.6.1.5.5.7.3.3"
    }).Count -eq 1
  $hasDurableIdentityEku = $null -ne $signature.SignerCertificate -and @(
    $signature.SignerCertificate.EnhancedKeyUsageList | Where-Object {
      $_.ObjectId -eq $expectedArtifactSigningIdentityEku
    }).Count -eq 1
  if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
      -not $publisherMatches -or
      -not $hasCodeSigningEku -or
      -not $hasDurableIdentityEku -or
      $null -eq $signature.TimeStamperCertificate) {
    throw "Refusing to install an unsigned or unexpected-publisher executable: $file"
  }
}

if (Get-Process -Name "STFCModBridge" -ErrorAction SilentlyContinue) {
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
  $startMenuDirectory = Join-Path $programs "STFC Mod Bridge"
  New-Item -ItemType Directory -Path $startMenuDirectory -Force | Out-Null
  $shortcut = $shell.CreateShortcut((Join-Path $startMenuDirectory "$productName.lnk"))
  $shortcut.TargetPath = Join-Path $target "STFCModBridge.exe"
  $shortcut.WorkingDirectory = $target
  $shortcut.IconLocation = "$($shortcut.TargetPath),0"
  $shortcut.Save()
  $desktop = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
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
  Set-ItemProperty -Path $uninstallKey -Name DisplayIcon -Value "$(Join-Path $target 'STFCModBridge.exe'),0"
  Set-ItemProperty -Path $uninstallKey -Name DisplayVersion -Value "0.1.0"
  Set-ItemProperty -Path $uninstallKey -Name Publisher -Value "Joseph Gustavson"
  Set-ItemProperty -Path $uninstallKey -Name InstallLocation -Value $target
  Set-ItemProperty -Path $uninstallKey -Name UninstallString -Value $uninstallCommand
  Set-ItemProperty -Path $uninstallKey -Name QuietUninstallString -Value $uninstallCommand
  New-ItemProperty -Path $uninstallKey -Name NoModify -Value 1 -PropertyType DWord -Force | Out-Null
  New-ItemProperty -Path $uninstallKey -Name NoRepair -Value 1 -PropertyType DWord -Force | Out-Null
  Start-Process -FilePath (Join-Path $target "STFCModBridge.exe") -WorkingDirectory $target
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
