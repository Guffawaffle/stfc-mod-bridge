[CmdletBinding()]
param(
  [string]$OutputDirectory = "artifacts/toml-native",
  [string]$SourceDirectory = ""
)
$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$pin = Get-Content -LiteralPath (Join-Path $repoRoot "dependencies/stfc-toml-source-pin.json") -Raw | ConvertFrom-Json
if ($pin.schemaVersion -ne 1 -or $pin.repository -cne "Guffawaffle/stfc-mod" -or $pin.revision -cnotmatch '^[0-9a-f]{40}$' `
    -or $pin.sourceArchiveSha256 -cnotmatch '^[0-9a-f]{64}$' -or $pin.sourceSubdirectory -cne "shared/toml") {
  throw "The shared TOML dependency requires an exact reviewed source pin."
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
  if (-not [IO.Path]::IsPathFullyQualified($SourceDirectory)) { throw "The explicit TOML source override must be an absolute existing checkout." }
  $sourceRoot = (Resolve-Path -LiteralPath $SourceDirectory).Path
  $revision = (& git -c "safe.directory=$($sourceRoot.Replace('\', '/'))" -C $sourceRoot rev-parse HEAD).Trim()
  if ($LASTEXITCODE -ne 0) { throw "Cannot identify the explicit TOML development source." }
  $dirty = @(& git -c "safe.directory=$($sourceRoot.Replace('\', '/'))" -C $sourceRoot status --porcelain --untracked-files=all)
  if ($LASTEXITCODE -ne 0) { throw "Cannot record the explicit TOML development source state." }
} else {
  if ($pin.qualificationState -cne "immutable" -or $pin.sourceArchiveSha256 -ceq ('0' * 64)) {
    throw "The TOML source pin is awaiting immutable producer qualification. Use an explicit source override only for development."
  }
  $downloadRoot = Join-Path $outputRoot ("source-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
  New-Item -ItemType Directory -Path $downloadRoot | Out-Null
  $archive = Join-Path $downloadRoot "source.tar.gz"
  Invoke-WebRequest -Uri "https://codeload.github.com/$($pin.repository)/tar.gz/$($pin.revision)" -OutFile $archive
  $archiveDigest = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
  if ($archiveDigest -cne $pin.sourceArchiveSha256) {
    throw "The TOML source archive SHA-256 differs from the reviewed immutable pin."
  }
  & tar -xf $archive --strip-components=1 -C $downloadRoot
  if ($LASTEXITCODE -ne 0) { throw "The pinned TOML source archive could not be extracted." }
  # Omit the archive's redundant commit-named directory so locked-recipe Git paths stay within Windows path limits.
  $sourceRoot = $downloadRoot
}
$componentRoot = Join-Path $sourceRoot $pin.sourceSubdirectory
if (-not (Test-Path -LiteralPath (Join-Path $componentRoot "include/stfc_toml/c_api.h") -PathType Leaf)) {
  throw "The exact TOML source does not implement the required offline native ABI."
}
$recipeLockPath = Join-Path $componentRoot "xmake-requires.lock"
if (-not (Test-Path -LiteralPath $recipeLockPath -PathType Leaf)) {
  throw "The shared TOML source must include its immutable native recipe lock."
}
$recipeLockOriginal = [IO.File]::ReadAllBytes($recipeLockPath)
$xmake = Get-Command xmake -ErrorAction Stop
$xmakeVersion = [Regex]::Replace((& $xmake.Source --version | Select-Object -First 1), "`e\[[0-9;]*m", "")
if ($xmakeVersion -notmatch ("^xmake v" + [Regex]::Escape($pin.xmakeVersion) + "(?:[+ ,]|$)")) {
  throw "Shared TOML must be built with the pinned XMake version $($pin.xmakeVersion)."
}
$buildRoot = Join-Path $outputRoot "build"
Push-Location $componentRoot
try {
  & $xmake.Source f -P $componentRoot -y -p windows -a x64 -m release -o $buildRoot
  if ($LASTEXITCODE -ne 0) { throw "Shared TOML native configuration failed." }
  # Reinstall only the two header inputs from the locked source recipes. Existing
  # build-tool dependencies are retained; foreign prebuilt header cache provenance
  # must not stand in for this component's selected recipe inputs.
  & $xmake.Source require -P $componentRoot --force --build --shallow -y "toml++ v3.4.0" "nlohmann_json 3.12.0"
  if ($LASTEXITCODE -ne 0) { throw "Pinned shared TOML header source installation failed." }
  & $xmake.Source build -P $componentRoot -y stfc-toml-native
  if ($LASTEXITCODE -ne 0) { throw "Shared TOML native build failed." }
} finally { Pop-Location }
$recipeLockAfter = [IO.File]::ReadAllBytes($recipeLockPath)
if (-not [System.Linq.Enumerable]::SequenceEqual[byte]($recipeLockOriginal, $recipeLockAfter)) {
  $strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
  $beforeText = $strictUtf8.GetString($recipeLockOriginal).TrimEnd([char[]]"`r`n")
  $afterText = $strictUtf8.GetString($recipeLockAfter).TrimEnd([char[]]"`r`n")
  if ($beforeText -cne $afterText) {
    throw "The pinned TOML recipe lock changed during the build. No native output qualifies."
  }
  # XMake can omit its final newline; preserve source bytes when recipes match.
  [IO.File]::WriteAllBytes($recipeLockPath, $recipeLockOriginal)
}
$built = Join-Path $buildRoot "windows/x64/release/stfc-toml-native.dll"
if (-not (Test-Path -LiteralPath $built -PathType Leaf)) { throw "The shared TOML native target did not produce its exact DLL." }
$nativePath = Join-Path $outputRoot "stfc-toml-native.dll"
Copy-Item -LiteralPath $built -Destination $nativePath -Force
$receipt = [ordered]@{
  schemaVersion = 1
  repository = $pin.repository
  sourceRevision = $revision
  sourceSubdirectory = $pin.sourceSubdirectory
  developmentOverride = $developmentOverride
  sourceDirectory = $(if ($developmentOverride) { $sourceRoot } else { $null })
  dirtyFiles = $dirty
  sourceArchiveSha256 = $archiveDigest
  xmakeVersion = $xmakeVersion
  target = "windows-x64-release"
  nativeSha256 = (Get-FileHash -LiteralPath $nativePath -Algorithm SHA256).Hash.ToLowerInvariant()
}
[IO.File]::WriteAllText((Join-Path $outputRoot "build-receipt.json"), ($receipt | ConvertTo-Json -Depth 5) + "`n", [Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path $sourceRoot "LICENSE") -Destination (Join-Path $outputRoot "STFC-TOML-LICENSE.txt") -Force
[pscustomobject]@{ NativePath = $nativePath; ReceiptPath = (Join-Path $outputRoot "build-receipt.json") }
