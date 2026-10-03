[CmdletBinding()]
param(
  [string]$OutputDirectory = "artifacts/profiles-native",
  [string]$SourceDirectory = ""
)
$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$pin = Get-Content -LiteralPath (Join-Path $repoRoot "dependencies/stfc-profiles-source-pin.json") -Raw | ConvertFrom-Json
if ($pin.schemaVersion -ne 1 -or $pin.repository -cne "Guffawaffle/stfc-profiles" -or $pin.revision -cnotmatch '^[0-9a-f]{40}$' `
    -or $pin.sourceArchiveSha256 -cnotmatch '^[0-9a-f]{64}$') {
  throw "The shared profiles dependency requires an exact reviewed source pin."
}
$outputRoot = if ([IO.Path]::IsPathFullyQualified($OutputDirectory)) {
  [IO.Path]::GetFullPath($OutputDirectory)
} else { [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory)) }
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$developmentOverride = -not [string]::IsNullOrWhiteSpace($SourceDirectory)
$revision = $pin.revision
$dirty = @()
$archiveDigest = $null
if ($developmentOverride) {
  if (-not [IO.Path]::IsPathFullyQualified($SourceDirectory)) { throw "The explicit source override must be an absolute existing checkout." }
  $sourceRoot = (Resolve-Path -LiteralPath $SourceDirectory).Path
  $revision = (& git -c "safe.directory=$($sourceRoot.Replace('\', '/'))" -C $sourceRoot rev-parse HEAD).Trim()
  if ($LASTEXITCODE -ne 0) { throw "Cannot identify the explicit profiles development source." }
  $dirty = @(& git -c "safe.directory=$($sourceRoot.Replace('\', '/'))" -C $sourceRoot status --porcelain)
  if ($LASTEXITCODE -ne 0) { throw "Cannot record the explicit profiles development source state." }
} else {
  $downloadRoot = Join-Path $outputRoot ("source-" + [Guid]::NewGuid().ToString("N"))
  New-Item -ItemType Directory -Path $downloadRoot | Out-Null
  $archive = Join-Path $downloadRoot "source.tar.gz"
  Invoke-WebRequest -Uri "https://codeload.github.com/$($pin.repository)/tar.gz/$($pin.revision)" -OutFile $archive
  $archiveDigest = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
  if ($archiveDigest -cne $pin.sourceArchiveSha256) {
    throw "The profiles source archive SHA-256 differs from the reviewed immutable pin."
  }
  & tar -xf $archive -C $downloadRoot
  if ($LASTEXITCODE -ne 0) { throw "The pinned profiles source archive could not be extracted." }
  $sourceRoot = Join-Path $downloadRoot "stfc-profiles-$($pin.revision)"
}
if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot "include/stfc_profiles/c_api.h") -PathType Leaf)) {
  throw "The exact profiles source does not implement the required native catalog ABI."
}
$xmake = Get-Command xmake -ErrorAction Stop
$xmakeVersion = [Regex]::Replace((& $xmake.Source --version | Select-Object -First 1), "`e\[[0-9;]*m", "")
if ($xmakeVersion -notmatch ("^xmake v" + [Regex]::Escape($pin.xmakeVersion) + "(?:[+ ,]|$)")) {
  throw "Shared profiles must be built with the pinned XMake version $($pin.xmakeVersion)."
}
$buildRoot = Join-Path $outputRoot "build"
Push-Location $sourceRoot
try {
  & $xmake.Source f -y -p windows -a x64 -m release --buildir=$buildRoot
  if ($LASTEXITCODE -ne 0) { throw "Shared profiles native configuration failed." }
  & $xmake.Source build -y stfc-profiles-native
  if ($LASTEXITCODE -ne 0) { throw "Shared profiles native build failed." }
} finally { Pop-Location }
$built = Join-Path $buildRoot "windows/x64/release/stfc-profiles-native.dll"
if (-not (Test-Path -LiteralPath $built -PathType Leaf)) { throw "The shared native target did not produce its exact DLL." }
$nativePath = Join-Path $outputRoot "stfc-profiles-native.dll"
Copy-Item -LiteralPath $built -Destination $nativePath -Force
$receipt = [ordered]@{
  schemaVersion = 1
  repository = $pin.repository
  sourceRevision = $revision
  developmentOverride = $developmentOverride
  sourceDirectory = $(if ($developmentOverride) { $sourceRoot } else { $null })
  dirtyFiles = $dirty
  sourceArchiveSha256 = $archiveDigest
  xmakeVersion = $xmakeVersion
  target = "windows-x64-release"
  nativeSha256 = (Get-FileHash -LiteralPath $nativePath -Algorithm SHA256).Hash.ToLowerInvariant()
}
[IO.File]::WriteAllText((Join-Path $outputRoot "build-receipt.json"), ($receipt | ConvertTo-Json -Depth 5) + "`n", [Text.UTF8Encoding]::new($false))
# Native GPL source is pinned above; preserve its exact license with the build evidence.
Copy-Item -LiteralPath (Join-Path $sourceRoot "LICENSE") -Destination (Join-Path $outputRoot "STFC-Profiles-LICENSE.txt") -Force
[pscustomobject]@{ NativePath = $nativePath; ReceiptPath = (Join-Path $outputRoot "build-receipt.json") }
