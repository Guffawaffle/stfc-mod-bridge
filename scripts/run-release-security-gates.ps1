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
Write-Host "Scanning pre-signing release payload with Microsoft Defender signatures $($defenderStatus.AntivirusSignatureVersion)."
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
    throw "Microsoft Defender rejected or could not scan the pre-signing release payload (exit $LASTEXITCODE)."
}

& (Join-Path $PSScriptRoot "generate-payload-sbom.ps1") `
    -ArtifactDirectory $artifactRoot `
    -OutputPath $OutputPath `
    -Version $Version `
    -SourceRevisionId $SourceRevisionId
Write-Host "Pre-signing security gates passed."
