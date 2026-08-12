[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string]$Bucket,
  [Parameter(Mandatory = $true)]
  [string]$UpdateBaseUri,
  [Parameter(Mandatory = $true)]
  [string]$Version,
  [string]$PackagePath = "artifacts/win-x64/package/STFCModBridge.msix",
  [string]$AppInstallerPath = "artifacts/win-x64/package/STFCModBridge.appinstaller"
)

$ErrorActionPreference = "Stop"
if ($Bucket -cnotmatch '^[a-z0-9][a-z0-9._-]{1,220}[a-z0-9]$') {
  throw "Bucket must be a canonical Google Cloud Storage bucket name."
}
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-rc\.\d+)?$') {
  throw "Version must use X.Y.Z or X.Y.Z-rc.N."
}
$channel = if ($Version.Contains("-rc.", [StringComparison]::Ordinal)) { "preview" } else { "stable" }
$baseUri = $UpdateBaseUri.TrimEnd('/')
$parsedBaseUri = $null
if (-not [Uri]::TryCreate($baseUri, [UriKind]::Absolute, [ref]$parsedBaseUri) `
    -or $parsedBaseUri.Scheme -ne [Uri]::UriSchemeHttps `
    -or -not [string]::IsNullOrEmpty($parsedBaseUri.Query) `
    -or -not [string]::IsNullOrEmpty($parsedBaseUri.Fragment)) {
  throw "UpdateBaseUri must be an absolute HTTPS URI without a query or fragment."
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$package = if ([System.IO.Path]::IsPathRooted($PackagePath)) {
  [System.IO.Path]::GetFullPath($PackagePath)
} else {
  [System.IO.Path]::GetFullPath((Join-Path $repoRoot $PackagePath))
}
$appInstaller = if ([System.IO.Path]::IsPathRooted($AppInstallerPath)) {
  [System.IO.Path]::GetFullPath($AppInstallerPath)
} else {
  [System.IO.Path]::GetFullPath((Join-Path $repoRoot $AppInstallerPath))
}
foreach ($path in @($package, $appInstaller)) {
  if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
    throw "Release hosting input was not found: $path"
  }
}
if (-not (Get-Command gcloud -ErrorAction SilentlyContinue)) {
  throw "The authenticated Google Cloud CLI is required."
}

$appInstallerUri = "$baseUri/$channel/STFCModBridge.appinstaller"
$packageUri = "$baseUri/packages/v$Version/STFCModBridge.msix"
$packageObject = "gs://$Bucket/packages/v$Version/STFCModBridge.msix"
$appInstallerObject = "gs://$Bucket/$channel/STFCModBridge.appinstaller"
[xml]$descriptor = Get-Content -Raw -LiteralPath $appInstaller
$namespace = [System.Xml.XmlNamespaceManager]::new($descriptor.NameTable)
$namespace.AddNamespace("a", $descriptor.DocumentElement.NamespaceURI)
$root = $descriptor.SelectSingleNode("/a:AppInstaller", $namespace)
$mainPackage = $descriptor.SelectSingleNode("/a:AppInstaller/a:MainPackage", $namespace)
if ($root.Uri -cne $appInstallerUri -or $mainPackage.Uri -cne $packageUri) {
  throw "The App Installer descriptor does not target the configured GCS channel and immutable package URLs."
}

function Invoke-Gcloud {
  param([Parameter(Mandatory = $true)] [string[]]$Arguments)
  & gcloud @Arguments
  if ($LASTEXITCODE -ne 0) {
    throw "gcloud exited with code $LASTEXITCODE."
  }
}

$expectedPackageHash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
$expectedDescriptorHash = (Get-FileHash -LiteralPath $appInstaller -Algorithm SHA256).Hash
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
  "stfc-mod-bridge-gcs-publish-" + [Guid]::NewGuid().ToString("N"))
try {
  [System.IO.Directory]::CreateDirectory($temporaryRoot)
  $existingPackage = Join-Path $temporaryRoot "existing.msix"
  & gcloud storage cp $packageObject $existingPackage --quiet 2>$null
  if ($LASTEXITCODE -eq 0) {
    $existingHash = (Get-FileHash -LiteralPath $existingPackage -Algorithm SHA256).Hash
    if ($existingHash -cne $expectedPackageHash) {
      throw "The immutable GCS package path already contains different bytes."
    }
    Write-Host "Immutable package already exists with the expected SHA-256; upload is idempotent."
  } else {
    Invoke-Gcloud @(
      "storage", "cp", $package, $packageObject,
      "--content-type=application/msix",
      "--cache-control=public,max-age=31536000,immutable",
      "--if-generation-match=0",
      "--quiet")
  }

  $publicPackage = Join-Path $temporaryRoot "public.msix"
  $packageHead = Invoke-WebRequest -Uri $packageUri -Method Head -UseBasicParsing
  $packageRange = Invoke-WebRequest `
    -Uri $packageUri `
    -Headers @{ Range = "bytes=0-1023" } `
    -UseBasicParsing
  if ($packageHead.StatusCode -ne 200 `
      -or $packageHead.Headers["Content-Type"] -notcontains "application/msix" `
      -or $packageRange.StatusCode -ne 206 `
      -or $packageRange.RawContentLength -ne 1024 `
      -or $packageRange.Headers["Content-Range"] -notmatch '^bytes 0-1023/\d+$') {
    throw "The public GCS package endpoint failed its MIME, length, or byte-range contract."
  }
  Invoke-WebRequest -Uri $packageUri -OutFile $publicPackage -UseBasicParsing
  if ((Get-FileHash -LiteralPath $publicPackage -Algorithm SHA256).Hash -cne $expectedPackageHash) {
    throw "The public GCS package bytes do not match the attested release package."
  }

  $existingDescriptor = Join-Path $temporaryRoot "existing.appinstaller"
  try {
    $existingDescriptorResponse = Invoke-WebRequest `
      -Uri $appInstallerUri `
      -OutFile $existingDescriptor `
      -UseBasicParsing
  } catch {
    if ($_.Exception.Response.StatusCode -ne 404) {
      throw
    }
    $existingDescriptorResponse = $null
  }
  if ($existingDescriptorResponse) {
    [xml]$existingDescriptorXml = Get-Content -Raw -LiteralPath $existingDescriptor
    $existingNamespace = [System.Xml.XmlNamespaceManager]::new($existingDescriptorXml.NameTable)
    $existingNamespace.AddNamespace("a", $existingDescriptorXml.DocumentElement.NamespaceURI)
    $existingRoot = $existingDescriptorXml.SelectSingleNode("/a:AppInstaller", $existingNamespace)
    if ([version]$existingRoot.Version -gt [version]$root.Version) {
      throw "The published $channel App Installer version is newer; refusing a channel downgrade."
    }
  }

  Invoke-Gcloud @(
    "storage", "cp", $appInstaller, $appInstallerObject,
    "--content-type=application/appinstaller",
    "--cache-control=no-cache,no-store,must-revalidate",
    "--quiet")

  $descriptorHead = Invoke-WebRequest -Uri $appInstallerUri -Method Head -UseBasicParsing
  $publishedDescriptor = Join-Path $temporaryRoot "published.appinstaller"
  Invoke-WebRequest -Uri $appInstallerUri -OutFile $publishedDescriptor -UseBasicParsing
  if ($descriptorHead.StatusCode -ne 200 `
      -or $descriptorHead.Headers["Content-Type"] -notcontains "application/appinstaller" `
      -or (Get-FileHash -LiteralPath $publishedDescriptor -Algorithm SHA256).Hash -cne $expectedDescriptorHash) {
    throw "The public GCS App Installer pointer does not match the released descriptor."
  }
} finally {
  if (Test-Path -LiteralPath $temporaryRoot) {
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
  }
}

Write-Host "Published immutable MSIX: $packageUri"
Write-Host "Advanced $channel App Installer channel: $appInstallerUri"
