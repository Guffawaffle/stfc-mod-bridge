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
  if ($item.attributionStatus -notin @("required", "review-pending", "internal-build-input")) {
    throw "Inventory item '$($item.id)' has unsupported attribution status '$($item.attributionStatus)'."
  }

  if ($item.attributionStatus -eq "required" -and [string]::IsNullOrWhiteSpace($item.noticeId)) {
    throw "Attribution-required inventory item '$($item.id)' has no notice ID."
  }

  if (-not [string]::IsNullOrWhiteSpace($item.noticeId) -and -not $noticesById.ContainsKey($item.noticeId)) {
    throw "Inventory item '$($item.id)' references missing notice '$($item.noticeId)'."
  }
}
foreach ($item in $catalog.dependencyInventory) {
  if ($item.evidenceKind -notin @("resolved-package", "runtime-pack")) {
    throw "Dependency inventory item '$($item.id)' has unsupported evidence kind '$($item.evidenceKind)'."
  }
}
foreach ($item in $catalog.assetInventory) {
  if ($item.evidenceKind -notin @("project-input", "package-transitive")) {
    throw "Asset inventory item '$($item.id)' has unsupported evidence kind '$($item.evidenceKind)'."
  }
}

function Normalize-ProjectPath([string]$path) {
  return $path.Replace("\", "/")
}

$productionProjects = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot "src") -Recurse -Filter "*.csproj")
$resolvedDependencies = @{}
$projectInputs = @{}
foreach ($projectFile in $productionProjects) {
  [xml]$project = Get-Content -Raw -LiteralPath $projectFile.FullName
  foreach ($kind in @("Resource", "Content", "EmbeddedResource")) {
    foreach ($element in @($project.SelectNodes("//*[local-name()='$kind']"))) {
      $include = [string]$element.Include
      if (-not [string]::IsNullOrWhiteSpace($include)) {
        $id = "$($projectFile.BaseName)|$kind|$(Normalize-ProjectPath $include)"
        $projectInputs[$id] = $true
      }
    }
  }
  foreach ($propertyName in @("ApplicationIcon", "ApplicationManifest")) {
    foreach ($element in @($project.SelectNodes("//*[local-name()='$propertyName']"))) {
      $include = [string]$element.InnerText
      if (-not [string]::IsNullOrWhiteSpace($include)) {
        $id = "$($projectFile.BaseName)|$propertyName|$(Normalize-ProjectPath $include)"
        $projectInputs[$id] = $true
      }
    }
  }

  $assetsPath = Join-Path $projectFile.DirectoryName "obj\project.assets.json"
  if (-not (Test-Path -LiteralPath $assetsPath)) {
    throw "Resolved dependency manifest is missing for '$($projectFile.BaseName)'. Run dotnet restore first."
  }
  $assets = Get-Content -Raw -LiteralPath $assetsPath | ConvertFrom-Json
  foreach ($target in $assets.targets.PSObject.Properties.Value) {
    foreach ($library in $target.PSObject.Properties) {
      $definition = $library.Value
      $hasRuntimePayload = $null -ne $definition.runtime `
        -or $null -ne $definition.runtimeTargets `
        -or $null -ne $definition.native `
        -or $null -ne $definition.resource
      if ($definition.type -eq "package" -and $hasRuntimePayload) {
        $separator = $library.Name.LastIndexOf('/')
        if ($separator -lt 1) {
          throw "Resolved package identity '$($library.Name)' is malformed."
        }
        $id = $library.Name.Substring(0, $separator)
        $version = $library.Name.Substring($separator + 1)
        $resolvedDependencies["resolved-package|$id|$version"] = $true
      }
    }
  }
  foreach ($framework in $assets.project.frameworks.PSObject.Properties.Value) {
    foreach ($download in @($framework.downloadDependencies)) {
      if ($null -eq $download) {
        continue
      }
      $version = [string]$download.version
      if ($version -match '^\[([^,]+),\s*\1\]$') {
        $version = $Matches[1]
      }
      $resolvedDependencies["runtime-pack|$($download.name)|$version"] = $true
    }
  }
}

$checkedDependencyInventory = @{}
foreach ($item in $catalog.dependencyInventory) {
  if ($item.evidenceKind -in @("resolved-package", "runtime-pack")) {
    $key = "$($item.evidenceKind)|$($item.id)|$($item.version)"
    $checkedDependencyInventory[$key] = $true
  }
}
foreach ($key in $resolvedDependencies.Keys) {
  if (-not $checkedDependencyInventory.ContainsKey($key)) {
    throw "Resolved publish dependency '$key' is missing from the notice dependency inventory."
  }
}
foreach ($key in $checkedDependencyInventory.Keys) {
  if (-not $resolvedDependencies.ContainsKey($key)) {
    throw "Notice dependency inventory '$key' is stale or absent from resolved publish inputs."
  }
}

$checkedProjectInputs = @{}
foreach ($item in $catalog.assetInventory) {
  if ($item.evidenceKind -eq "project-input") {
    $checkedProjectInputs[[string]$item.id] = $true
  }
}
foreach ($id in $projectInputs.Keys) {
  if (-not $checkedProjectInputs.ContainsKey($id)) {
    throw "Explicit bundled project input '$id' is unclassified in the notice asset inventory."
  }
}
foreach ($id in $checkedProjectInputs.Keys) {
  if (-not $projectInputs.ContainsKey($id)) {
    throw "Notice asset inventory project input '$id' is stale or absent from production projects."
  }
}

$builder = [System.Text.StringBuilder]::new()
[void]$builder.AppendLine("# Third-party notices")
[void]$builder.AppendLine()
[void]$builder.AppendLine("This file is generated from ``docs/windows-launcher/about-content.v1.json``. Do not edit it directly.")
[void]$builder.AppendLine()
[void]$builder.AppendLine("STFC Mod Bridge is distributed under the repository license. The components below retain their own terms.")
[void]$builder.AppendLine()
[void]$builder.AppendLine("## Coverage and open review")
[void]$builder.AppendLine()
[void]$builder.AppendLine($catalog.noticeCoverageStatus)
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

  Write-Host "PASS: notices, resolved publish dependencies, explicit bundled inputs, and generated document agree."
  exit 0
}

[System.IO.File]::WriteAllText(
  $outputPath,
  $expected,
  [System.Text.UTF8Encoding]::new($false))
Write-Host "Generated $outputPath"
