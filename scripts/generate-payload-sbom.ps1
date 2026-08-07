[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string]$ArtifactDirectory,
  [Parameter(Mandatory = $true)]
  [string]$OutputPath,
  [Parameter(Mandatory = $true)]
  [string]$Version,
  [Parameter(Mandatory = $true)]
  [ValidatePattern('^[0-9a-f]{40}$')]
  [string]$SourceRevisionId
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = (Resolve-Path -LiteralPath $ArtifactDirectory).Path
$outputFile = if ([System.IO.Path]::IsPathRooted($OutputPath)) {
  [System.IO.Path]::GetFullPath($OutputPath)
} else {
  [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputPath))
}
$outputDirectory = Split-Path -Parent $outputFile

& dotnet tool restore
if ($LASTEXITCODE -ne 0) {
  throw "The pinned SBOM generator could not be restored."
}
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
if (Test-Path -LiteralPath $outputFile -PathType Leaf) {
  Remove-Item -LiteralPath $outputFile -Force
}
$manifestRoot = Join-Path $outputDirectory ".payload-sbom-work"
if (Test-Path -LiteralPath $manifestRoot) {
  Remove-Item -LiteralPath $manifestRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $manifestRoot | Out-Null
try {
  & dotnet tool run sbom-tool -- generate `
    -b $artifactRoot `
    -bc $repositoryRoot `
    -m $manifestRoot `
    -pn "STFC Mod Bridge" `
    -pv $Version `
    -ps "Organization: Joseph Gustavson" `
    -nsb "https://github.com/Guffawaffle/stfc-mod-bridge" `
    -nsu $SourceRevisionId `
    -D true `
    -pm true `
    -F false
  if ($LASTEXITCODE -ne 0) {
    throw "SBOM generation failed."
  }
  $generatedSboms = @(Get-ChildItem -LiteralPath $manifestRoot -Recurse -Filter manifest.spdx.json -File)
  if ($generatedSboms.Count -ne 1) {
    throw "Expected exactly one SPDX 2.2 SBOM, found $($generatedSboms.Count)."
  }
  Copy-Item -LiteralPath $generatedSboms[0].FullName -Destination $outputFile -Force
  $sbom = Get-Content -LiteralPath $outputFile -Raw | ConvertFrom-Json
  if ($sbom.spdxVersion -ne "SPDX-2.2" -or $sbom.name -ne "STFC Mod Bridge $Version") {
    throw "Generated SBOM did not satisfy the reviewed SPDX package identity."
  }
  foreach ($name in @(
      "STFCModBridge.exe",
      "STFCModBridge.ReleaseVerifier.exe",
      "STFCModBridge.Updater.exe")) {
    $path = Join-Path $artifactRoot $name
    $entry = @($sbom.files | Where-Object { $_.fileName -eq "./$name" })
    $digest = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($entry.Count -ne 1 `
        -or @($entry[0].checksums | Where-Object {
            $_.algorithm -eq "SHA256" -and $_.checksumValue -ceq $digest
          }).Count -ne 1) {
      throw "Payload SBOM is not bound to the exact $name bytes."
    }
  }
} finally {
  if (Test-Path -LiteralPath $manifestRoot) {
    Remove-Item -LiteralPath $manifestRoot -Recurse -Force
  }
}

Write-Host "SPDX SBOM was written to '$outputFile'."
