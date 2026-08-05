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
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = (Resolve-Path -LiteralPath $ArtifactDirectory).Path
$outputFile = [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $OutputPath))
$outputDirectory = Split-Path -Parent $outputFile

if (Test-Path -LiteralPath (Join-Path $repositoryRoot ".gitmodules")) {
    throw "Release inputs must not contain unaudited Git submodules."
}

$projects = @(Get-ChildItem -LiteralPath $repositoryRoot -Recurse -Filter *.csproj -File |
    Where-Object { $_.FullName -notmatch '[\\/](artifacts|bin|obj)[\\/]' })
foreach ($project in $projects) {
    $lockFile = Join-Path $project.DirectoryName "packages.lock.json"
    if (-not (Test-Path -LiteralPath $lockFile -PathType Leaf)) {
        throw "Missing dependency lock file for '$($project.FullName)'."
    }
}

Write-Host "Auditing locked direct and transitive NuGet dependencies for known vulnerabilities."
$auditOutput = & dotnet list (Join-Path $repositoryRoot "STFCCommunityMod.Launcher.sln") package `
    --vulnerable --include-transitive --format json --output-version 1 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    throw "NuGet vulnerability audit failed:`n$auditOutput"
}
$audit = $auditOutput | ConvertFrom-Json
$vulnerablePackages = @()
foreach ($project in @($audit.projects)) {
    if (-not $project.PSObject.Properties["frameworks"]) {
        continue
    }
    foreach ($framework in @($project.frameworks)) {
        foreach ($propertyName in @("topLevelPackages", "transitivePackages")) {
            if (-not $framework.PSObject.Properties[$propertyName]) {
                continue
            }
            $vulnerablePackages += @($framework.$propertyName | Where-Object {
                $_.PSObject.Properties["vulnerabilities"] -and $_.vulnerabilities.Count -gt 0
            })
        }
    }
}
if ($vulnerablePackages.Count -gt 0) {
    $identities = $vulnerablePackages | ForEach-Object { "$($_.id) $($_.resolvedVersion)" }
    throw "Known vulnerable dependencies block release signing: $($identities -join ', ')."
}

$defenderStatus = Get-MpComputerStatus
if (-not $defenderStatus.AntivirusEnabled -or -not $defenderStatus.AntivirusSignatureVersion) {
    throw "Microsoft Defender must be enabled with loaded signatures for the pre-signing malware gate."
}
Write-Host "Scanning unsigned release payload with Microsoft Defender signatures $($defenderStatus.AntivirusSignatureVersion)."
$platformScanner = @(Get-ChildItem `
    -LiteralPath "C:\ProgramData\Microsoft\Windows Defender\Platform" `
    -Filter MpCmdRun.exe `
    -Recurse `
    -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '[\\/]X86[\\/]' } |
    Sort-Object FullName -Descending |
    Select-Object -First 1 -ExpandProperty FullName)
$scanner = if ($platformScanner.Count -gt 0) {
    $platformScanner[0]
} else {
    Join-Path $env:ProgramFiles "Windows Defender\MpCmdRun.exe"
}
if (-not (Test-Path -LiteralPath $scanner -PathType Leaf)) {
    throw "Microsoft Defender command-line scanner was not found."
}
& $scanner -Scan -ScanType 3 -File $artifactRoot -DisableRemediation
if ($LASTEXITCODE -ne 0) {
    throw "Microsoft Defender rejected or could not scan the unsigned release payload (exit $LASTEXITCODE)."
}

Write-Host "Restoring the repository-pinned SBOM generator."
& dotnet tool restore
if ($LASTEXITCODE -ne 0) {
    throw "The pinned SBOM generator could not be restored."
}

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
if (Test-Path -LiteralPath $outputFile -PathType Leaf) {
    Remove-Item -LiteralPath $outputFile -Force
}
$manifestRoot = Join-Path $outputDirectory "sbom-generation"
New-Item -ItemType Directory -Path $manifestRoot -Force | Out-Null
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
Remove-Item -LiteralPath $manifestRoot -Recurse -Force
Write-Host "Pre-signing security gates passed and SPDX SBOM was written to '$outputFile'."
