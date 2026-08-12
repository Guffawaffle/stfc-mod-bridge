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
$windowsPowerShell = Join-Path ([Environment]::SystemDirectory) "WindowsPowerShell\v1.0\powershell.exe"
$appxModule = Join-Path `
  ([Environment]::SystemDirectory) `
  "WindowsPowerShell\v1.0\Modules\Appx\Appx.psd1"

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
if (-not (Test-Path -LiteralPath $windowsPowerShell -PathType Leaf)) {
  throw "Windows PowerShell is required for disposable Appx registration."
}
if (-not (Test-Path -LiteralPath $appxModule -PathType Leaf)) {
  throw "The System32 Appx module is required for disposable package registration."
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

function Invoke-WindowsPowerShellAppxCommand {
  param(
    [Parameter(Mandatory)]
    [ValidateSet("query", "install", "remove")]
    [string]$Operation,
    [Parameter(Mandatory)]
    [string]$Command
  )

  $previousAppxModule = $env:STFC_BATTLE_QUALIFICATION_APPX_MODULE
  try {
    $env:STFC_BATTLE_QUALIFICATION_APPX_MODULE = $appxModule
    $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($Command))
    $output = @(& $windowsPowerShell `
        -NoLogo `
        -NoProfile `
        -NonInteractive `
        -OutputFormat Text `
        -EncodedCommand $encodedCommand 2>&1)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
      $diagnostic = @(
        $output |
          ForEach-Object { ([string]$_).Trim() } |
          Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
          Select-Object -Last 12
      ) -join " | "
      if ($diagnostic.Length -gt 2048) {
        $diagnostic = $diagnostic.Substring($diagnostic.Length - 2048)
      }
      if ([string]::IsNullOrWhiteSpace($diagnostic)) {
        $diagnostic = "No child diagnostic was returned."
      }
      throw "The Windows PowerShell Appx $Operation command failed with exit code $exitCode. $diagnostic"
    }
    return $output
  } finally {
    $env:STFC_BATTLE_QUALIFICATION_APPX_MODULE = $previousAppxModule
  }
}

function Get-DisposablePackages {
  $previousPackageName = $env:STFC_BATTLE_QUALIFICATION_PACKAGE_NAME
  try {
    $env:STFC_BATTLE_QUALIFICATION_PACKAGE_NAME = $expectedPackageIdentity
    $json = @(Invoke-WindowsPowerShellAppxCommand -Operation "query" -Command @'
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$WarningPreference = "SilentlyContinue"
$InformationPreference = "SilentlyContinue"
$VerbosePreference = "SilentlyContinue"
Import-Module $env:STFC_BATTLE_QUALIFICATION_APPX_MODULE -ErrorAction Stop
@(
  Get-AppxPackage -Name $env:STFC_BATTLE_QUALIFICATION_PACKAGE_NAME -ErrorAction Stop |
    ForEach-Object {
      [pscustomobject]@{
        PackageFullName = $_.PackageFullName
        PackageFamilyName = $_.PackageFamilyName
      }
    }
) | ConvertTo-Json -Compress
'@) -join [Environment]::NewLine
    if ([string]::IsNullOrWhiteSpace($json)) {
      return @()
    }
    return @($json | ConvertFrom-Json -ErrorAction Stop)
  } finally {
    $env:STFC_BATTLE_QUALIFICATION_PACKAGE_NAME = $previousPackageName
  }
}

function Install-DisposablePackage {
  $previousPackagePath = $env:STFC_BATTLE_QUALIFICATION_MSIX
  try {
    $env:STFC_BATTLE_QUALIFICATION_MSIX = $package
    Invoke-WindowsPowerShellAppxCommand -Operation "install" -Command @'
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$WarningPreference = "SilentlyContinue"
$InformationPreference = "SilentlyContinue"
$VerbosePreference = "SilentlyContinue"
Import-Module $env:STFC_BATTLE_QUALIFICATION_APPX_MODULE -ErrorAction Stop
Add-AppxPackage -Path $env:STFC_BATTLE_QUALIFICATION_MSIX -ErrorAction Stop
'@ | Out-Null
  } finally {
    $env:STFC_BATTLE_QUALIFICATION_MSIX = $previousPackagePath
  }
}

function Remove-DisposablePackage {
  param(
    [Parameter(Mandatory)]
    [string]$PackageFullName
  )

  $previousPackageFullName = $env:STFC_BATTLE_QUALIFICATION_PACKAGE_FULL_NAME
  try {
    $env:STFC_BATTLE_QUALIFICATION_PACKAGE_FULL_NAME = $PackageFullName
    Invoke-WindowsPowerShellAppxCommand -Operation "remove" -Command @'
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$WarningPreference = "SilentlyContinue"
$InformationPreference = "SilentlyContinue"
$VerbosePreference = "SilentlyContinue"
Import-Module $env:STFC_BATTLE_QUALIFICATION_APPX_MODULE -ErrorAction Stop
Remove-AppxPackage -Package $env:STFC_BATTLE_QUALIFICATION_PACKAGE_FULL_NAME -ErrorAction Stop
'@ | Out-Null
  } finally {
    $env:STFC_BATTLE_QUALIFICATION_PACKAGE_FULL_NAME = $previousPackageFullName
  }
}

Invoke-QualificationProcess -Path $launcher -Mode "standalone"

$existing = @(Get-DisposablePackages)
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
  Install-DisposablePackage
  $packages = @(Get-DisposablePackages)
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
    $registeredAfterFailure = @(Get-DisposablePackages)
    if ($registeredAfterFailure.Count -eq 1) {
      $installed = $registeredAfterFailure[0]
    } elseif ($registeredAfterFailure.Count -gt 1) {
      throw "Disposable MSIX cleanup cannot identify one exact installed package."
    }
  }
  if ($null -ne $installed) {
    Remove-DisposablePackage -PackageFullName $installed.PackageFullName
  }
}

if (@(Get-DisposablePackages).Count -ne 0) {
  throw "The disposable STFC Mod Bridge package remained installed after qualification."
}

Write-Host "Signed standalone and medium-integrity MSIX Battle named-pipe qualification passed."
