[CmdletBinding()]
param(
  [string]$OutputDirectory = "artifacts/win-x64",
  [switch]$RequireSignatures,
  [string]$ExpectedPublisherSubject = "CN=Joseph Gustavson, O=Joseph Gustavson, L=Dousman, S=Wisconsin, C=US, PostalCode=53118"
)

$ErrorActionPreference = "Stop"
$expectedPublisherName = [System.Security.Cryptography.X509Certificates.X500DistinguishedName]::new(
  $ExpectedPublisherSubject)
$expectedArtifactSigningIdentityEku = "1.3.6.1.4.1.311.97.664386437.910814316.510550690.722133748"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$outputRoot = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
  [System.IO.Path]::GetFullPath($OutputDirectory)
} else {
  [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
}
$archive = Join-Path $outputRoot "stfc-mod-bridge-win-x64.zip"
$setupDirectory = Join-Path $outputRoot "setup"
$setup = Join-Path $setupDirectory "STFCModBridge.Setup.exe"

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

function Assert-PortableExecutable {
  param([string]$Path)

  if (-not (Test-PortableExecutable $Path)) {
    throw "File does not have a Windows PE header: $Path"
  }

  if ($RequireSignatures) {
    $signTool = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($null -eq $signTool) {
      $kitsBin = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
      $signTool = Get-ChildItem -LiteralPath $kitsBin -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending |
        ForEach-Object { Join-Path $_.FullName "x64\signtool.exe" } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1
    } else {
      $signTool = $signTool.Source
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
    $codeSigningEku = @($signature.SignerCertificate.EnhancedKeyUsageList | Where-Object {
      $_.ObjectId -eq "1.3.6.1.5.5.7.3.3"
    })
    if ($codeSigningEku.Count -ne 1) {
      throw "The Authenticode signer certificate lacks the code-signing EKU for $Path"
    }
    $durableIdentityEku = @($signature.SignerCertificate.EnhancedKeyUsageList | Where-Object {
      $_.ObjectId -eq $expectedArtifactSigningIdentityEku
    })
    if ($durableIdentityEku.Count -ne 1) {
      throw "The signer does not match the reviewed Artifact Signing identity for $Path"
    }
    if ($null -eq $signature.TimeStamperCertificate) {
      throw "The Authenticode signature has no trusted timestamp for $Path"
    }
  }
}

if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
  throw "Mod Bridge self-update archive was not found: $archive"
}
$setupFiles = @(Get-ChildItem -LiteralPath $setupDirectory -File)
if ($setupFiles.Count -ne 1 -or $setupFiles[0].FullName -ne $setup) {
  throw "The setup directory must contain exactly one user-facing artifact: STFCModBridge.Setup.exe"
}
Assert-PortableExecutable $setup

$inspectionRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("stfc-launcher-inspection-" + [Guid]::NewGuid().ToString("N"))
try {
  [System.IO.Compression.ZipFile]::ExtractToDirectory($archive, $inspectionRoot)
  $files = @(Get-ChildItem -LiteralPath $inspectionRoot -File -Recurse)
  $relativeFiles = @($files | ForEach-Object {
    [System.IO.Path]::GetRelativePath($inspectionRoot, $_.FullName).Replace('\', '/')
  })
  $requiredExecutables = @("STFCModBridge.exe", "STFCModBridge.Updater.exe")
  $portableExecutables = @($files | Where-Object { Test-PortableExecutable $_.FullName } | ForEach-Object {
    [System.IO.Path]::GetRelativePath($inspectionRoot, $_.FullName).Replace('\', '/')
  })
  $unexpectedPortableExecutables = @($portableExecutables | Where-Object { $_ -cnotin $requiredExecutables })
  if ($unexpectedPortableExecutables.Count -gt 0) {
    throw "Mod Bridge archive contains a portable executable outside the reviewed signing allowlist: $($unexpectedPortableExecutables -join ', ')"
  }
  foreach ($required in $requiredExecutables) {
    if (@($relativeFiles | Where-Object { $_ -eq $required }).Count -ne 1) {
      throw "Mod Bridge archive must contain exactly one root $required"
    }
    Assert-PortableExecutable (Join-Path $inspectionRoot $required)
  }
} finally {
  if (Test-Path -LiteralPath $inspectionRoot) {
    Remove-Item -LiteralPath $inspectionRoot -Recurse -Force
  }
}

Write-Host "Package inspection passed. Setup is the only user-facing install artifact; the update archive contains the expected PE boundary."
