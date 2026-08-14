param(
  [string]$OutputDirectory = "artifacts/release-verifier",
  [switch]$RunVulnerabilityAudit
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$moduleRoot = Join-Path $repositoryRoot "src/STFCModBridge.ReleaseVerifier"
$inventoryPath = Join-Path $moduleRoot "dependencies.v1.txt"
$licenseInventoryPath = Join-Path $moduleRoot "licenses.v1.json"
$expectedGoVersion = "go1.26.6"
$expectedSigstoreVersion = "v1.3.0"

$actualGoVersion = (& go env GOVERSION).Trim()
if ($LASTEXITCODE -ne 0 -or $actualGoVersion -ne $expectedGoVersion) {
  throw "Release verifier requires Go $expectedGoVersion; found '$actualGoVersion'."
}

Push-Location $moduleRoot
try {
  & go mod tidy -diff
  if ($LASTEXITCODE -ne 0) {
    throw "Release verifier go.mod/go.sum are not tidy."
  }

  & go test ./...
  if ($LASTEXITCODE -ne 0) {
    throw "Release verifier tests failed."
  }

  if ($RunVulnerabilityAudit) {
    & go run golang.org/x/vuln/cmd/govulncheck@v1.6.0 -show verbose ./...
    if ($LASTEXITCODE -ne 0) {
      throw "Release verifier reachable-vulnerability audit failed."
    }
  }

  $actualInventory = @(& go list -deps -f '{{with .Module}}{{if not .Main}}{{.Path}} {{.Version}} {{.Sum}}{{end}}{{end}}' .) `
    | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } `
    | Sort-Object -Unique
  if ($LASTEXITCODE -ne 0) {
    throw "Release verifier module inventory could not be resolved."
  }
  $expectedInventory = @(Get-Content -LiteralPath $inventoryPath) `
    | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
  if ([string]::Join("`n", $actualInventory) -cne [string]::Join("`n", $expectedInventory)) {
    throw "Release verifier dependency inventory is stale; review and regenerate dependencies.v1.txt."
  }
  if (($actualInventory | Where-Object { $_ -like "github.com/sigstore/sigstore-go *" }) `
      -cne "github.com/sigstore/sigstore-go $expectedSigstoreVersion h1:hnIMHREyCNTYFtOE1o7ae3Axa9B5W5EjUSBJICP2NBE=") {
    throw "Release verifier is not locked to the reviewed sigstore-go version and module checksum."
  }
  $licenseInventory = Get-Content -Raw -LiteralPath $licenseInventoryPath | ConvertFrom-Json
  $inventorySha256 = (Get-FileHash -LiteralPath $inventoryPath -Algorithm SHA256).Hash.ToLowerInvariant()
  if ($licenseInventory.schemaVersion -ne 1 `
      -or $licenseInventory.dependencyInventorySha256 -cne $inventorySha256) {
    throw "Release verifier license classification is not bound to the exact dependency inventory."
  }
  $allowedLicenses = @("Apache-2.0", "MIT", "BSD-2-Clause", "BSD-3-Clause", "MIT AND Apache-2.0")
  $classifiedModules = @()
  foreach ($classification in @($licenseInventory.classifications)) {
    if ($classification.spdxExpression -cnotin $allowedLicenses `
        -or @($classification.modules).Count -eq 0) {
      throw "Release verifier license classification contains an unsupported or empty group."
    }
    $classifiedModules += @($classification.modules)
  }
  $expectedModules = @($expectedInventory | ForEach-Object { $_.Split(' ', 2)[0] } | Sort-Object)
  $classifiedModules = @($classifiedModules | Sort-Object)
  if ($classifiedModules.Count -ne ($classifiedModules | Sort-Object -Unique).Count `
      -or [string]::Join("`n", $classifiedModules) -cne [string]::Join("`n", $expectedModules)) {
    throw "Release verifier license classification does not cover the exact compiled module graph."
  }
  $aboutCatalog = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot "docs/windows-launcher/about-content.v1.json") `
    | ConvertFrom-Json
  $goInventory = @($aboutCatalog.dependencyInventory | Where-Object { $_.evidenceKind -like "go-build-*" })
  $hasToolchainInventory = $null -ne ($goInventory | Where-Object {
      $_.evidenceKind -eq "go-build-toolchain" -and $_.version -eq "1.26.6"
    })
  $hasSigstoreInventory = $null -ne ($goInventory | Where-Object {
      $_.evidenceKind -eq "go-build-module" `
        -and $_.id -eq "github.com/sigstore/sigstore-go" `
        -and $_.version -eq "1.3.0"
    })
  $hasGraphInventory = $null -ne ($goInventory | Where-Object {
      $_.evidenceKind -eq "go-build-graph" `
        -and $_.version -eq "$($actualInventory.Count) exact modules in dependencies.v1.txt"
    })
  if ($goInventory.Count -ne 3 -or -not $hasToolchainInventory -or -not $hasSigstoreInventory -or -not $hasGraphInventory) {
    throw "Release verifier notice inventory does not match the reviewed toolchain and compiled module graph."
  }

  $resolvedOutput = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    [System.IO.Path]::GetFullPath($OutputDirectory)
  } else {
    [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
  }
  New-Item -ItemType Directory -Path $resolvedOutput -Force | Out-Null
  $binaryPath = Join-Path $resolvedOutput "STFCModBridge.ReleaseVerifier.exe"
  $priorCgo = $env:CGO_ENABLED
  $priorGoos = $env:GOOS
  $priorGoarch = $env:GOARCH
  try {
    $env:CGO_ENABLED = "0"
    $env:GOOS = "windows"
    $env:GOARCH = "amd64"
    & go build -trimpath -ldflags "-s -w -buildid=" -o $binaryPath .
    if ($LASTEXITCODE -ne 0) {
      throw "Release verifier build failed."
    }
  }
  finally {
    $env:CGO_ENABLED = $priorCgo
    $env:GOOS = $priorGoos
    $env:GOARCH = $priorGoarch
  }

  $buildInfo = & go version -m $binaryPath
  if ($LASTEXITCODE -ne 0 `
      -or $buildInfo -notcontains "$binaryPath`: $expectedGoVersion" `
      -or -not ($buildInfo | Select-String -SimpleMatch "dep`tgithub.com/sigstore/sigstore-go`t$expectedSigstoreVersion`t")) {
    throw "Release verifier binary does not carry the reviewed Go/sigstore-go build identity."
  }

  $finalSbomPath = Join-Path $resolvedOutput "STFCModBridge.ReleaseVerifier.spdx.json"
  $sourceRevision = (& git -C $repositoryRoot rev-parse HEAD).Trim()
  & (Join-Path $PSScriptRoot "generate-release-verifier-sbom.ps1") `
    -BinaryDirectory $resolvedOutput `
    -OutputPath $finalSbomPath `
    -SourceRevisionId $sourceRevision

  $source = @(Get-ChildItem -LiteralPath $moduleRoot -Filter "*.go" -File `
      | Where-Object { $_.Name -notlike "*_test.go" } `
      | Sort-Object Name `
      | ForEach-Object { Get-Content -Raw -LiteralPath $_.FullName }) -join "`n"
  foreach ($forbiddenImport in @('"net"', '"net/http"', '"os/exec"', '"syscall"', '"plugin"')) {
    if ($source.Contains($forbiddenImport, [StringComparison]::Ordinal)) {
      throw "Release verifier source imports forbidden runtime capability $forbiddenImport."
    }
  }

  Write-Host "PASS: release verifier toolchain, module lock, tests, source capability boundary, Windows build, and SPDX inventory agree."
}
finally {
  Pop-Location
}
