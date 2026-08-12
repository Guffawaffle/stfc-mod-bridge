[CmdletBinding()]
param(
  [string]$OutputDirectory = "artifacts/win-x64",
  [Parameter(Mandatory)]
  [string]$ExpectedSourceRevisionId,
  [switch]$AllowDisposableMsixInstall,
  [switch]$UseDisposableDevelopmentCertificate
)

$ErrorActionPreference = "Stop"
$expectedPackageIdentity = "Guffawaffle.STFCModBridge"
$expectedPublisherSubject = "CN=Joseph Gustavson, O=Joseph Gustavson, L=Dousman, S=Wisconsin, C=US, PostalCode=53118"
$qualificationArgument = "--battle-ipc-package-qualification"
$stateEvidenceSchema = "stfc.mod-bridge.package-state-qualification.v1"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$outputRoot = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
  [System.IO.Path]::GetFullPath($OutputDirectory)
} else {
  [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
}
$launcher = Join-Path $outputRoot "app\STFCModBridge.exe"
$canonicalPackage = Join-Path $outputRoot "package\STFCModBridge.msix"
$package = $canonicalPackage
$windowsPowerShell = Join-Path ([Environment]::SystemDirectory) "WindowsPowerShell\v1.0\powershell.exe"
$appxModule = Join-Path `
  ([Environment]::SystemDirectory) `
  "WindowsPowerShell\v1.0\Modules\Appx\Appx.psd1"
$kitsBin = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
$signTool = Get-Command signtool.exe -ErrorAction SilentlyContinue
$signTool = if ($signTool) {
  $signTool.Source
} else {
  Get-ChildItem -LiteralPath $kitsBin -Directory -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending |
    ForEach-Object { Join-Path $_.FullName "x64\signtool.exe" } |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
    Select-Object -First 1
}

if (-not $IsWindows) {
  throw "Battle named-pipe package qualification requires Windows."
}
if (-not $AllowDisposableMsixInstall -and $env:CI -ne "true") {
  throw "This gate installs and removes a disposable MSIX. Pass -AllowDisposableMsixInstall outside CI."
}
if (-not (Test-Path -LiteralPath $launcher -PathType Leaf) `
    -or -not (Test-Path -LiteralPath $canonicalPackage -PathType Leaf)) {
  throw "The signed standalone launcher or MSIX package is missing."
}
if (-not (Test-Path -LiteralPath $windowsPowerShell -PathType Leaf)) {
  throw "Windows PowerShell is required for disposable Appx registration."
}
if (-not (Test-Path -LiteralPath $appxModule -PathType Leaf)) {
  throw "The System32 Appx module is required for disposable package registration."
}
if ($UseDisposableDevelopmentCertificate -and -not $signTool) {
  throw "Windows SDK SignTool is required for disposable development package signing."
}
if ($UseDisposableDevelopmentCertificate) {
  $principal = [Security.Principal.WindowsPrincipal]::new(
    [Security.Principal.WindowsIdentity]::GetCurrent())
  if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Disposable MSIX development signing requires an elevated runner to trust the test certificate in LocalMachine TrustedPeople."
  }
}

$inspectionArguments = @{
  OutputDirectory = $outputRoot
  ExpectedSourceRevisionId = $ExpectedSourceRevisionId
}
if (-not $UseDisposableDevelopmentCertificate) {
  $inspectionArguments.RequireSignatures = $true
}
& (Join-Path $PSScriptRoot "inspect-package.ps1") @inspectionArguments | Out-Host

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

function Invoke-WindowsPowerShellCommand {
  param(
    [Parameter(Mandatory)]
    [ValidateSet("query", "install", "remove", "certificate-create", "certificate-remove")]
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
      throw "The Windows PowerShell $Operation command failed with exit code $exitCode. $diagnostic"
    }
    return $output
  } finally {
    $env:STFC_BATTLE_QUALIFICATION_APPX_MODULE = $previousAppxModule
  }
}

function Remove-DisposableDevelopmentCertificate {
  param(
    [string]$Thumbprint,
    [string]$FriendlyName
  )

  if ([string]::IsNullOrWhiteSpace($Thumbprint) `
      -and [string]::IsNullOrWhiteSpace($FriendlyName)) {
    return
  }
  $previousThumbprint = $env:STFC_BATTLE_QUALIFICATION_CERTIFICATE_THUMBPRINT
  $previousFriendlyName = $env:STFC_BATTLE_QUALIFICATION_CERTIFICATE_FRIENDLY_NAME
  try {
    $env:STFC_BATTLE_QUALIFICATION_CERTIFICATE_THUMBPRINT = $Thumbprint
    $env:STFC_BATTLE_QUALIFICATION_CERTIFICATE_FRIENDLY_NAME = $FriendlyName
    Invoke-WindowsPowerShellCommand -Operation "certificate-remove" -Command @'
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$WarningPreference = "SilentlyContinue"
$InformationPreference = "SilentlyContinue"
$VerbosePreference = "SilentlyContinue"
$thumbprint = $env:STFC_BATTLE_QUALIFICATION_CERTIFICATE_THUMBPRINT
if ([string]::IsNullOrWhiteSpace($thumbprint)) {
  $candidates = @(Get-ChildItem -LiteralPath "Cert:\CurrentUser\My" | Where-Object {
      $_.FriendlyName -ceq $env:STFC_BATTLE_QUALIFICATION_CERTIFICATE_FRIENDLY_NAME
    })
  if ($candidates.Count -gt 1) {
    throw "Disposable certificate cleanup found more than one exact friendly name."
  }
  if ($candidates.Count -eq 0) {
    return
  }
  $thumbprint = $candidates[0].Thumbprint
}
$trustedPath = "Cert:\LocalMachine\TrustedPeople\$thumbprint"
$personalPath = "Cert:\CurrentUser\My\$thumbprint"
if (Test-Path -LiteralPath $trustedPath) {
  Remove-Item -LiteralPath $trustedPath -Force
}
if (Test-Path -LiteralPath $personalPath) {
  Remove-Item -LiteralPath $personalPath -DeleteKey -Force
}
if ((Test-Path -LiteralPath $trustedPath) -or (Test-Path -LiteralPath $personalPath)) {
  throw "The disposable development certificate remained installed after cleanup."
}
'@ | Out-Null
  } finally {
    $env:STFC_BATTLE_QUALIFICATION_CERTIFICATE_THUMBPRINT = $previousThumbprint
    $env:STFC_BATTLE_QUALIFICATION_CERTIFICATE_FRIENDLY_NAME = $previousFriendlyName
  }
}

function New-DisposableDevelopmentPackage {
  $qualificationRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    "stfc-mod-bridge-msix-qualification-" + [Guid]::NewGuid().ToString("N"))
  $qualifiedPackage = Join-Path $qualificationRoot "STFCModBridge.qualification.msix"
  $friendlyName = "STFC Mod Bridge disposable qualification " + [Guid]::NewGuid().ToString("N")
  $thumbprint = ""
  New-Item -ItemType Directory -Path $qualificationRoot | Out-Null
  try {
    Copy-Item -LiteralPath $canonicalPackage -Destination $qualifiedPackage
    $previousPublisher = $env:STFC_BATTLE_QUALIFICATION_CERTIFICATE_SUBJECT
    $previousFriendlyName = $env:STFC_BATTLE_QUALIFICATION_CERTIFICATE_FRIENDLY_NAME
    try {
      $env:STFC_BATTLE_QUALIFICATION_CERTIFICATE_SUBJECT = $expectedPublisherSubject
      $env:STFC_BATTLE_QUALIFICATION_CERTIFICATE_FRIENDLY_NAME = $friendlyName
      $certificateOutput = @(Invoke-WindowsPowerShellCommand -Operation "certificate-create" -Command @'
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$WarningPreference = "SilentlyContinue"
$InformationPreference = "SilentlyContinue"
$VerbosePreference = "SilentlyContinue"
Import-Module PKI -ErrorAction Stop
$certificate = $null
try {
  $certificate = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject $env:STFC_BATTLE_QUALIFICATION_CERTIFICATE_SUBJECT `
    -FriendlyName $env:STFC_BATTLE_QUALIFICATION_CERTIFICATE_FRIENDLY_NAME `
    -KeyAlgorithm RSA `
    -KeyLength 3072 `
    -HashAlgorithm SHA256 `
    -KeyExportPolicy NonExportable `
    -NotAfter (Get-Date).AddDays(1) `
    -CertStoreLocation "Cert:\CurrentUser\My"
  if ($certificate.Subject -cne $env:STFC_BATTLE_QUALIFICATION_CERTIFICATE_SUBJECT) {
    throw "The disposable certificate subject does not match the package publisher."
  }
  $publicCertificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
    $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
  $trustedPeople = [System.Security.Cryptography.X509Certificates.X509Store]::new(
    "TrustedPeople",
    [System.Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)
  try {
    $trustedPeople.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
    $trustedPeople.Add($publicCertificate)
  } finally {
    $trustedPeople.Dispose()
    $publicCertificate.Dispose()
  }
  "THUMBPRINT=$($certificate.Thumbprint)"
} catch {
  if ($null -ne $certificate) {
    $trustedPath = "Cert:\LocalMachine\TrustedPeople\$($certificate.Thumbprint)"
    $personalPath = "Cert:\CurrentUser\My\$($certificate.Thumbprint)"
    if (Test-Path -LiteralPath $trustedPath) {
      Remove-Item -LiteralPath $trustedPath -Force
    }
    if (Test-Path -LiteralPath $personalPath) {
      Remove-Item -LiteralPath $personalPath -DeleteKey -Force
    }
  }
  throw
}
'@)
      $thumbprintLine = @($certificateOutput | ForEach-Object { ([string]$_).Trim() } | Where-Object {
          $_ -cmatch '^THUMBPRINT=[0-9A-F]{40}$'
        }) | Select-Object -Last 1
      $thumbprint = ([string]$thumbprintLine).Substring("THUMBPRINT=".Length)
      if ([string]::IsNullOrWhiteSpace($thumbprint)) {
        throw "Windows PowerShell did not return the disposable certificate thumbprint."
      }
    } finally {
      $env:STFC_BATTLE_QUALIFICATION_CERTIFICATE_SUBJECT = $previousPublisher
      $env:STFC_BATTLE_QUALIFICATION_CERTIFICATE_FRIENDLY_NAME = $previousFriendlyName
    }

    & $signTool sign /fd SHA256 /sha1 $thumbprint /s My $qualifiedPackage | Out-Host
    if ($LASTEXITCODE -ne 0) {
      throw "SignTool could not sign the disposable MSIX with the development certificate."
    }
    & $signTool verify /pa /all /q $qualifiedPackage | Out-Host
    if ($LASTEXITCODE -ne 0) {
      throw "WinVerifyTrust rejected the disposable development-signed MSIX."
    }
    return [pscustomobject]@{
      CertificateThumbprint = $thumbprint
      CertificateFriendlyName = $friendlyName
      PackagePath = $qualifiedPackage
      QualificationRoot = $qualificationRoot
    }
  } catch {
    try {
      Remove-DisposableDevelopmentCertificate `
        -Thumbprint $thumbprint `
        -FriendlyName $friendlyName
    } finally {
      if (Test-Path -LiteralPath $qualificationRoot) {
        Remove-Item -LiteralPath $qualificationRoot -Recurse -Force
      }
    }
    throw
  }
}

function Get-DisposablePackages {
  $previousPackageName = $env:STFC_BATTLE_QUALIFICATION_PACKAGE_NAME
  try {
    $env:STFC_BATTLE_QUALIFICATION_PACKAGE_NAME = $expectedPackageIdentity
    $json = @(Invoke-WindowsPowerShellCommand -Operation "query" -Command @'
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
    Invoke-WindowsPowerShellCommand -Operation "install" -Command @'
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
    Invoke-WindowsPowerShellCommand -Operation "remove" -Command @'
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

$canonicalPackageSha256 = (Get-FileHash -LiteralPath $canonicalPackage -Algorithm SHA256).Hash
$developmentPackage = $null
$stateEvidenceNonce = [Guid]::NewGuid().ToString("N")
$stateEvidencePath = Join-Path `
  ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) `
  "STFC Mod Bridge\package-qualification-$stateEvidenceNonce.json"
try {
  $existing = @(Get-DisposablePackages)
  if ($existing.Count -ne 0) {
    throw "Battle IPC qualification refuses to replace an existing STFC Mod Bridge package."
  }

  Invoke-QualificationProcess -Path $launcher -Mode "standalone"

  if ($UseDisposableDevelopmentCertificate) {
    $developmentPackage = New-DisposableDevelopmentPackage
    $package = $developmentPackage.PackagePath
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
      "$qualificationArgument msix $stateEvidenceNonce")
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

    if (-not (Test-Path -LiteralPath $stateEvidencePath -PathType Leaf)) {
      throw "The unpackaged qualification host could not observe the packaged Bridge state evidence."
    }
    $stateEvidence = Get-Content -Raw -LiteralPath $stateEvidencePath | ConvertFrom-Json -ErrorAction Stop
    if ($stateEvidence.schema -cne $stateEvidenceSchema `
        -or $stateEvidence.nonce -cne $stateEvidenceNonce) {
      throw "The packaged Bridge external-state evidence is invalid."
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
  $qualificationKind = if ($UseDisposableDevelopmentCertificate) {
    "Disposable development-signed"
  } else {
    "Production-signed"
  }
  Write-Host "$qualificationKind standalone, medium-integrity MSIX Battle named-pipe, and external-state qualification passed."
} finally {
  if (Test-Path -LiteralPath $stateEvidencePath -PathType Leaf) {
    Remove-Item -LiteralPath $stateEvidencePath -Force
  }
  try {
    if ($null -ne $developmentPackage) {
      Remove-DisposableDevelopmentCertificate `
        -Thumbprint $developmentPackage.CertificateThumbprint `
        -FriendlyName $developmentPackage.CertificateFriendlyName
    }
  } finally {
    if ($null -ne $developmentPackage `
        -and (Test-Path -LiteralPath $developmentPackage.QualificationRoot)) {
      Remove-Item -LiteralPath $developmentPackage.QualificationRoot -Recurse -Force
    }
    $package = $canonicalPackage
    $actualCanonicalSha256 = (Get-FileHash -LiteralPath $canonicalPackage -Algorithm SHA256).Hash
    if ($actualCanonicalSha256 -cne $canonicalPackageSha256) {
      throw "Disposable qualification changed the canonical unsigned MSIX."
    }
  }
}
