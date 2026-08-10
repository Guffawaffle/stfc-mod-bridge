[CmdletBinding()]
param(
  [string]$OutputDirectory = "artifacts/win-x64",
  [Parameter(Mandatory)]
  [string]$ExpectedSourceRevisionId,
  [switch]$AllowDisposableMsixInstall
)

$ErrorActionPreference = "Stop"
$expectedPackageIdentity = "Guffawaffle.STFCModBridge"
$qualificationArgument = "--battle-ipc-package-qualification"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$outputRoot = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
  [System.IO.Path]::GetFullPath($OutputDirectory)
} else {
  [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
}
$launcher = Join-Path $outputRoot "app\STFCModBridge.exe"
$package = Join-Path $outputRoot "package\STFCModBridge.msix"

if (-not $IsWindows) {
  throw "Battle named-pipe package qualification requires Windows."
}
if (-not $AllowDisposableMsixInstall -and $env:CI -ne "true") {
  throw "This gate installs and removes a disposable MSIX. Pass -AllowDisposableMsixInstall outside CI."
}
if (-not (Test-Path -LiteralPath $launcher -PathType Leaf) `
    -or -not (Test-Path -LiteralPath $package -PathType Leaf)) {
  throw "The signed standalone launcher or MSIX package is missing."
}

& (Join-Path $PSScriptRoot "inspect-package.ps1") `
  -OutputDirectory $outputRoot `
  -RequireSignatures `
  -ExpectedSourceRevisionId $ExpectedSourceRevisionId | Out-Host

function Invoke-QualificationProcess {
  param(
    [Parameter(Mandatory)]
    [string]$Path,
    [Parameter(Mandatory)]
    [string]$Mode
  )

  $startInfo = [System.Diagnostics.ProcessStartInfo]::new($Path)
  $startInfo.UseShellExecute = $false
  $startInfo.CreateNoWindow = $true
  $startInfo.ArgumentList.Add($qualificationArgument)
  $startInfo.ArgumentList.Add($Mode)
  $process = [System.Diagnostics.Process]::Start($startInfo)
  if ($null -eq $process) {
    throw "The $Mode Battle IPC qualification process did not start."
  }
  try {
    if (-not $process.WaitForExit(30000)) {
      $process.Kill($true)
      if (-not $process.WaitForExit(10000)) {
        throw "The $Mode Battle IPC qualification did not terminate after forced stop."
      }
      throw "The $Mode Battle IPC qualification exceeded 30 seconds."
    }
    if ($process.ExitCode -ne 0) {
      throw "The $Mode Battle IPC qualification failed with exit code $($process.ExitCode)."
    }
  } finally {
    $process.Dispose()
  }
}

Invoke-QualificationProcess -Path $launcher -Mode "standalone"

$existing = @(Get-AppxPackage -Name $expectedPackageIdentity -ErrorAction Stop)
if ($existing.Count -ne 0) {
  throw "Battle IPC qualification refuses to replace an existing STFC Mod Bridge package."
}

if (-not ("BattlePackageActivation.ApplicationActivation" -as [type])) {
  Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace BattlePackageActivation
{
    [Flags]
    internal enum ActivateOptions
    {
        None = 0,
    }

    [ComImport]
    [Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IApplicationActivationManager
    {
        int ActivateApplication(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments,
            ActivateOptions options,
            out uint processId);

        int ActivateForFile(IntPtr appUserModelId, IntPtr itemArray, IntPtr verb, out uint processId);
        int ActivateForProtocol(IntPtr appUserModelId, IntPtr itemArray, out uint processId);
    }

    [ComImport]
    [Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    internal class ApplicationActivationManagerClass
    {
    }

    public static class ApplicationActivation
    {
        public static uint Activate(string appUserModelId, string arguments)
        {
            var manager = (IApplicationActivationManager)new ApplicationActivationManagerClass();
            var result = manager.ActivateApplication(
                appUserModelId,
                arguments,
                ActivateOptions.None,
                out var processId);
            Marshal.ThrowExceptionForHR(result);
            return processId;
        }
    }
}
'@
}

$installed = $null
$registrationAttempted = $false
try {
  $registrationAttempted = $true
  Add-AppxPackage -Path $package -ErrorAction Stop
  $packages = @(Get-AppxPackage -Name $expectedPackageIdentity -ErrorAction Stop)
  if ($packages.Count -ne 1) {
    throw "The disposable MSIX did not register exactly one reviewed package identity."
  }
  $installed = $packages[0]
  $appUserModelId = "$($installed.PackageFamilyName)!App"
  $processId = [BattlePackageActivation.ApplicationActivation]::Activate(
    $appUserModelId,
    "$qualificationArgument msix")
  $process = [System.Diagnostics.Process]::GetProcessById([int]$processId)
  try {
    if (-not $process.WaitForExit(30000)) {
      $process.Kill($true)
      if (-not $process.WaitForExit(10000)) {
        throw "The MSIX Battle IPC qualification did not terminate after forced stop."
      }
      throw "The MSIX Battle IPC qualification exceeded 30 seconds."
    }
    if ($process.ExitCode -ne 0) {
      throw "The MSIX Battle IPC qualification failed with exit code $($process.ExitCode)."
    }
  } finally {
    $process.Dispose()
  }
} finally {
  if ($null -eq $installed -and $registrationAttempted) {
    $registeredAfterFailure = @(Get-AppxPackage -Name $expectedPackageIdentity -ErrorAction Stop)
    if ($registeredAfterFailure.Count -eq 1) {
      $installed = $registeredAfterFailure[0]
    } elseif ($registeredAfterFailure.Count -gt 1) {
      throw "Disposable MSIX cleanup cannot identify one exact installed package."
    }
  }
  if ($null -ne $installed) {
    Remove-AppxPackage -Package $installed.PackageFullName -ErrorAction Stop
  }
}

if (@(Get-AppxPackage -Name $expectedPackageIdentity -ErrorAction Stop).Count -ne 0) {
  throw "The disposable STFC Mod Bridge package remained installed after qualification."
}

Write-Host "Signed standalone and medium-integrity MSIX Battle named-pipe qualification passed."
