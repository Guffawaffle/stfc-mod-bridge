[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][string]$SbomPath,
  [Parameter(Mandatory = $true)][string]$ProfilesBuildReceiptPath
)
$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$pin = Get-Content -LiteralPath (Join-Path $repoRoot "dependencies/stfc-profiles-source-pin.json") -Raw | ConvertFrom-Json
$receipt = Get-Content -LiteralPath $ProfilesBuildReceiptPath -Raw | ConvertFrom-Json
if ($receipt.schemaVersion -ne 1 -or $receipt.repository -cne $pin.repository `
    -or $receipt.sourceRevision -cne $pin.revision -or $receipt.developmentOverride `
    -or @($receipt.dirtyFiles).Count -ne 0 -or $receipt.target -cne "windows-x64-release" `
    -or $pin.sourceArchiveSha256 -cnotmatch '^[0-9a-f]{64}$' `
    -or $receipt.sourceArchiveSha256 -cne $pin.sourceArchiveSha256 `
    -or $receipt.nativeSha256 -cnotmatch '^[0-9a-f]{64}$') {
  throw "Release native source inventory requires the immutable archive build receipt; development overrides do not qualify."
}
$sbom = Get-Content -LiteralPath $SbomPath -Raw | ConvertFrom-Json
$native = @($sbom.files | Where-Object { $_.fileName -ceq "./stfc-profiles-native.dll" })
if ($native.Count -ne 1) { throw "The SBOM must contain the exact bundled shared native DLL." }
$sourceId = "SPDXRef-Native-STFCProfiles"
$source = [ordered]@{
  SPDXID = $sourceId
  name = "STFC Profiles source snapshot"
  versionInfo = $pin.revision
  downloadLocation = "https://codeload.github.com/$($pin.repository)/tar.gz/$($pin.revision)"
  filesAnalyzed = $false
  licenseConcluded = "NOASSERTION"
  licenseDeclared = "GPL-3.0-only"
  copyrightText = "NOASSERTION"
  checksums = @([ordered]@{ algorithm = "SHA256"; checksumValue = $receipt.sourceArchiveSha256 })
  comment = "Pinned source/recipe inventory recorded by the native build. Source archive SHA-256 matches the reviewed immutable pin; this is not independent binary object provenance. The pre-signing native DLL SHA-256 was $($receipt.nativeSha256); the bundled final DLL hash remains in its SPDX file entry. XMake $($pin.xmakeVersion), windows-x64-release."
}
$packages = @($sbom.packages) + @([pscustomobject]$source)
$relationships = @($sbom.relationships) + @([pscustomobject]@{
  spdxElementId = $native[0].SPDXID; relationshipType = "OTHER"; relatedSpdxElement = $sourceId
  comment = "Expected native source and static dependency recipe inventory for this hash-paired DLL; not independently discovered binary provenance."
})
foreach ($dependency in $pin.nativeDependencies) {
  $id = "SPDXRef-Native-" + ($dependency.id -replace '[^A-Za-z0-9.-]', '-')
  $packages += [pscustomobject][ordered]@{
    SPDXID = $id; name = $dependency.id; versionInfo = $dependency.version
    downloadLocation = "NOASSERTION"; filesAnalyzed = $false
    licenseConcluded = "NOASSERTION"; licenseDeclared = $dependency.license; copyrightText = "NOASSERTION"
    comment = "Exact static/header dependency and effective license choice from the pinned Profiles recipe inventory. Version pin is build input evidence, not independently inspected object provenance."
  }
  $relationships += [pscustomobject]@{
    spdxElementId = $sourceId; relationshipType = "DEPENDS_ON"; relatedSpdxElement = $id
    comment = "Declared shared native recipe dependency."
  }
}
$sbom.packages = $packages
$sbom.relationships = $relationships
[IO.File]::WriteAllText([IO.Path]::GetFullPath($SbomPath), ($sbom | ConvertTo-Json -Depth 50) + "`n", [Text.UTF8Encoding]::new($false))
