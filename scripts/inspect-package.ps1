[CmdletBinding()]
param(
  [string]$OutputDirectory = "artifacts/win-x64",
  [switch]$RequireSignatures,
  [string]$ExpectedSourceRevisionId = "",
  [string]$ExpectedPublisherSubject = "CN=Joseph Gustavson, O=Joseph Gustavson, L=Dousman, S=Wisconsin, C=US, PostalCode=53118"
)

$ErrorActionPreference = "Stop"
$expectedPackageIdentity = "Guffawaffle.STFCModBridge"
$expectedPublisherName = [System.Security.Cryptography.X509Certificates.X500DistinguishedName]::new(
  $ExpectedPublisherSubject)
$expectedArtifactSigningIdentityEku = "1.3.6.1.4.1.311.97.664386437.910814316.510550690.722133748"
if ($ExpectedSourceRevisionId -and $ExpectedSourceRevisionId -cnotmatch '^[0-9a-f]{40}$') {
  throw "ExpectedSourceRevisionId must be exactly 40 lowercase hexadecimal characters."
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$outputRoot = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
  [System.IO.Path]::GetFullPath($OutputDirectory)
} else {
  [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
}
$archive = Join-Path $outputRoot "stfc-mod-bridge-win-x64.zip"
$packageDirectory = Join-Path $outputRoot "package"
$package = Join-Path $packageDirectory "STFCModBridge.msix"
$appInstaller = Join-Path $packageDirectory "STFCModBridge.appinstaller"

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
$makeAppx = Get-ChildItem -LiteralPath $kitsBin -Directory -ErrorAction SilentlyContinue |
  Sort-Object Name -Descending |
  ForEach-Object { Join-Path $_.FullName "x64\MakeAppx.exe" } |
  Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
  Select-Object -First 1
if (-not $makeAppx) {
  throw "Windows SDK MakeAppx.exe is required for MSIX inspection."
}

function Test-PortableExecutable {
  param([string]$Path)

  if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
    return $false
  }
  $stream = [System.IO.File]::OpenRead($Path)
  try {
    if ($stream.Length -lt 64 -or $stream.ReadByte() -ne 0x4d -or $stream.ReadByte() -ne 0x5a) {
      return $false
    }
    $stream.Position = 0x3c
    $offsetBytes = [byte[]]::new(4)
    if ($stream.Read($offsetBytes, 0, 4) -ne 4) {
      return $false
    }
    $peOffset = [BitConverter]::ToInt32($offsetBytes, 0)
    if ($peOffset -lt 0 -or $peOffset -gt ($stream.Length - 4)) {
      return $false
    }
    $stream.Position = $peOffset
    $signature = [byte[]]::new(4)
    return $stream.Read($signature, 0, 4) -eq 4 `
      -and $signature[0] -eq 0x50 `
      -and $signature[1] -eq 0x45 `
      -and $signature[2] -eq 0 `
      -and $signature[3] -eq 0
  } finally {
    $stream.Dispose()
  }
}

function Assert-TrustedSignature {
  param([string]$Path)

  if (-not $RequireSignatures) {
    return
  }
  if (-not $signTool) {
    throw "Windows SDK SignTool is required for all-signature package verification."
  }
  & $signTool verify /pa /all /q $Path
  if ($LASTEXITCODE -ne 0) {
    throw "WinVerifyTrust rejected one or more Authenticode signatures for $Path"
  }
  $signature = Get-AuthenticodeSignature -LiteralPath $Path
  if ($signature.Status -ne "Valid") {
    throw "Authenticode status was $($signature.Status) for $Path"
  }
  if (-not [System.Linq.Enumerable]::SequenceEqual(
    $signature.SignerCertificate.SubjectName.RawData,
    $expectedPublisherName.RawData)) {
    throw "Unexpected Authenticode publisher for $Path"
  }
  if (@($signature.SignerCertificate.EnhancedKeyUsageList | Where-Object {
      $_.ObjectId -eq "1.3.6.1.5.5.7.3.3"
    }).Count -ne 1) {
    throw "The Authenticode signer certificate lacks the code-signing EKU for $Path"
  }
  if (@($signature.SignerCertificate.EnhancedKeyUsageList | Where-Object {
      $_.ObjectId -eq $expectedArtifactSigningIdentityEku
    }).Count -ne 1) {
    throw "The signer does not match the reviewed Artifact Signing identity for $Path"
  }
  if ($null -eq $signature.TimeStamperCertificate) {
    throw "The Authenticode signature has no trusted timestamp for $Path"
  }
}

function Assert-PortableExecutable {
  param([string]$Path)
  if (-not (Test-PortableExecutable $Path)) {
    throw "File does not have a Windows PE header: $Path"
  }
  Assert-TrustedSignature $Path
}

function Assert-LauncherVerifierPairing {
  param(
    [string]$Root,
    [string]$Context
  )

  $launcherPath = Join-Path $Root "STFCModBridge.exe"
  $verifierPath = Join-Path $Root "STFCModBridge.ReleaseVerifier.exe"
  $productVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($launcherPath).ProductVersion
  if ($productVersion -cnotmatch '\+commit\.(?<commit>unknown|[0-9a-f]{40})\.verifier\.(?<digest>[0-9a-f]{64})$') {
    throw "The $Context launcher does not carry the closed release-verifier identity."
  }
  if ($ExpectedSourceRevisionId -and $Matches.commit -cne $ExpectedSourceRevisionId) {
    throw "The $Context launcher source identity does not match the expected release commit."
  }
  $actualDigest = (Get-FileHash -LiteralPath $verifierPath -Algorithm SHA256).Hash.ToLowerInvariant()
  if ($actualDigest -cne $Matches.digest) {
    throw "The $Context launcher is not paired to the exact packaged release verifier."
  }
}

function Assert-NoPackagedBattleStorageState {
  param(
    [string]$Root,
    [string]$Context
  )

  $forbidden = @(Get-ChildItem -LiteralPath $Root -File -Recurse | Where-Object {
      $_.Name -match '^(?i:sqlite3.*\.dll|winsqlite3\.dll|e_sqlite3\.dll|Microsoft\.Data\.Sqlite.*\.dll|SQLitePCLRaw.*\.dll)$' `
        -or $_.Name -match '(?i:\.(?:sqlite|sqlite3|db)(?:-(?:wal|shm))?$|-(?:wal|shm)$)'
    })
  if ($forbidden.Count -ne 0) {
    $relative = @($forbidden | ForEach-Object {
        [System.IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\', '/')
      })
    throw "$Context contains packaged Battle state or a loose SQLite/provider binary: $($relative -join ', ')"
  }
}

if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
  throw "Mod Bridge fallback self-update archive was not found: $archive"
}
$archiveInspectionRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
  "stfc-launcher-archive-inspection-" + [Guid]::NewGuid().ToString("N"))
try {
  Expand-Archive -LiteralPath $archive -DestinationPath $archiveInspectionRoot
  $archiveExecutables = @(Get-ChildItem -LiteralPath $archiveInspectionRoot -File -Recurse |
    Where-Object { Test-PortableExecutable $_.FullName })
  $archiveExecutableNames = @($archiveExecutables | ForEach-Object {
    [System.IO.Path]::GetRelativePath($archiveInspectionRoot, $_.FullName).Replace('\', '/')
  })
  $expectedArchiveExecutables = @(
    "STFCModBridge.exe",
    "STFCModBridge.ReleaseVerifier.exe",
    "STFCModBridge.Updater.exe")
  if ($archiveExecutableNames.Count -ne $expectedArchiveExecutables.Count `
      -or @($archiveExecutableNames | Where-Object { $expectedArchiveExecutables -cnotcontains $_ }).Count -ne 0) {
    throw "Fallback archive contains a portable executable outside the reviewed signing allowlist: $($archiveExecutableNames -join ', ')"
  }
  foreach ($executable in $archiveExecutables) {
    Assert-PortableExecutable $executable.FullName
  }
  Assert-NoPackagedBattleStorageState $archiveInspectionRoot "Fallback archive"
  Assert-LauncherVerifierPairing $archiveInspectionRoot "fallback archive"
} finally {
  if (Test-Path -LiteralPath $archiveInspectionRoot) {
    Remove-Item -LiteralPath $archiveInspectionRoot -Recurse -Force
  }
}

$packageFiles = @(Get-ChildItem -LiteralPath $packageDirectory -File)
if ($packageFiles.Count -ne 2 `
    -or $packageFiles.FullName -notcontains $package `
    -or $packageFiles.FullName -notcontains $appInstaller) {
  throw "The package directory must contain exactly STFCModBridge.msix and STFCModBridge.appinstaller."
}
Assert-TrustedSignature $package

$inspectionRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
  "stfc-launcher-msix-inspection-" + [Guid]::NewGuid().ToString("N"))
try {
  & $makeAppx unpack /p $package /d $inspectionRoot /o
  if ($LASTEXITCODE -ne 0) {
    throw "MakeAppx could not unpack the generated MSIX."
  }
  $portableExecutables = @(Get-ChildItem -LiteralPath $inspectionRoot -File -Recurse |
    Where-Object { Test-PortableExecutable $_.FullName })
  $packageExecutableNames = @($portableExecutables | ForEach-Object {
    [System.IO.Path]::GetRelativePath($inspectionRoot, $_.FullName).Replace('\', '/')
  })
  $expectedPackageExecutables = @("STFCModBridge.exe", "STFCModBridge.ReleaseVerifier.exe")
  if ($packageExecutableNames.Count -ne $expectedPackageExecutables.Count `
      -or @($packageExecutableNames | Where-Object { $expectedPackageExecutables -cnotcontains $_ }).Count -ne 0) {
    $relative = @($portableExecutables | ForEach-Object {
      [System.IO.Path]::GetRelativePath($inspectionRoot, $_.FullName).Replace('\', '/')
    })
    throw "MSIX contains a portable executable outside the reviewed allowlist: $($relative -join ', ')"
  }
  foreach ($executable in $portableExecutables) {
    Assert-PortableExecutable $executable.FullName
  }
  Assert-NoPackagedBattleStorageState $inspectionRoot "MSIX"
  Assert-LauncherVerifierPairing $inspectionRoot "MSIX"

  [xml]$manifest = Get-Content -Raw -LiteralPath (Join-Path $inspectionRoot "AppxManifest.xml")
  $namespaces = [System.Xml.XmlNamespaceManager]::new($manifest.NameTable)
  $namespaces.AddNamespace("f", "http://schemas.microsoft.com/appx/manifest/foundation/windows10")
  $namespaces.AddNamespace("uap10", "http://schemas.microsoft.com/appx/manifest/uap/windows10/10")
  $namespaces.AddNamespace("rescap", "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities")
  $identity = $manifest.SelectSingleNode("/f:Package/f:Identity", $namespaces)
  if ($identity.Name -cne $expectedPackageIdentity `
      -or $identity.Publisher -cne $ExpectedPublisherSubject `
      -or $identity.ProcessorArchitecture -cne "x64" `
      -or $identity.Version -cnotmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw "The MSIX identity, publisher, architecture, or version is not the reviewed production contract."
  }
  $application = $manifest.SelectSingleNode("/f:Package/f:Applications/f:Application", $namespaces)
  if ($application.Executable -cne "STFCModBridge.exe" `
      -or $application.GetAttribute("RuntimeBehavior", $namespaces.LookupNamespace("uap10")) -cne "win32App" `
      -or $application.GetAttribute("TrustLevel", $namespaces.LookupNamespace("uap10")) -cne "mediumIL") {
    throw "The MSIX application must remain a medium-integrity full-trust Win32 desktop app."
  }
  $integrity = $manifest.SelectSingleNode(
    "/f:Package/f:Properties/uap10:PackageIntegrity/uap10:Content",
    $namespaces)
  if ($null -eq $integrity -or $integrity.Enforcement -cne "on") {
    throw "The MSIX must enforce package-content integrity at runtime."
  }
  $capabilities = @($manifest.SelectNodes("/f:Package/f:Capabilities/*", $namespaces))
  $expectedCapabilities = @("runFullTrust", "unvirtualizedResources")
  $capabilityNames = @($capabilities | ForEach-Object { $_.Name })
  if ($capabilities.Count -ne $expectedCapabilities.Count `
      -or @($capabilities | Where-Object {
          $_.LocalName -cne "Capability" `
            -or $_.NamespaceURI -cne $namespaces.LookupNamespace("rescap") `
            -or $expectedCapabilities -cnotcontains $_.Name
        }).Count -ne 0 `
      -or @($expectedCapabilities | Where-Object {
          $capabilityNames -cnotcontains $_
        }).Count -ne 0) {
    throw "The MSIX must declare exactly the reviewed runFullTrust and unvirtualizedResources capabilities."
  }

  [xml]$appInstallerDocument = Get-Content -Raw -LiteralPath $appInstaller
  $appInstallerNamespace = [System.Xml.XmlNamespaceManager]::new($appInstallerDocument.NameTable)
  $appInstallerNamespace.AddNamespace("a", "http://schemas.microsoft.com/appx/appinstaller/2021")
  $appInstallerRoot = $appInstallerDocument.SelectSingleNode("/a:AppInstaller", $appInstallerNamespace)
  $mainPackage = $appInstallerDocument.SelectSingleNode(
    "/a:AppInstaller/a:MainPackage",
    $appInstallerNamespace)
  $onLaunch = $appInstallerDocument.SelectSingleNode(
    "/a:AppInstaller/a:UpdateSettings/a:OnLaunch",
    $appInstallerNamespace)
  $appInstallerUri = [Uri]$appInstallerRoot.Uri
  $packageUri = [Uri]$mainPackage.Uri
  if ($appInstallerUri.Scheme -ne [Uri]::UriSchemeHttps `
      -or $appInstallerUri.AbsolutePath -cnotmatch '/(stable|preview)/STFCModBridge\.appinstaller$' `
      -or $packageUri.Scheme -ne [Uri]::UriSchemeHttps `
      -or $packageUri.AbsolutePath -cnotmatch '/packages/v[^/]+/STFCModBridge\.msix$' `
      -or $mainPackage.Name -cne $expectedPackageIdentity `
      -or $mainPackage.Publisher -cne $ExpectedPublisherSubject `
      -or $mainPackage.ProcessorArchitecture -cne "x64" `
      -or $mainPackage.Version -cne $identity.Version `
      -or $appInstallerRoot.Version -cne $identity.Version `
      -or $onLaunch.HoursBetweenUpdateChecks -cne "0" `
      -or $onLaunch.ShowPrompt -cne "true" `
      -or $onLaunch.UpdateBlocksActivation -cne "true") {
    throw "The App Installer descriptor does not match the reviewed identity, hosting, or update contract."
  }
} finally {
  if (Test-Path -LiteralPath $inspectionRoot) {
    Remove-Item -LiteralPath $inspectionRoot -Recurse -Force
  }
}

$signatureState = if ($RequireSignatures) { "signed" } else { "generated" }
Write-Host "Package inspection passed. App Installer is the user entry point; the $signatureState MSIX contains the reviewed launcher and paired release verifier and enforces package integrity."
