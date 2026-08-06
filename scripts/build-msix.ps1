[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string]$Version,
  [Parameter(Mandatory = $true)]
  [string]$UpdateBaseUri,
  [string]$OutputDirectory = "artifacts/win-x64",
  [string]$PayloadDirectory = ""
)

$ErrorActionPreference = "Stop"
$publisher = "CN=Joseph Gustavson, O=Joseph Gustavson, L=Dousman, S=Wisconsin, C=US, PostalCode=53118"
$packageIdentity = "Guffawaffle.STFCModBridge"

if ($Version -notmatch '^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:-rc\.(?<rc>[1-9]\d*))?$') {
  throw "Version must use X.Y.Z or X.Y.Z-rc.N."
}
$components = @(
  [int]$Matches.major,
  [int]$Matches.minor,
  [int]$Matches.patch)
if ($components | Where-Object { $_ -gt 65535 }) {
  throw "MSIX version components must not exceed 65535."
}
$isReleaseCandidate = -not [string]::IsNullOrEmpty($Matches.rc)
$revision = if ($isReleaseCandidate) { [int]$Matches.rc } else { 65535 }
if ($isReleaseCandidate -and ($revision -lt 1 -or $revision -gt 65534)) {
  throw "An RC number must be between 1 and 65534; revision 65535 is reserved for the stable package."
}
$packageVersion = "$($components[0]).$($components[1]).$($components[2]).$revision"
$channel = if ($isReleaseCandidate) { "preview" } else { "stable" }

$baseUri = $UpdateBaseUri.TrimEnd('/')
$parsedBaseUri = $null
if (-not [Uri]::TryCreate($baseUri, [UriKind]::Absolute, [ref]$parsedBaseUri) `
    -or $parsedBaseUri.Scheme -ne [Uri]::UriSchemeHttps `
    -or -not [string]::IsNullOrEmpty($parsedBaseUri.Query) `
    -or -not [string]::IsNullOrEmpty($parsedBaseUri.Fragment)) {
  throw "UpdateBaseUri must be an absolute HTTPS URI without a query or fragment."
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$outputRoot = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
  [System.IO.Path]::GetFullPath($OutputDirectory)
} else {
  [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
}
$payloadRoot = if ($PayloadDirectory) {
  if ([System.IO.Path]::IsPathRooted($PayloadDirectory)) {
    [System.IO.Path]::GetFullPath($PayloadDirectory)
  } else {
    [System.IO.Path]::GetFullPath((Join-Path $repoRoot $PayloadDirectory))
  }
} else {
  Join-Path $outputRoot "app"
}
$launcher = Join-Path $payloadRoot "STFCModBridge.exe"
if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) {
  throw "The launcher payload was not found: $launcher"
}

$sdkRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
$makeAppx = Get-ChildItem -LiteralPath $sdkRoot -Directory -ErrorAction SilentlyContinue |
  Sort-Object Name -Descending |
  ForEach-Object { Join-Path $_.FullName "x64\MakeAppx.exe" } |
  Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
  Select-Object -First 1
if (-not $makeAppx) {
  throw "Windows SDK MakeAppx.exe is required to build the MSIX package."
}

$packageDirectory = Join-Path $outputRoot "package"
$layout = Join-Path $outputRoot ".msix-layout"
$package = Join-Path $packageDirectory "STFCModBridge.msix"
$appInstaller = Join-Path $packageDirectory "STFCModBridge.appinstaller"
$manifestTemplate = Join-Path $repoRoot "packaging\windows\AppxManifest.xml.in"
$appInstallerTemplate = Join-Path $repoRoot "packaging\windows\STFCModBridge.appinstaller.xml.in"
$logo = Join-Path $repoRoot "assets\launcher.png"
$appInstallerUri = "$baseUri/$channel/STFCModBridge.appinstaller"
$packageUri = "$baseUri/packages/v$Version/STFCModBridge.msix"

function ConvertTo-XmlAttribute([string]$Value) {
  return [System.Security.SecurityElement]::Escape($Value)
}

try {
  if (Test-Path -LiteralPath $layout) {
    Remove-Item -LiteralPath $layout -Recurse -Force
  }
  New-Item -ItemType Directory -Path (Join-Path $layout "Assets") -Force | Out-Null
  New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
  Copy-Item -LiteralPath $launcher -Destination (Join-Path $layout "STFCModBridge.exe") -Force
  Copy-Item -LiteralPath $logo -Destination (Join-Path $layout "Assets\Logo.png") -Force

  $manifest = (Get-Content -Raw -LiteralPath $manifestTemplate).Replace(
    "__PACKAGE_VERSION__",
    $packageVersion)
  $manifestPath = Join-Path $layout "AppxManifest.xml"
  [System.IO.File]::WriteAllText($manifestPath, $manifest, [System.Text.UTF8Encoding]::new($false))
  [xml]$manifest | Out-Null

  $appInstallerDocument = Get-Content -Raw -LiteralPath $appInstallerTemplate
  $appInstallerDocument = $appInstallerDocument.Replace("__PACKAGE_VERSION__", $packageVersion)
  $appInstallerDocument = $appInstallerDocument.Replace(
    "__APPINSTALLER_URI__",
    (ConvertTo-XmlAttribute $appInstallerUri))
  $appInstallerDocument = $appInstallerDocument.Replace(
    "__PACKAGE_URI__",
    (ConvertTo-XmlAttribute $packageUri))
  [System.IO.File]::WriteAllText(
    $appInstaller,
    $appInstallerDocument,
    [System.Text.UTF8Encoding]::new($false))
  [xml]$appInstallerDocument | Out-Null

  if (Test-Path -LiteralPath $package -PathType Leaf) {
    [System.IO.File]::Delete($package)
  }
  & $makeAppx pack /d $layout /p $package /o
  if ($LASTEXITCODE -ne 0) {
    throw "MakeAppx failed with exit code $LASTEXITCODE."
  }
} finally {
  if (Test-Path -LiteralPath $layout) {
    Remove-Item -LiteralPath $layout -Recurse -Force
  }
}

Write-Host "Built unsigned MSIX: $package"
Write-Host "Built App Installer descriptor: $appInstaller"
Write-Host "Package identity: $packageIdentity"
Write-Host "Package publisher: $publisher"
Write-Host "Package version: $packageVersion ($Version, $channel)"
Write-Host "Stable App Installer URI: $appInstallerUri"
Write-Host "Immutable package URI: $packageUri"
