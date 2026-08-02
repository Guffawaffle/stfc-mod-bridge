param(
  [switch]$Check
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$catalogPath = Join-Path $repositoryRoot "docs\windows-launcher\about-content.v1.json"
$outputPath = Join-Path $repositoryRoot "THIRD-PARTY-NOTICES.md"
$catalog = Get-Content -Raw -LiteralPath $catalogPath | ConvertFrom-Json

if ($catalog.schemaVersion -ne 1) {
  throw "Unsupported About catalog schema version '$($catalog.schemaVersion)'."
}

$noticesById = @{}
foreach ($notice in $catalog.thirdPartyNotices) {
  if ($noticesById.ContainsKey($notice.id)) {
    throw "Duplicate third-party notice ID '$($notice.id)'."
  }

  $noticesById[$notice.id] = $notice
}

foreach ($item in @($catalog.dependencyInventory) + @($catalog.assetInventory)) {
  if ($item.attributionStatus -eq "required" -and [string]::IsNullOrWhiteSpace($item.noticeId)) {
    throw "Attribution-required inventory item '$($item.id)' has no notice ID."
  }

  if (-not [string]::IsNullOrWhiteSpace($item.noticeId) -and -not $noticesById.ContainsKey($item.noticeId)) {
    throw "Inventory item '$($item.id)' references missing notice '$($item.noticeId)'."
  }
}

$declaredPackages = Get-ChildItem -LiteralPath (Join-Path $repositoryRoot "src") -Recurse -Filter "*.csproj" |
  ForEach-Object {
    [xml]$project = Get-Content -Raw -LiteralPath $_.FullName
    @($project.Project.ItemGroup.PackageReference) |
      Where-Object { $_ -and $_.Include } |
      ForEach-Object { [string]$_.Include }
  } |
  Sort-Object -Unique
$inventoryIds = @($catalog.dependencyInventory | ForEach-Object { [string]$_.id })
foreach ($package in $declaredPackages) {
  if ($package -notin $inventoryIds) {
    throw "Production PackageReference '$package' is missing from the notice dependency inventory."
  }
}

$builder = [System.Text.StringBuilder]::new()
[void]$builder.AppendLine("# Third-party notices")
[void]$builder.AppendLine()
[void]$builder.AppendLine("This file is generated from ``docs/windows-launcher/about-content.v1.json``. Do not edit it directly.")
[void]$builder.AppendLine()
[void]$builder.AppendLine("STFC Mod Control is distributed under the repository license. The components below retain their own terms.")
[void]$builder.AppendLine()
foreach ($notice in $catalog.thirdPartyNotices) {
  [void]$builder.AppendLine("## $($notice.name)")
  [void]$builder.AppendLine()
  [void]$builder.AppendLine("- Version: $($notice.version)")
  [void]$builder.AppendLine("- License: $($notice.license)")
  [void]$builder.AppendLine("- Source: $($notice.sourceUrl)")
  [void]$builder.AppendLine("- Authoritative license information: $($notice.licenseUrl)")
  [void]$builder.AppendLine()
  [void]$builder.AppendLine('```text')
  [void]$builder.AppendLine(($notice.noticeText -replace "`r`n", "`n"))
  [void]$builder.AppendLine('```')
  [void]$builder.AppendLine()
}

[void]$builder.AppendLine("## Attribution review boundary")
[void]$builder.AppendLine()
[void]$builder.AppendLine($catalog.legalReviewStatus)
$expected = $builder.ToString().Replace("`r`n", "`n")

if ($Check) {
  if (-not (Test-Path -LiteralPath $outputPath)) {
    throw "Generated notice document is missing: $outputPath"
  }

  $actual = (Get-Content -Raw -LiteralPath $outputPath).Replace("`r`n", "`n")
  if ($actual -ne $expected) {
    throw "THIRD-PARTY-NOTICES.md is stale. Run scripts/generate-third-party-notices.ps1."
  }

  Write-Host "PASS: third-party notice catalog, production package coverage, and generated document agree."
  exit 0
}

[System.IO.File]::WriteAllText(
  $outputPath,
  $expected,
  [System.Text.UTF8Encoding]::new($false))
Write-Host "Generated $outputPath"
