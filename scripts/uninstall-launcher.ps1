[CmdletBinding()]
param(
  [switch]$RemoveState,
  [ValidateRange(0, [int]::MaxValue)]
  [int]$WaitForProcessId = 0,
  [switch]$Quiet
)

$ErrorActionPreference = "Stop"
$productName = "STFC Mod Bridge"
function Assert-OrdinaryDirectoryTree([string]$Path, [string]$Label) {
  if (-not (Test-Path -LiteralPath $Path)) {
    return
  }

  $root = Get-Item -LiteralPath $Path -Force
  if (-not $root.PSIsContainer) {
    throw "$Label is not a directory: $Path"
  }
  $pending = [Collections.Generic.Stack[IO.DirectoryInfo]]::new()
  $pending.Push([IO.DirectoryInfo]$root)
  while ($pending.Count -gt 0) {
    $directory = $pending.Pop()
    if (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
      throw "$Label contains a filesystem link or reparse point: $($directory.FullName)"
    }
    foreach ($entry in $directory.EnumerateFileSystemInfos()) {
      if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Label contains a filesystem link or reparse point: $($entry.FullName)"
      }
      if (($entry.Attributes -band [IO.FileAttributes]::Directory) -ne 0) {
        $pending.Push([IO.DirectoryInfo]$entry)
      }
    }
  }
}

if ($WaitForProcessId -gt 0) {
  $scheduledProcess = Get-Process -Id $WaitForProcessId -ErrorAction SilentlyContinue
  if ($scheduledProcess) {
    if ($scheduledProcess.ProcessName -ne "STFCModBridge") {
      throw "The uninstall wait target is not STFC Mod Bridge."
    }
    $scheduledProcess | Wait-Process -Timeout 30 -ErrorAction Stop
  }
}
if (Get-Process -Name "STFCModBridge" -ErrorAction SilentlyContinue) {
  throw "Close $productName before uninstalling it."
}
$localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$programs = [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)
$target = Join-Path $localAppData "Programs\STFC Mod Bridge"
$state = Join-Path $localAppData "STFC Mod Bridge"
$shortcuts = @(
  (Join-Path $programs "STFC Mod Bridge\$productName.lnk"),
  (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)) "$productName.lnk")
)
Assert-OrdinaryDirectoryTree -Path $target -Label "The Mod Bridge program directory"
if ($RemoveState) {
  Assert-OrdinaryDirectoryTree -Path $state -Label "The Mod Bridge local-data directory"
}
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
$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\STFCModBridge"
if (Test-Path -LiteralPath $uninstallKey) {
  Remove-Item -LiteralPath $uninstallKey -Recurse -Force
}
if (-not $Quiet) {
  Write-Host "$productName removed. The game installation and community mod files were not changed."
}
