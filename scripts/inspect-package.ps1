[CmdletBinding()]
param(
  [string]$OutputDirectory = "artifacts/win-x64",
  [switch]$RequireSignatures,
  [string]$ExpectedPublisher = "Joseph Gustavson"
)

$ErrorActionPreference = "Stop"

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
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne "Valid") {
      throw "Authenticode status was $($signature.Status) for $Path"
    }
    $publisher = $signature.SignerCertificate.GetNameInfo(
      [System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName,
      $false)
    if (-not [string]::Equals($publisher, $ExpectedPublisher, [System.StringComparison]::OrdinalIgnoreCase)) {
      throw "Unexpected Authenticode publisher for $Path"
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
