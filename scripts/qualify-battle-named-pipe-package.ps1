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
$profilesQualificationArgument = "--profiles-package-qualification"
$profilesEvidenceSchema = "stfc.mod-bridge.profiles-package-qualification.v1"
$stateEvidenceSchema = "stfc.mod-bridge.package-state-qualification.v1"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$outputRoot = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
  [System.IO.Path]::GetFullPath($OutputDirectory)
} else {
  [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
}
$launcher = Join-Path $outputRoot "app\STFCModBridge.exe"
$canonicalPackage = Join-Path $outputRoot "package\STFCModBridge.msix"
$canonicalAppInstaller = Join-Path $outputRoot "package\STFCModBridge.appinstaller"
$package = $canonicalPackage
$appInstallerHostScript = Join-Path $PSScriptRoot "serve-appinstaller.py"
$python = Get-Command py.exe, python.exe -ErrorAction SilentlyContinue | Select-Object -First 1
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
    -or -not (Test-Path -LiteralPath $canonicalPackage -PathType Leaf) `
    -or -not (Test-Path -LiteralPath $canonicalAppInstaller -PathType Leaf)) {
  throw "The signed standalone launcher, MSIX package, or App Installer descriptor is missing."
}
if (-not (Test-Path -LiteralPath $windowsPowerShell -PathType Leaf)) {
  throw "Windows PowerShell is required for disposable Appx registration."
}
if (-not (Test-Path -LiteralPath $appxModule -PathType Leaf)) {
  throw "The System32 Appx module is required for disposable package registration."
}
if (-not $python -or -not (Test-Path -LiteralPath $appInstallerHostScript -PathType Leaf)) {
  throw "Python and scripts/serve-appinstaller.py are required for disposable App Installer qualification."
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

function Assert-ExternalProfilesFixture {
  # This control script runs unpackaged; these are Windows known-folder facts, not environment variables.
  $physicalAppData = @(
    [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData),
    [Environment]::GetFolderPath([Environment+SpecialFolder]::ApplicationData)
  )
  $localParent = [System.IO.Path]::GetDirectoryName($physicalAppData[0])
  $roamingParent = [System.IO.Path]::GetDirectoryName($physicalAppData[1])
  if ($localParent -ieq $roamingParent) { $physicalAppData += $localParent }
  foreach ($root in $physicalAppData) {
    if ([string]::IsNullOrWhiteSpace($root) -or -not [System.IO.Path]::IsPathFullyQualified($root)) {
      throw "The physical OS-user AppData folder facts are unavailable."
    }
    $full = [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetFullPath($root))
    if ($profilesFixture -ieq $full -or $profilesFixture.StartsWith($full + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
      throw "Profiles package qualification refuses an output fixture under physical OS-user AppData; select a workspace artifact output directory."
    }
  }
  if ((Test-Path -LiteralPath $profilesFixture) -or [System.IO.Path]::GetDirectoryName($profilesFixture) -ine $outputRoot) {
    throw "Profiles package qualification requires a fresh nonce fixture directly under the canonical output root."
  }
}
function Assert-CanonicalProfilesPairing {
  $archivePath = Join-Path $outputRoot "stfc-mod-bridge-win-x64.zip"
  $nativePath = Join-Path $outputRoot "app\stfc-profiles-native.dll"
  $productVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($launcher).ProductVersion
  if ($productVersion -cnotmatch '\+commit\.(?<source>[0-9a-f]{40})\.verifier\.[0-9a-f]{64}\.profiles\.(?<native>[0-9a-f]{64})$' `
      -or $Matches.source -cne $ExpectedSourceRevisionId) {
    throw "The standalone Profiles qualification host is not bound to the exact candidate source."
  }
  $nativeSha256 = (Get-FileHash -LiteralPath $nativePath -Algorithm SHA256).Hash.ToLowerInvariant()
  if ($nativeSha256 -cne $Matches.native) {
    throw "The standalone Profiles qualification host is not paired to its exact native DLL."
  }
  $launcherSha256 = (Get-FileHash -LiteralPath $launcher -Algorithm SHA256).Hash.ToLowerInvariant()
  foreach ($artifactPath in @($archivePath, $canonicalPackage)) {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($artifactPath)
    try {
      foreach ($expected in @(
          [pscustomobject]@{ Name = "STFCModBridge.exe"; Sha256 = $launcherSha256 },
          [pscustomobject]@{ Name = "stfc-profiles-native.dll"; Sha256 = $nativeSha256 })) {
        $entries = @($zip.Entries | Where-Object { $_.FullName -ceq $expected.Name })
        if ($entries.Count -ne 1) {
          throw "The canonical archive/package does not contain exactly one $($expected.Name)."
        }
        $stream = $entries[0].Open()
        try {
          $actual = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()
        } finally {
          $stream.Dispose()
        }
        if ($actual -cne $expected.Sha256) {
          throw "The canonical ZIP, MSIX and standalone app have different $($expected.Name) bytes."
        }
      }
    } finally {
      $zip.Dispose()
    }
  }
  return $productVersion
}

function Invoke-ProfilesQualificationProcess {
  param([Parameter(Mandatory)][ValidateSet("prepare", "verify", "cleanup")][string]$Mode)
  $started = [DateTimeOffset]::UtcNow
  $startInfo = [System.Diagnostics.ProcessStartInfo]::new($launcher)
  $startInfo.UseShellExecute = $false
  $startInfo.CreateNoWindow = $true
  $startInfo.RedirectStandardOutput = $true
  $startInfo.RedirectStandardError = $true
  foreach ($argument in @($profilesQualificationArgument, $Mode, $profilesNonce, $profilesFixture)) {
    $startInfo.ArgumentList.Add($argument)
  }
  $process = [System.Diagnostics.Process]::Start($startInfo)
  if ($null -eq $process) { throw "Profiles $Mode did not start." }
  try {
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(30000)) {
      $process.Kill($true)
      if (-not $process.WaitForExit(10000)) { throw "Profiles $Mode did not stop after its timeout." }
      throw "Profiles $Mode exceeded 30 seconds; receipt and fixture remain at $profilesFixture."
    }
    $diagnostic = (($stdout.GetAwaiter().GetResult() + "`n" + $stderr.GetAwaiter().GetResult()).Trim())
    if ($diagnostic.Length -gt 2048) { $diagnostic = $diagnostic.Substring($diagnostic.Length - 2048) }
    Write-Host "Profiles $Mode exit=$($process.ExitCode) duration=$([math]::Round(([DateTimeOffset]::UtcNow - $started).TotalSeconds, 2))s source=$ExpectedSourceRevisionId cwd=$repoRoot"
    if ($process.ExitCode -ne 0) {
      $failureFiles = @(Get-ChildItem -LiteralPath $profilesFixture -Filter "failed-$Mode-*.json" -File -ErrorAction SilentlyContinue)
      foreach ($failureFile in $failureFiles) {
        $diagnostic += " | " + [System.IO.File]::ReadAllText($failureFile.FullName)
      }
      throw "Profiles $Mode failed. $diagnostic Receipt and fixture retained at $profilesFixture."
    }
  } finally {
    $process.Dispose()
  }
}

function Assert-ProfilesEvidence {
  param(
    [Parameter(Mandatory)][string]$FileName,
    [Parameter(Mandatory)][string]$Status,
    [Parameter(Mandatory)][string]$PackageFullName
  )
  $path = Join-Path $profilesFixture $FileName
  if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Profiles $Status marker is missing at $path." }
  $receipt = Get-Content -Raw -LiteralPath (Join-Path $profilesFixture "receipt.json") | ConvertFrom-Json -ErrorAction Stop
  $evidence = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json -ErrorAction Stop
  if ($receipt.schema -cne $profilesEvidenceSchema -or $receipt.nonce -cne $profilesNonce `
      -or $receipt.fixture -cne $profilesFixture -or $receipt.id -cnotmatch '^[0-9a-f]{32}$' `
      -or $receipt.name -cne "Bridge MSIX $profilesNonce" `
      -or $receipt.revision -cnotmatch '^[0-9a-f]{64}$' `
      -or $receipt.buildIdentity -cne $profilesBuildIdentity `
      -or $evidence.schema -cne $profilesEvidenceSchema -or $evidence.nonce -cne $profilesNonce `
      -or $evidence.id -cne $receipt.id -or $evidence.revision -cne $receipt.revision `
      -or $evidence.buildIdentity -cne $profilesBuildIdentity `
      -or $evidence.packageFullName -cne $PackageFullName -or $evidence.status -cne $Status `
      -or $null -ne $evidence.stage) {
    throw "Profiles $Status evidence is not bound to the exact candidate package, source, nonce and profile revision."
  }
}

function Open-ActivatedQualificationProcess {
  param([Parameter(Mandatory)][int]$ProcessId)
  $process = [System.Diagnostics.Process]::GetProcessById($ProcessId)
  try {
    # GetProcessById/WaitForExit alone do not retain a handle for ExitCode.
    # Open it while the activated fixture is alive and keep it through release verification.
    $handle = $process.get_SafeHandle()
    if ($null -eq $handle -or $handle.IsInvalid -or $handle.IsClosed) {
      throw "The activated qualification process has no usable retained handle."
    }
    return $process
  } catch {
    $process.Dispose()
    throw
  }
}

function Invoke-PackagedProfilesQualification {
  param(
    [Parameter(Mandatory)][string]$AppUserModelId,
    [Parameter(Mandatory)][string]$PackageFullName
  )
  $started = [DateTimeOffset]::UtcNow
  $arguments = "$profilesQualificationArgument msix $profilesNonce `"$profilesFixture`""
  $processId = [BattlePackageActivation.ApplicationActivation]::Activate($AppUserModelId, $arguments)
  $process = Open-ActivatedQualificationProcess -ProcessId ([int]$processId)
  try {
    $readyPath = Join-Path $profilesFixture "msix-ready.json"
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
    while (-not (Test-Path -LiteralPath $readyPath -PathType Leaf)) {
      if ($process.HasExited -or [DateTimeOffset]::UtcNow -ge $deadline) {
        $failureFiles = @(Get-ChildItem -LiteralPath $profilesFixture -Filter "failed-msix-*.json" -File)
        $diagnostic = @($failureFiles | ForEach-Object { [System.IO.File]::ReadAllText($_.FullName) }) -join " | "
        throw "Packaged Profiles never acquired both native leases. $diagnostic Receipt retained at $profilesFixture."
      }
      Start-Sleep -Milliseconds 50
    }
    Assert-ProfilesEvidence -FileName "msix-ready.json" -Status "ready" -PackageFullName $PackageFullName
    Invoke-ProfilesQualificationProcess -Mode "verify"
    Assert-ProfilesEvidence -FileName "standalone-verified.json" -Status "verified" -PackageFullName $PackageFullName
    if (-not $process.WaitForExit(30000)) {
      throw "Packaged Profiles did not exit after standalone verification."
    }
    # Explicit getter calls propagate errors that PowerShell property access can hide as null.
    $exitCode = $process.get_ExitCode()
    if ($exitCode -ne 0) { throw "Packaged Profiles exited with code $exitCode." }
    Assert-ProfilesEvidence -FileName "msix-released.json" -Status "passed" -PackageFullName $PackageFullName
    Write-Host "Profiles msix exit=0 duration=$([math]::Round(([DateTimeOffset]::UtcNow - $started).TotalSeconds, 2))s source=$ExpectedSourceRevisionId package=$PackageFullName"
  } finally {
    try {
      if (-not $process.HasExited) {
        $process.Kill($true)
        if (-not $process.WaitForExit(10000)) { throw "Packaged Profiles failed to terminate; do not clean up its profile." }
      }
    } finally {
      $process.Dispose()
    }
  }
}

function Invoke-WindowsPowerShellCommand {
  param(
    [Parameter(Mandatory)]
    [ValidateSet("query", "settings", "install", "remove", "certificate-create", "certificate-remove")]
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

function Start-DisposableAppInstallerHost {
  $hostRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    "stfc-mod-bridge-appinstaller-qualification-" + [Guid]::NewGuid().ToString("N"))
  $hostPackage = Join-Path $hostRoot "STFCModBridge.msix"
  $hostDescriptor = Join-Path $hostRoot "STFCModBridge.appinstaller"
  $listener = [System.Net.Sockets.TcpListener]::new(
    [System.Net.IPAddress]::Loopback,
    0)
  $server = $null
  New-Item -ItemType Directory -Path $hostRoot | Out-Null
  try {
    Copy-Item -LiteralPath $package -Destination $hostPackage
    $listener.Start()
    $port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    $listener.Stop()

    [xml]$descriptor = Get-Content -Raw -LiteralPath $canonicalAppInstaller
    $baseUri = "http://127.0.0.1:$port"
    $namespaceUri = $descriptor.DocumentElement.NamespaceURI
    if ([string]::IsNullOrWhiteSpace($namespaceUri)) {
      throw "The canonical App Installer descriptor has no namespace."
    }
    $namespaceManager = [System.Xml.XmlNamespaceManager]::new($descriptor.NameTable)
    $namespaceManager.AddNamespace("ai", $namespaceUri)
    $appInstallerElement = $descriptor.SelectSingleNode("/ai:AppInstaller", $namespaceManager)
    $mainPackageElement = $descriptor.SelectSingleNode("/ai:AppInstaller/ai:MainPackage", $namespaceManager)
    if ($null -eq $appInstallerElement -or $null -eq $mainPackageElement) {
      throw "The canonical App Installer descriptor is missing AppInstaller or MainPackage."
    }
    $appInstallerElement.SetAttribute("Uri", "$baseUri/STFCModBridge.appinstaller")
    $mainPackageElement.SetAttribute("Uri", "$baseUri/STFCModBridge.msix")
    $descriptor.Save($hostDescriptor)

    $pythonArguments = if ([System.IO.Path]::GetFileName($python.Source) -ieq "py.exe") {
      @("-3", "`"$appInstallerHostScript`"", "$port", "`"$hostRoot`"")
    } else {
      @("`"$appInstallerHostScript`"", "$port", "`"$hostRoot`"")
    }
    $server = Start-Process `
      -FilePath $python.Source `
      -ArgumentList $pythonArguments `
      -WindowStyle Hidden `
      -PassThru

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
    do {
      try {
        $response = Invoke-WebRequest `
          -Uri "$baseUri/STFCModBridge.appinstaller" `
          -Method Head `
          -UseBasicParsing
      } catch {
        if ($server.HasExited -or [DateTimeOffset]::UtcNow -ge $deadline) {
          throw
        }
        Start-Sleep -Milliseconds 200
      }
    } until ($response.StatusCode -eq 200)

    return [pscustomobject]@{
      DescriptorPath = $hostDescriptor
      Process = $server
      Root = $hostRoot
    }
  } catch {
    if ($null -ne $server -and -not $server.HasExited) {
      $server.Kill($true)
      [void]$server.WaitForExit(10000)
    }
    if ($null -ne $server) {
      $server.Dispose()
    }
    if (Test-Path -LiteralPath $hostRoot) {
      Remove-Item -LiteralPath $hostRoot -Recurse -Force
    }
    throw
  } finally {
    $listener.Dispose()
  }
}

function Get-DisposablePackageUpdateSettings {
  param(
    [Parameter(Mandatory)]
    [string]$PackageFamilyName
  )

  $previousPackageFamilyName = $env:STFC_BATTLE_QUALIFICATION_PACKAGE_FAMILY_NAME
  try {
    $env:STFC_BATTLE_QUALIFICATION_PACKAGE_FAMILY_NAME = $PackageFamilyName
    $json = @(Invoke-WindowsPowerShellCommand -Operation "settings" -Command @'
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$WarningPreference = "SilentlyContinue"
$InformationPreference = "SilentlyContinue"
$VerbosePreference = "SilentlyContinue"
Import-Module $env:STFC_BATTLE_QUALIFICATION_APPX_MODULE -ErrorAction Stop
if (-not (Get-Command Get-AppxPackageAutoUpdateSettings -ErrorAction SilentlyContinue)) {
  [pscustomobject]@{
    Available = $false
  } | ConvertTo-Json -Compress
  return
}
$settings = Get-AppxPackageAutoUpdateSettings `
  -PackageFamilyName $env:STFC_BATTLE_QUALIFICATION_PACKAGE_FAMILY_NAME `
  -ErrorAction Stop
[pscustomobject]@{
  Available = $true
  CheckForUpdatesOnLaunch = $settings.CheckForUpdatesOnLaunch
  HoursBetweenUpdateChecks = $settings.HoursBetweenUpdateChecks
  AutomaticBackgroundTaskUpdatesEnabled = $settings.AutomaticBackgroundTaskUpdatesEnabled
  ShowPromptOnLaunchWhenUpdateIsAvailable = $settings.ShowPromptOnLaunchWhenUpdateIsAvailable
  UpdateBlocksActivation = $settings.UpdateBlocksActivation
} | ConvertTo-Json -Compress
'@) -join [Environment]::NewLine
    return $json | ConvertFrom-Json -ErrorAction Stop
  } finally {
    $env:STFC_BATTLE_QUALIFICATION_PACKAGE_FAMILY_NAME = $previousPackageFamilyName
  }
}

function Install-DisposablePackage {
  param(
    [Parameter(Mandatory)]
    [string]$AppInstallerPath
  )

  $previousAppInstallerPath = $env:STFC_BATTLE_QUALIFICATION_APPINSTALLER
  try {
    $env:STFC_BATTLE_QUALIFICATION_APPINSTALLER = $AppInstallerPath
    Invoke-WindowsPowerShellCommand -Operation "install" -Command @'
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$WarningPreference = "SilentlyContinue"
$InformationPreference = "SilentlyContinue"
$VerbosePreference = "SilentlyContinue"
Import-Module $env:STFC_BATTLE_QUALIFICATION_APPX_MODULE -ErrorAction Stop
Add-AppxPackage `
  -Path $env:STFC_BATTLE_QUALIFICATION_APPINSTALLER `
  -AppInstallerFile `
  -ErrorAction Stop
'@ | Out-Null
  } finally {
    $env:STFC_BATTLE_QUALIFICATION_APPINSTALLER = $previousAppInstallerPath
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
$appInstallerHost = $null
$effectiveUpdateSettingsVerified = $false
$profilesNonce = [Guid]::NewGuid().ToString("N")
$profilesFixture = Join-Path $outputRoot "stfc-mod-bridge-profiles-qualification-$profilesNonce"
$profilesBuildIdentity = $null
$profilesPrepared = $false
$profilesCleanupPassed = $false
$stateEvidenceNonce = [Guid]::NewGuid().ToString("N")
$stateEvidencePath = Join-Path `
  ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) `
  "STFC Mod Bridge\package-qualification-$stateEvidenceNonce.json"
try {
  $existing = @(Get-DisposablePackages)
  if ($existing.Count -ne 0) {
    throw "Battle IPC qualification refuses to replace an existing STFC Mod Bridge package."
  }

  Assert-ExternalProfilesFixture
  $profilesBuildIdentity = Assert-CanonicalProfilesPairing
  Invoke-QualificationProcess -Path $launcher -Mode "standalone"
  Invoke-ProfilesQualificationProcess -Mode "prepare"
  $profilesPrepared = $true

  if ($UseDisposableDevelopmentCertificate) {
    $developmentPackage = New-DisposableDevelopmentPackage
    $package = $developmentPackage.PackagePath
  }
  $appInstallerHost = Start-DisposableAppInstallerHost

  $installed = $null
  $registrationAttempted = $false
  try {
    $registrationAttempted = $true
    Install-DisposablePackage -AppInstallerPath $appInstallerHost.DescriptorPath
    $packages = @(Get-DisposablePackages)
    if ($packages.Count -ne 1) {
      throw "The disposable MSIX did not register exactly one reviewed package identity."
    }
    $installed = $packages[0]
    $updateSettings = Get-DisposablePackageUpdateSettings `
      -PackageFamilyName $installed.PackageFamilyName
    if ($updateSettings.Available) {
      if ($updateSettings.CheckForUpdatesOnLaunch -ne $false `
          -or [int]$updateSettings.HoursBetweenUpdateChecks -ne 24 `
          -or $updateSettings.AutomaticBackgroundTaskUpdatesEnabled -ne $false `
          -or $updateSettings.ShowPromptOnLaunchWhenUpdateIsAvailable -ne $false `
          -or $updateSettings.UpdateBlocksActivation -ne $false) {
        throw "The disposable App Installer association did not record the reviewed False / 24 / False update defaults."
      }
      $effectiveUpdateSettingsVerified = $true
    } else {
      Write-Host "This Windows host predates the supported App Installer settings readback; descriptor inspection and normal package activation remain mandatory."
    }
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
    if ($stateEvidence.status -cne "passed" -or $null -ne $stateEvidence.stage) {
      $failedStage = if ([string]::IsNullOrWhiteSpace([string]$stateEvidence.stage)) {
        "unknown"
      } else {
        [string]$stateEvidence.stage
      }
      throw "The packaged Bridge qualification reported failure at $failedStage."
    }
    Invoke-PackagedProfilesQualification `
      -AppUserModelId $appUserModelId `
      -PackageFullName $installed.PackageFullName
    Invoke-ProfilesQualificationProcess -Mode "cleanup"
    $profilesCleanupPassed = $true
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
  $updateSettingsEvidence = if ($effectiveUpdateSettingsVerified) {
    "App Installer False / 24 / False policy"
  } else {
    "App Installer association and uninterrupted normal package activation"
  }
  Write-Host "$qualificationKind standalone, $updateSettingsEvidence, medium-integrity MSIX Battle named-pipe, external-state, and shared Profiles catalog/lease qualification passed."
} finally {
  if ($profilesPrepared -or (Test-Path -LiteralPath $profilesFixture)) {
    if ($profilesCleanupPassed) {
      Write-Host "Synthetic Profiles account deleted through the native API; qualification evidence retained at $profilesFixture."
    } else {
      Write-Warning "Profiles qualification did not complete cleanup. Receipt, synthetic profile and evidence retained for inspection at $profilesFixture. No account data was broadly deleted."
    }
  }
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
    if ($null -ne $appInstallerHost) {
      try {
        if (-not $appInstallerHost.Process.HasExited) {
          $appInstallerHost.Process.Kill($true)
          if (-not $appInstallerHost.Process.WaitForExit(10000)) {
            throw "The disposable App Installer host did not terminate after forced stop."
          }
        }
      } finally {
        $appInstallerHost.Process.Dispose()
        if (Test-Path -LiteralPath $appInstallerHost.Root) {
          Remove-Item -LiteralPath $appInstallerHost.Root -Recurse -Force
        }
      }
    }
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
