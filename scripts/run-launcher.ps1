[CmdletBinding()]
param([string]$ProfilesSourceDirectory = "")
$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if (-not $env:WINDIR) {
  if (-not $env:SystemRoot) { throw "Neither WINDIR nor SystemRoot is available." }
  $env:WINDIR = $env:SystemRoot
}
Write-Host "LOCAL DOGFOOD BUILD - unsigned and unpackaged"
Write-Host "Close any installed Mod Bridge window before continuing."
Write-Host "This build shares your normal Bridge state and operation lock."
$nativeBuild = & (Join-Path $PSScriptRoot "build-profiles-native.ps1") `
  -OutputDirectory (Join-Path $repoRoot "artifacts/local-profiles-native") -SourceDirectory $ProfilesSourceDirectory
$native = @($nativeBuild)[-1].NativePath
$digest = (Get-FileHash -LiteralPath $native -Algorithm SHA256).Hash.ToLowerInvariant()
$project = Join-Path $repoRoot "src/STFCCommunityMod.Launcher/STFCCommunityMod.Launcher.csproj"
& dotnet build $project -c Release -r win-x64 --self-contained true --nologo "-p:ProfilesNativeSha256=$digest"
if ($LASTEXITCODE -ne 0) { throw "The current Bridge source did not build. Nothing was launched." }
$launcherDirectory = Join-Path $repoRoot "src/STFCCommunityMod.Launcher/bin/Release/net8.0-windows10.0.19041.0/win-x64"
Copy-Item -LiteralPath $native -Destination (Join-Path $launcherDirectory "stfc-profiles-native.dll") -Force
$launcher = Join-Path $launcherDirectory "STFCModBridge.exe"
if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) { throw "The current launcher executable was not produced." }
Start-Process -FilePath $launcher -WorkingDirectory $launcherDirectory
