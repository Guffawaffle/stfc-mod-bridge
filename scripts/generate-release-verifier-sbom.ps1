[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string]$BinaryDirectory,
  [Parameter(Mandatory = $true)]
  [string]$OutputPath,
  [string]$Version = "0.1.0-dev",
  [string]$SourceRevisionId = ""
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$moduleRoot = Join-Path $repositoryRoot "src/STFCModBridge.ReleaseVerifier"
$inventoryPath = Join-Path $moduleRoot "dependencies.v1.txt"
$binaryRoot = (Resolve-Path -LiteralPath $BinaryDirectory).Path
$binaryPath = Join-Path $binaryRoot "STFCModBridge.ReleaseVerifier.exe"
$finalSbomPath = if ([System.IO.Path]::IsPathRooted($OutputPath)) {
  [System.IO.Path]::GetFullPath($OutputPath)
} else {
  [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputPath))
}
if (-not (Test-Path -LiteralPath $binaryPath -PathType Leaf)) {
  throw "The release-verifier binary was not found at its canonical path: $binaryPath"
}
if ($Version -cnotmatch '^\d+\.\d+\.\d+(?:-rc\.\d+|-dev)?$') {
  throw "Release-verifier SBOM version is invalid."
}
if (-not $SourceRevisionId) {
  $SourceRevisionId = (& git -C $repositoryRoot rev-parse HEAD).Trim()
}
if ($SourceRevisionId -cnotmatch '^[0-9a-f]{40}$') {
  throw "Release-verifier SBOM source revision must be a lowercase Git commit."
}

& dotnet tool restore
if ($LASTEXITCODE -ne 0) {
  throw "The repository-pinned SBOM generator could not be restored."
}
$outputDirectory = Split-Path -Parent $finalSbomPath
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
if (Test-Path -LiteralPath $finalSbomPath) {
  Remove-Item -LiteralPath $finalSbomPath -Force
}
$sbomWork = Join-Path $outputDirectory ".release-verifier-sbom-work"
if (Test-Path -LiteralPath $sbomWork) {
  Remove-Item -LiteralPath $sbomWork -Recurse -Force
}
New-Item -ItemType Directory -Path $sbomWork | Out-Null
try {
  & dotnet tool run sbom-tool -- generate `
    -b $binaryRoot `
    -bc $moduleRoot `
    -m $sbomWork `
    -pn "STFC Mod Bridge Release Verifier" `
    -pv $Version `
    -ps "Organization: Joseph Gustavson" `
    -nsb "https://github.com/Guffawaffle/stfc-mod-bridge" `
    -nsu $SourceRevisionId `
    -D true `
    -pm true `
    -F false
  if ($LASTEXITCODE -ne 0) {
    throw "Release verifier SBOM generation failed."
  }
  $generatedSboms = @(Get-ChildItem -LiteralPath $sbomWork -Recurse -Filter "manifest.spdx.json" -File)
  if ($generatedSboms.Count -ne 1) {
    throw "Release verifier build produced $($generatedSboms.Count) SPDX manifests instead of one."
  }
  $expectedInventory = @(Get-Content -LiteralPath $inventoryPath) `
    | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
  $expectedSbomInventory = @($expectedInventory | ForEach-Object {
      $parts = $_.Split(' ', 3, [StringSplitOptions]::RemoveEmptyEntries)
      "$($parts[0]) $($parts[1])"
    } | Sort-Object -Unique)
  $sbom = Get-Content -Raw -LiteralPath $generatedSboms[0].FullName | ConvertFrom-Json
  $sbomInventory = @($sbom.packages | Where-Object { $_.SPDXID -ne "SPDXRef-RootPackage" } | ForEach-Object {
      "$($_.name) $($_.versionInfo)"
    } | Sort-Object -Unique)
  if ($sbom.spdxVersion -ne "SPDX-2.2" `
      -or $sbom.name -ne "STFC Mod Bridge Release Verifier $Version" `
      -or [string]::Join("`n", $sbomInventory) -cne [string]::Join("`n", $expectedSbomInventory)) {
    throw "Release verifier SPDX inventory does not match the compiled module lock."
  }
  $binaryEntry = @($sbom.files | Where-Object { $_.fileName -eq "./STFCModBridge.ReleaseVerifier.exe" })
  $binaryDigest = (Get-FileHash -LiteralPath $binaryPath -Algorithm SHA256).Hash.ToLowerInvariant()
  if ($binaryEntry.Count -ne 1 `
      -or @($binaryEntry[0].checksums | Where-Object {
          $_.algorithm -eq "SHA256" -and $_.checksumValue -ceq $binaryDigest
        }).Count -ne 1) {
    throw "Release verifier SBOM is not bound to the exact helper bytes."
  }
  Copy-Item -LiteralPath $generatedSboms[0].FullName -Destination $finalSbomPath -Force
} finally {
  if (Test-Path -LiteralPath $sbomWork) {
    Remove-Item -LiteralPath $sbomWork -Recurse -Force
  }
}

Write-Host "PASS: release-verifier SPDX inventory describes the exact binary directory."
