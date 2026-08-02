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
$archive = Join-Path $outputRoot "stfc-community-mod-launcher-win-x64.zip"
$setupDirectory = Join-Path $outputRoot "setup"
$setup = Join-Path $setupDirectory "STFCCommunityMod.Launcher.Setup.exe"

function Assert-PortableExecutable {
  param([string]$Path)

  if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
    throw "Expected PE file was not found: $Path"
  }
  $stream = [System.IO.File]::OpenRead($Path)
  try {
    if ($stream.Length -lt 2 -or $stream.ReadByte() -ne 0x4d -or $stream.ReadByte() -ne 0x5a) {
      throw "File does not have a Windows PE header: $Path"
    }
  } finally {
    $stream.Dispose()
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
  throw "Launcher self-update archive was not found: $archive"
}
$setupFiles = @(Get-ChildItem -LiteralPath $setupDirectory -File)
if ($setupFiles.Count -ne 1 -or $setupFiles[0].FullName -ne $setup) {
  throw "The setup directory must contain exactly one user-facing artifact: STFCCommunityMod.Launcher.Setup.exe"
}
Assert-PortableExecutable $setup

$inspectionRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("stfc-launcher-inspection-" + [Guid]::NewGuid().ToString("N"))
try {
  [System.IO.Compression.ZipFile]::ExtractToDirectory($archive, $inspectionRoot)
  $files = @(Get-ChildItem -LiteralPath $inspectionRoot -File -Recurse)
  $relativeFiles = @($files | ForEach-Object {
    [System.IO.Path]::GetRelativePath($inspectionRoot, $_.FullName).Replace('\', '/')
  })
  $requiredExecutables = @("STFCCommunityMod.Launcher.exe", "STFCCommunityMod.Launcher.Updater.exe")
  $packagedExecutables = @($relativeFiles | Where-Object { $_.EndsWith('.exe', [System.StringComparison]::OrdinalIgnoreCase) })
  if ($packagedExecutables.Count -ne $requiredExecutables.Count) {
    throw "Launcher archive contains an executable outside the reviewed signing allowlist."
  }
  foreach ($required in $requiredExecutables) {
    if (@($relativeFiles | Where-Object { $_ -eq $required }).Count -ne 1) {
      throw "Launcher archive must contain exactly one root $required"
    }
    Assert-PortableExecutable (Join-Path $inspectionRoot $required)
  }
} finally {
  if (Test-Path -LiteralPath $inspectionRoot) {
    Remove-Item -LiteralPath $inspectionRoot -Recurse -Force
  }
}

Write-Host "Package inspection passed. Setup is the only user-facing install artifact; the update archive contains the expected PE boundary."
