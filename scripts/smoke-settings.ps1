[CmdletBinding()]
param(
  [Parameter(Mandatory)]
  [string]$LauncherPath,

  [ValidateRange(5, 120)]
  [int]$TimeoutSeconds = 30,

  [switch]$UseDisposableSyncFixture,

  [switch]$AllowInteractiveFocus
)

$ErrorActionPreference = "Stop"

if (-not $AllowInteractiveFocus -and $env:CI -ne "true") {
  throw "This UI Automation smoke launches and focuses Mod Control. Run it in CI or pass -AllowInteractiveFocus when the interactive desktop is available."
}

function Get-ActiveConfigurationPath {
  $localApplicationData = [Environment]::GetFolderPath(
    [Environment+SpecialFolder]::LocalApplicationData)
  if ([string]::IsNullOrWhiteSpace($localApplicationData)) {
    return $null
  }

  $selectionPath = Join-Path `
    $localApplicationData `
    "STFC Community Mod Launcher\install-selection.json"
  if (-not (Test-Path -LiteralPath $selectionPath -PathType Leaf)) {
    return $null
  }

  try {
    $selection = Get-Content -LiteralPath $selectionPath -Raw |
      ConvertFrom-Json -ErrorAction Stop
    if ($selection.schemaVersion -ne 1 -or
        [string]::IsNullOrWhiteSpace([string]$selection.gameDirectory)) {
      return $null
    }

    $configurationPath = Join-Path `
      ([string]$selection.gameDirectory) `
      "community_patch_settings.toml"
    if (Test-Path -LiteralPath $configurationPath -PathType Leaf) {
      return [System.IO.Path]::GetFullPath($configurationPath)
    }
  }
  catch {
    Write-Verbose "The saved game selection could not be read: $($_.Exception.Message)"
  }

  return $null
}

function Wait-ForResponsiveMainWindow {
  param(
    [Parameter(Mandatory)]
    [System.Diagnostics.Process]$Process,

    [Parameter(Mandatory)]
    [DateTimeOffset]$Deadline
  )

  while ([DateTimeOffset]::UtcNow -lt $Deadline) {
    $Process.Refresh()
    if ($Process.HasExited) {
      throw "The launcher exited with code $($Process.ExitCode) before creating its main window."
    }

    if ($Process.MainWindowHandle -ne [IntPtr]::Zero -and $Process.Responding) {
      try {
        [void]$Process.WaitForInputIdle(5000)
      }
      catch [InvalidOperationException] {
        # A responsive HWND is sufficient if the process does not expose an
        # input-idle wait handle.
      }

      $Process.Refresh()
      if ($Process.MainWindowHandle -ne [IntPtr]::Zero -and $Process.Responding) {
        return $Process.MainWindowHandle
      }
    }

    Start-Sleep -Milliseconds 100
  }

  throw "The launcher did not create a responsive main window within the timeout."
}

function Find-AutomationElement {
  param(
    [Parameter(Mandatory)]
    [System.Windows.Automation.AutomationElement]$Root,

    [Parameter(Mandatory)]
    [string]$Name,

    [System.Windows.Automation.ControlType]$ControlType,

    [Parameter(Mandatory)]
    [DateTimeOffset]$Deadline
  )

  while ([DateTimeOffset]::UtcNow -lt $Deadline) {
    try {
      $elements = $Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    }
    catch [System.Runtime.InteropServices.COMException] {
      Start-Sleep -Milliseconds 100
      continue
    }
    foreach ($element in $elements) {
      if ($element.Current.Name -ne $Name) {
        continue
      }

      if ($null -eq $ControlType -or
          $element.Current.ControlType -eq $ControlType) {
        return $element
      }
    }

    Start-Sleep -Milliseconds 100
  }

  $typeDescription = if ($null -eq $ControlType) {
    "element"
  }
  else {
    $ControlType.ProgrammaticName
  }
  throw "UI Automation could not find $typeDescription '$Name' within the timeout."
}

function Find-ColorModeSelector {
  param(
    [Parameter(Mandatory)]
    [System.Windows.Automation.AutomationElement]$Root,

    [Parameter(Mandatory)]
    [DateTimeOffset]$Deadline
  )

  while ([DateTimeOffset]::UtcNow -lt $Deadline) {
    $elements = $Root.FindAll(
      [System.Windows.Automation.TreeScope]::Descendants,
      [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($element in $elements) {
      if ($element.Current.ControlType -eq
          [System.Windows.Automation.ControlType]::ComboBox -and
          $element.Current.Name -match
          '^Launcher color mode, (System|Light|Dark)$') {
        return [pscustomobject]@{
          Element = $element
          SelectedMode = $Matches[1]
        }
      }
    }

    Start-Sleep -Milliseconds 100
  }

  throw "UI Automation did not expose a Launcher color mode ComboBox with a selected System, Light, or Dark value."
}

function Test-ColorModeSelectorPresent {
  param(
    [Parameter(Mandatory)]
    [System.Windows.Automation.AutomationElement]$Root
  )

  $elements = $Root.FindAll(
    [System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)
  foreach ($element in $elements) {
    if ($element.Current.ControlType -eq
        [System.Windows.Automation.ControlType]::ComboBox -and
        $element.Current.Name -match
        '^Launcher color mode, (System|Light|Dark)$') {
      return $true
    }
  }

  return $false
}

function Invoke-AutomationElement {
  param(
    [Parameter(Mandatory)]
    [System.Windows.Automation.AutomationElement]$Element
  )

  $pattern = $null
  if (-not $Element.TryGetCurrentPattern(
      [System.Windows.Automation.InvokePattern]::Pattern,
      [ref]$pattern)) {
    throw "UI Automation element '$($Element.Current.Name)' does not support Invoke."
  }

  $pattern.Invoke()
}

function Send-AutomationKeyAndWaitForFocus {
  param(
    [Parameter(Mandatory)]
    [System.Windows.Automation.AutomationElement]$Element,

    [Parameter(Mandatory)]
    [string]$Keys,

    [Parameter(Mandatory)]
    [string]$ExpectedNamePattern,

    [Parameter(Mandatory)]
    [DateTimeOffset]$Deadline
  )

  $Element.SetFocus()
  [System.Windows.Forms.SendKeys]::SendWait($Keys)
  while ([DateTimeOffset]::UtcNow -lt $Deadline) {
    $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
    if ($null -ne $focused -and $focused.Current.Name -match $ExpectedNamePattern) {
      return $focused
    }

    Start-Sleep -Milliseconds 50
  }

  $actualName = [System.Windows.Automation.AutomationElement]::FocusedElement.Current.Name
  throw "Keyboard input '$Keys' did not move focus to '$ExpectedNamePattern'; focus remained '$actualName'."
}

function Assert-AutomationToggleOn {
  param(
    [Parameter(Mandatory)]
    [System.Windows.Automation.AutomationElement]$Element
  )

  $togglePattern = $null
  if (-not $Element.TryGetCurrentPattern(
      [System.Windows.Automation.TogglePattern]::Pattern,
      [ref]$togglePattern) -or
      $togglePattern.Current.ToggleState -ne
        [System.Windows.Automation.ToggleState]::On) {
    throw "UI Automation element '$($Element.Current.Name)' has focus but is not the selected tab."
  }
}

function Wait-ForHorizontalScrollChange {
  param(
    [Parameter(Mandatory)]
    [System.Windows.Automation.ScrollPattern]$ScrollPattern,

    [Parameter(Mandatory)]
    [double]$PreviousPercent,

    [Parameter(Mandatory)]
    [ValidateSet("Increase", "Decrease")]
    [string]$Direction,

    [Parameter(Mandatory)]
    [DateTimeOffset]$Deadline
  )

  while ([DateTimeOffset]::UtcNow -lt $Deadline) {
    $current = $ScrollPattern.Current.HorizontalScrollPercent
    if (($Direction -eq "Increase" -and $current -gt $PreviousPercent) -or
        ($Direction -eq "Decrease" -and $current -lt $PreviousPercent)) {
      return $current
    }

    Start-Sleep -Milliseconds 50
  }

  throw "Destination tab overflow did not $($Direction.ToLowerInvariant()) from $PreviousPercent percent."
}

function Stop-OwnedProcess {
  param(
    [System.Diagnostics.Process]$Process
  )

  if ($null -eq $Process) {
    return
  }

  try {
    $Process.Refresh()
    if ($Process.HasExited) {
      return
    }

    [void]$Process.CloseMainWindow()
    if (-not $Process.WaitForExit(5000)) {
      $Process.Kill()
      [void]$Process.WaitForExit(5000)
    }
  }
  catch [InvalidOperationException] {
    # The exact process started by this script exited between checks.
  }
}

function Restore-DisposableFixture {
  param(
    [bool]$SelectionExisted,
    [AllowNull()][byte[]]$SelectionBytes,
    [string]$SelectionPath,
    [AllowNull()][string]$DisposableGameDirectory
  )

  if ($SelectionExisted) {
    [System.IO.File]::WriteAllBytes($SelectionPath, $SelectionBytes)
  }
  elseif (Test-Path -LiteralPath $SelectionPath -PathType Leaf) {
    [System.IO.File]::Delete($SelectionPath)
  }

  if ($null -ne $DisposableGameDirectory -and
      $DisposableGameDirectory.StartsWith(
        [System.IO.Path]::GetTempPath(),
        [StringComparison]::OrdinalIgnoreCase) -and
      (Split-Path -Leaf $DisposableGameDirectory).StartsWith(
        "stfc-launcher-sync-smoke-",
        [StringComparison]::Ordinal) -and
      (Test-Path -LiteralPath $DisposableGameDirectory -PathType Container)) {
    [System.IO.Directory]::Delete($DisposableGameDirectory, $true)
  }
}

$resolvedLauncherPath = (Resolve-Path -LiteralPath $LauncherPath).Path
if (-not (Test-Path -LiteralPath $resolvedLauncherPath -PathType Leaf) -or
    [System.IO.Path]::GetExtension($resolvedLauncherPath) -ne ".exe") {
  throw "LauncherPath must identify an existing .exe file: $LauncherPath"
}

$runtimeManifestPath = (
  Resolve-Path -LiteralPath (
    Join-Path `
      $PSScriptRoot `
      "..\docs\windows-launcher\runtime-manifest.guffawaffle.v1.json")
).Path
$runtimeManifest = Get-Content -LiteralPath $runtimeManifestPath -Raw |
  ConvertFrom-Json -ErrorAction Stop
$expectedRuntimeIdentity = "Guffawaffle $($runtimeManifest.runtimeVersion)"

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

$originalWindir = [Environment]::GetEnvironmentVariable("WINDIR", "Process")
$windirWasRestored = $false
if ([string]::IsNullOrWhiteSpace($originalWindir)) {
  if ([string]::IsNullOrWhiteSpace($env:SystemRoot)) {
    throw "WINDIR is missing and SystemRoot is unavailable, so WPF cannot be started safely."
  }

  [Environment]::SetEnvironmentVariable("WINDIR", $env:SystemRoot, "Process")
  $windirWasRestored = $true
  Write-Verbose "Restored process WINDIR from SystemRoot for the launcher smoke."
}

$selectionPath = Join-Path `
  ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) `
  "STFC Community Mod Launcher\install-selection.json"
$selectionExisted = Test-Path -LiteralPath $selectionPath -PathType Leaf
$selectionBytes = if ($selectionExisted) {
  [System.IO.File]::ReadAllBytes($selectionPath)
} else {
  $null
}
$disposableGameDirectory = $null
try {
  if ($UseDisposableSyncFixture) {
    $disposableGameDirectory = Join-Path `
      ([System.IO.Path]::GetTempPath()) `
      "stfc-launcher-sync-smoke-$([Guid]::NewGuid().ToString('N'))"
    [void](New-Item -ItemType Directory -Path $disposableGameDirectory)
    [System.IO.File]::WriteAllBytes(
      (Join-Path $disposableGameDirectory "prime.exe"),
      [byte[]]::new(0))
    [System.IO.File]::WriteAllText(
      (Join-Path $disposableGameDirectory "community_patch_settings.toml"),
      @"
[sync]
jobs = true

[sidecar.sync]
enabled = false

[sync.targets.community]
url = "https://community.example.invalid/sync"
token = "disposable-smoke-secret"

[sync.targets.alpha]
url = "https://alpha.example.invalid/sync"
token = "disposable-alpha-secret"

[sync.targets.bravo]
url = "https://bravo.example.invalid/sync"
token = "disposable-bravo-secret"

[sync.targets.charlie]
url = "https://charlie.example.invalid/sync"
token = "disposable-charlie-secret"

[sync.targets.delta]
url = "https://delta.example.invalid/sync"
token = "disposable-delta-secret"

[sync.targets.echo]
url = "https://echo.example.invalid/sync"
token = "disposable-echo-secret"

[sync.targets.foxtrot]
url = "https://foxtrot.example.invalid/sync"
token = "disposable-foxtrot-secret"
"@,
      [System.Text.UTF8Encoding]::new($false))
    [void](New-Item -ItemType Directory -Path (Split-Path -Parent $selectionPath) -Force)
    $selectionDocument = [ordered]@{
      schemaVersion = 1
      gameDirectory = $disposableGameDirectory
      confirmedAtUtc = [DateTimeOffset]::UtcNow
    }
    [System.IO.File]::WriteAllText(
      $selectionPath,
      ($selectionDocument | ConvertTo-Json),
      [System.Text.UTF8Encoding]::new($false))
    Write-Host "Using disposable Sync fixture: $disposableGameDirectory"
  }

  $configurationPath = Get-ActiveConfigurationPath
  $configurationHashBefore = $null
  if ($null -ne $configurationPath) {
    $configurationHashBefore = (
      Get-FileHash -LiteralPath $configurationPath -Algorithm SHA256
    ).Hash
    Write-Host "Guarding active TOML: $configurationPath"
  }
  else {
    Write-Host "No active game TOML was discoverable; continuing with read-only UI checks."
  }
}
catch {
  if ($UseDisposableSyncFixture) {
    Restore-DisposableFixture `
      -SelectionExisted $selectionExisted `
      -SelectionBytes $selectionBytes `
      -SelectionPath $selectionPath `
      -DisposableGameDirectory $disposableGameDirectory
  }
  throw
}

$launcherProcess = $null
$smokeFailure = $null
$integrityFailure = $null

try {
  $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
  $startInfo.FileName = $resolvedLauncherPath
  $startInfo.WorkingDirectory = Split-Path -Parent $resolvedLauncherPath
  $startInfo.UseShellExecute = $false
  $startInfo.Environment["WINDIR"] = [Environment]::GetEnvironmentVariable(
    "WINDIR",
    "Process")

  $launcherProcess = [System.Diagnostics.Process]::Start($startInfo)
  if ($null -eq $launcherProcess) {
    throw "Windows did not return a process for the launcher."
  }

  Write-Host "Started launcher process $($launcherProcess.Id)."
  $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
  $windowHandle = Wait-ForResponsiveMainWindow `
    -Process $launcherProcess `
    -Deadline $deadline
  $root = [System.Windows.Automation.AutomationElement]::FromHandle($windowHandle)
  if ($null -eq $root -or $root.Current.ProcessId -ne $launcherProcess.Id) {
    throw "UI Automation did not attach to the exact launcher process that was started."
  }

  if ($root.Current.Name -ne "STFC Mod Control") {
    throw "Unexpected launcher window title '$($root.Current.Name)'."
  }

  [void](Find-AutomationElement `
    -Root $root `
    -Name "Minimize Mod Control" `
    -ControlType ([System.Windows.Automation.ControlType]::Button) `
    -Deadline $deadline)
  [void](Find-AutomationElement `
    -Root $root `
    -Name "Close Mod Control" `
    -ControlType ([System.Windows.Automation.ControlType]::Button) `
    -Deadline $deadline)
  if (Test-ColorModeSelectorPresent -Root $root) {
    throw "The appearance selector must not consume title space on launcher Home."
  }
  Write-Host "PASS: launcher Home omits the appearance selector."

  [void](Find-AutomationElement `
    -Root $root `
    -Name "Community mod status" `
    -ControlType ([System.Windows.Automation.ControlType]::Text) `
    -Deadline $deadline)
  $buttonCondition = [System.Windows.Automation.PropertyCondition]::new(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Button)
  $homeButtons = $root.FindAll(
    [System.Windows.Automation.TreeScope]::Descendants,
    $buttonCondition)
  $modAction = $homeButtons | Where-Object {
    $_.Current.Name -match '(?i)community mod'
  } | Select-Object -First 1
  if ($null -eq $modAction) {
    throw "Launcher Home did not expose an accessible community-mod action."
  }
  Write-Host "PASS: launcher Home exposes community-mod state and action '$($modAction.Current.Name)'."

  $launchAction = $homeButtons | Where-Object {
    $_.Current.Name -match '(?i)^(launch prime\.exe|open Scopely launcher)'
  } | Select-Object -First 1
  if ($null -eq $launchAction) {
    throw "Launcher Home did not expose an accessible game-launch action."
  }
  $launchTargetMenu = Find-AutomationElement `
    -Root $root `
    -Name "Choose game launch target" `
    -ControlType ([System.Windows.Automation.ControlType]::Button) `
    -Deadline $deadline
  if ($launchTargetMenu.Current.BoundingRectangle.Height -lt 43 -or
      $launchTargetMenu.Current.BoundingRectangle.Width -lt 43) {
    throw "The launch-target menu segment is smaller than the required 44-DIP target."
  }
  Invoke-AutomationElement -Element $launchTargetMenu
  $menuDeadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
  $desktop = [System.Windows.Automation.AutomationElement]::RootElement
  $primeChoice = $null
  $scopelyChoice = $null
  while (($null -eq $primeChoice -or $null -eq $scopelyChoice) -and
      [DateTimeOffset]::UtcNow -lt $menuDeadline) {
    $launcherElements = $desktop.FindAll(
      [System.Windows.Automation.TreeScope]::Descendants,
      [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $launcherProcess.Id))
    $primeChoice = $launcherElements | Where-Object {
      $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and
      $_.Current.Name -match '^Launch prime\.exe'
    } | Select-Object -First 1
    $scopelyChoice = $launcherElements | Where-Object {
      $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and
      $_.Current.Name -match '^Open Scopely launcher'
    } | Select-Object -First 1
    if ($null -eq $primeChoice -or $null -eq $scopelyChoice) {
      Start-Sleep -Milliseconds 50
    }
  }
  if ($null -eq $primeChoice -or $null -eq $scopelyChoice) {
    throw "The launch-target menu did not expose both explicit choices through UI Automation."
  }
  [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
  Write-Host "PASS: launcher Home exposes separate prime.exe and Scopely launch choices without invoking either target."

  $diagnosticsEntry = Find-AutomationElement `
    -Root $root `
    -Name "Open redacted Mod Control diagnostics" `
    -ControlType ([System.Windows.Automation.ControlType]::Button) `
    -Deadline $deadline
  Invoke-AutomationElement -Element $diagnosticsEntry
  $diagnosticPreview = Find-AutomationElement `
    -Root $root `
    -Name "Redacted diagnostic preview" `
    -ControlType ([System.Windows.Automation.ControlType]::Edit) `
    -Deadline $deadline
  $valuePattern = $null
  if (-not $diagnosticPreview.TryGetCurrentPattern(
      [System.Windows.Automation.ValuePattern]::Pattern,
      [ref]$valuePattern)) {
    throw "The redacted diagnostic preview does not expose its text through UI Automation."
  }
  $diagnosticText = $valuePattern.Current.Value
  if ($diagnosticText -notmatch '"health"') {
    throw "The diagnostic preview did not expose health facts."
  }
  [void](Find-AutomationElement `
    -Root $root `
    -Name "Check for a launcher self-update" `
    -ControlType ([System.Windows.Automation.ControlType]::Button) `
    -Deadline $deadline)
  if ($diagnosticText.IndexOf($env:USERPROFILE, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
    throw "The diagnostic preview exposed the raw user-profile path."
  }
  if ($null -ne $configurationPath) {
    $activeGameDirectory = Split-Path -Parent $configurationPath
    if ($diagnosticText.IndexOf($activeGameDirectory, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
      throw "The diagnostic preview exposed the raw game-directory path."
    }
  }
  $closeDiagnostics = Find-AutomationElement `
    -Root $root `
    -Name "Close dialog" `
    -ControlType ([System.Windows.Automation.ControlType]::Button) `
    -Deadline $deadline
  Invoke-AutomationElement -Element $closeDiagnostics
  Write-Host "PASS: diagnostics are previewed through UI Automation with private paths redacted."

  $settingsEntry = Find-AutomationElement `
    -Root $root `
    -Name "Open Mod Control settings" `
    -ControlType ([System.Windows.Automation.ControlType]::Button) `
    -Deadline $deadline
  Invoke-AutomationElement -Element $settingsEntry

  $settingsDeadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
  [void](Find-AutomationElement `
    -Root $root `
    -Name "Return to launcher home" `
    -ControlType ([System.Windows.Automation.ControlType]::Button) `
    -Deadline $settingsDeadline)
  $appearance = Find-ColorModeSelector -Root $root -Deadline $settingsDeadline
  Write-Host "Settings appearance selector exposes selected value: $($appearance.SelectedMode)"
  [void](Find-AutomationElement `
    -Root $root `
    -Name "General settings" `
    -ControlType ([System.Windows.Automation.ControlType]::Button) `
    -Deadline $settingsDeadline)

  $openSettingsSearch = $root.FindFirst(
    [System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.AndCondition]::new(
      [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        "Open settings search"),
      [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)))
  if ($null -eq $openSettingsSearch) {
    $initialCloseSettingsSearch = Find-AutomationElement `
      -Root $root `
      -Name "Close settings search" `
      -ControlType ([System.Windows.Automation.ControlType]::Button) `
      -Deadline $settingsDeadline
    Invoke-AutomationElement -Element $initialCloseSettingsSearch
    $openSettingsSearch = Find-AutomationElement `
      -Root $root `
      -Name "Open settings search" `
      -ControlType ([System.Windows.Automation.ControlType]::Button) `
      -Deadline ([DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds))
  }
  Invoke-AutomationElement -Element $openSettingsSearch
  $settingsSearch = Find-AutomationElement `
    -Root $root `
    -Name "Search settings" `
    -ControlType ([System.Windows.Automation.ControlType]::Edit) `
    -Deadline ([DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds))
  $searchValuePattern = $settingsSearch.GetCurrentPattern(
    [System.Windows.Automation.ValuePattern]::Pattern)
  $searchValuePattern.SetValue("zoom")
  $clearSettingsSearch = Find-AutomationElement `
    -Root $root `
    -Name "Clear settings search query" `
    -ControlType ([System.Windows.Automation.ControlType]::Button) `
    -Deadline ([DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds))
  $searchCommandDeadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
  while (-not $clearSettingsSearch.Current.IsEnabled -and
      [DateTimeOffset]::UtcNow -lt $searchCommandDeadline) {
    Start-Sleep -Milliseconds 50
  }
  if (-not $clearSettingsSearch.Current.IsEnabled) {
    throw "Clear settings search did not become available after entering a query."
  }
  Invoke-AutomationElement -Element $clearSettingsSearch
  while ($searchValuePattern.Current.Value -ne "" -and
      [DateTimeOffset]::UtcNow -lt $searchCommandDeadline) {
    Start-Sleep -Milliseconds 50
  }
  if ($searchValuePattern.Current.Value -ne "") {
    throw "Clear settings search did not clear the query."
  }
  $closeSettingsSearch = Find-AutomationElement `
    -Root $root `
    -Name "Close settings search" `
    -ControlType ([System.Windows.Automation.ControlType]::Button) `
    -Deadline ([DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds))
  Invoke-AutomationElement -Element $closeSettingsSearch
  [void](Find-AutomationElement `
    -Root $root `
    -Name "Open settings search" `
    -ControlType ([System.Windows.Automation.ControlType]::Button) `
    -Deadline ([DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)))
  Write-Host "PASS: settings search exposes distinct open, clear-query, and close actions."

  $provenanceSurface = $root.FindAll(
      [System.Windows.Automation.TreeScope]::Descendants,
      [System.Windows.Automation.Condition]::TrueCondition) |
    Where-Object {
      $_.Current.Name.StartsWith(
        "Default and effective value for ",
        [StringComparison]::Ordinal)
    } |
    Select-Object -First 1
  if ($null -eq $provenanceSurface -or
      [string]::IsNullOrWhiteSpace($provenanceSurface.Current.HelpText)) {
    throw "Settings rows do not expose default/effective provenance through UI Automation."
  }
  Write-Host "PASS: settings rows expose a stable default/effective provenance help surface."

  $unexpectedSyncWorkspace = $root.FindFirst(
    [System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.PropertyCondition]::new(
      [System.Windows.Automation.AutomationElement]::NameProperty,
      "Data Sync workspace"))
  if ($null -ne $unexpectedSyncWorkspace) {
    throw "The typed Sync workspace is visible while General settings is selected."
  }
  Write-Host "PASS: typed Data Sync stays hidden outside the Data Sync section."
  $notificationsNavigation = Find-AutomationElement `
    -Root $root `
    -Name "Notification settings" `
    -ControlType ([System.Windows.Automation.ControlType]::Button) `
    -Deadline $settingsDeadline
  $hotkeysNavigation = Find-AutomationElement `
    -Root $root `
    -Name "Hotkey settings" `
    -ControlType ([System.Windows.Automation.ControlType]::Button) `
    -Deadline $settingsDeadline
  $syncNavigation = Find-AutomationElement `
    -Root $root `
    -Name "Data Sync settings" `
    -ControlType ([System.Windows.Automation.ControlType]::Button) `
    -Deadline $settingsDeadline

  Invoke-AutomationElement -Element $notificationsNavigation
  [void](Find-AutomationElement `
    -Root $root `
    -Name "Choose which events alert you and how." `
    -Deadline ([DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)))

  Invoke-AutomationElement -Element $hotkeysNavigation
  [void](Find-AutomationElement `
    -Root $root `
    -Name "Fleet" `
    -Deadline ([DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)))
  $moreFleetActions = Find-AutomationElement `
    -Root $root `
    -Name "More actions for Primary fleet action" `
    -ControlType ([System.Windows.Automation.ControlType]::Button) `
    -Deadline ([DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds))
  Invoke-AutomationElement -Element $moreFleetActions
  [void](Find-AutomationElement `
    -Root ([System.Windows.Automation.AutomationElement]::RootElement) `
    -Name "Add a binding for Primary fleet action" `
    -ControlType ([System.Windows.Automation.ControlType]::Button) `
    -Deadline ([DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)))

  Invoke-AutomationElement -Element $syncNavigation
  [void](Find-AutomationElement `
    -Root $root `
    -Name "Data Sync workspace" `
    -Deadline ([DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)))
  [void](Find-AutomationElement `
    -Root $root `
    -Name "Global defaults" `
    -ControlType ([System.Windows.Automation.ControlType]::Text) `
    -Deadline ([DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)))
  $dataSyncPage = Find-AutomationElement `
    -Root $root `
    -Name "Data Sync page content" `
    -Deadline ([DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds))
  $scrollPattern = $null
  if (-not $dataSyncPage.TryGetCurrentPattern(
      [System.Windows.Automation.ScrollPattern]::Pattern,
      [ref]$scrollPattern)) {
    throw "The Data Sync page does not expose its styled vertical scroll surface to UI Automation."
  }
  if ($scrollPattern.Current.HorizontallyScrollable) {
    throw "The Data Sync page exposed horizontal scrolling at the supported viewport."
  }

  $transformPattern = $null
  if (-not $root.TryGetCurrentPattern(
      [System.Windows.Automation.TransformPattern]::Pattern,
      [ref]$transformPattern) -or
      -not $transformPattern.Current.CanResize) {
    throw "The launcher window does not expose resize support for minimum-width validation."
  }
  $transformPattern.Resize(960, 620)
  Start-Sleep -Milliseconds 250
  $bounds = $root.Current.BoundingRectangle
  if ($bounds.Width -lt 959 -or $bounds.Height -lt 619) {
    throw "The launcher did not retain its supported 960x620 minimum after resize."
  }
  if ($scrollPattern.Current.HorizontallyScrollable) {
    throw "The Data Sync page exposed horizontal scrolling at 960x620."
  }

  $infoButton = Find-AutomationElement `
    -Root $root `
    -Name "About Data Sync editing" `
    -ControlType ([System.Windows.Automation.ControlType]::Button) `
    -Deadline ([DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds))
  if ([string]::IsNullOrWhiteSpace($infoButton.Current.HelpText)) {
    throw "The Data Sync information button has no keyboard-readable help text."
  }

  $scrollTabsLeft = Find-AutomationElement `
    -Root $root `
    -Name "Scroll destination tabs left" `
    -ControlType ([System.Windows.Automation.ControlType]::Button) `
    -Deadline ([DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds))
  $scrollTabsRight = Find-AutomationElement `
    -Root $root `
    -Name "Scroll destination tabs right" `
    -ControlType ([System.Windows.Automation.ControlType]::Button) `
    -Deadline ([DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds))
  $tabOverflow = Find-AutomationElement `
    -Root $root `
    -Name "Destination tab overflow" `
    -Deadline ([DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds))
  $tabScrollPattern = $null
  if (-not $tabOverflow.TryGetCurrentPattern(
      [System.Windows.Automation.ScrollPattern]::Pattern,
      [ref]$tabScrollPattern) -or
      -not $tabScrollPattern.Current.HorizontallyScrollable) {
    throw "The forced-overflow destination strip does not expose a horizontal offset."
  }
  $leftPercent = $tabScrollPattern.Current.HorizontalScrollPercent
  Invoke-AutomationElement -Element $scrollTabsRight
  $rightPercent = Wait-ForHorizontalScrollChange `
    -ScrollPattern $tabScrollPattern `
    -PreviousPercent $leftPercent `
    -Direction Increase `
    -Deadline ([DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds))
  Invoke-AutomationElement -Element $scrollTabsLeft
  [void](Wait-ForHorizontalScrollChange `
    -ScrollPattern $tabScrollPattern `
    -PreviousPercent $rightPercent `
    -Direction Decrease `
    -Deadline ([DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)))

  $globalTab = Find-AutomationElement `
    -Root $root `
    -Name "Global Data Sync defaults tab" `
    -ControlType ([System.Windows.Automation.ControlType]::Button) `
    -Deadline ([DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds))
  $focusedTab = Send-AutomationKeyAndWaitForFocus `
    -Element $globalTab `
    -Keys "{END}" `
    -ExpectedNamePattern '^local-sidecar, Sidecar, (Ready|Needs attention)$' `
    -Deadline ([DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds))
  Assert-AutomationToggleOn -Element $focusedTab
  [void](Find-AutomationElement `
    -Root $root `
    -Name "Destination Realtime battlelogs feed override, inherited" `
    -ControlType ([System.Windows.Automation.ControlType]::Button) `
    -Deadline ([DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)))
  [void](Find-AutomationElement `
    -Root $root `
    -Name "Destination Fleet runtime feed override, inherited" `
    -ControlType ([System.Windows.Automation.ControlType]::Button) `
    -Deadline ([DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)))
  $unsupportedSidecarFeed = $root.FindFirst(
    [System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.PropertyCondition]::new(
      [System.Windows.Automation.AutomationElement]::NameProperty,
      "Destination Jobs feed override, inherited"))
  if ($null -ne $unsupportedSidecarFeed) {
    throw "The Sidecar editor exposed an unsupported Jobs feed override."
  }

  $focusedTab = Send-AutomationKeyAndWaitForFocus `
    -Element $focusedTab `
    -Keys "{HOME}" `
    -ExpectedNamePattern '^Global Data Sync defaults tab$' `
    -Deadline ([DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds))
  Assert-AutomationToggleOn -Element $focusedTab
  $focusedTab = Send-AutomationKeyAndWaitForFocus `
    -Element $focusedTab `
    -Keys "{RIGHT}" `
    -ExpectedNamePattern '^alpha, Ready$' `
    -Deadline ([DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds))
  Assert-AutomationToggleOn -Element $focusedTab
  $focusedTab = Send-AutomationKeyAndWaitForFocus `
    -Element $focusedTab `
    -Keys "{LEFT}" `
    -ExpectedNamePattern '^Global Data Sync defaults tab$' `
    -Deadline ([DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds))
  Assert-AutomationToggleOn -Element $focusedTab

  $addDestination = Find-AutomationElement `
    -Root $root `
    -Name "Add Data Sync destination" `
    -ControlType ([System.Windows.Automation.ControlType]::Button) `
    -Deadline ([DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds))
  $addDestination.SetFocus()
  Invoke-AutomationElement -Element $addDestination
  $customChoice = $null
  foreach ($choice in @("Custom sync", "Spock's Club", "Next Spock's Club")) {
    $choiceElement = Find-AutomationElement `
      -Root $root `
      -Name $choice `
      -ControlType ([System.Windows.Automation.ControlType]::Button) `
      -Deadline ([DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds))
    if ($choice -eq "Custom sync") {
      $customChoice = $choiceElement
    }
  }
  [void](Send-AutomationKeyAndWaitForFocus `
    -Element $customChoice `
    -Keys " " `
    -ExpectedNamePattern '^Custom sync$' `
    -Deadline ([DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)))
  $nextWizardStep = Find-AutomationElement `
    -Root $root `
    -Name "Next" `
    -ControlType ([System.Windows.Automation.ControlType]::Button) `
    -Deadline ([DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds))
  $focusedField = Send-AutomationKeyAndWaitForFocus `
    -Element $nextWizardStep `
    -Keys "{ENTER}" `
    -ExpectedNamePattern '^Destination display name$' `
    -Deadline ([DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds))
  [void](Send-AutomationKeyAndWaitForFocus `
    -Element $focusedField `
    -Keys "{ESC}" `
    -ExpectedNamePattern '^Add Data Sync destination$' `
    -Deadline ([DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)))
  $wizardStillOpen = $root.FindFirst(
    [System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.PropertyCondition]::new(
      [System.Windows.Automation.AutomationElement]::NameProperty,
      "Custom sync"))
  if ($null -ne $wizardStillOpen) {
    throw "Escape did not cancel the Add destination wizard."
  }
  $transformPattern.Resize(1120, 740)
  Start-Sleep -Milliseconds 250
  Write-Host "PASS: Data Sync is source-bound, minimum-width safe, vertically scoped, capability-filtered, and keyboard-proven across overflow tabs and wizard cancellation/focus restoration."

  $advancedNavigation = Find-AutomationElement `
    -Root $root `
    -Name "Advanced settings" `
    -ControlType ([System.Windows.Automation.ControlType]::Button) `
    -Deadline $settingsDeadline
  Invoke-AutomationElement -Element $advancedNavigation
  [void](Find-AutomationElement `
    -Root $root `
    -Name "Patch editing safety warning" `
    -Deadline ([DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)))
  [void](Find-AutomationElement `
    -Root $root `
    -Name "Read-only patch value summary" `
    -Deadline ([DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)))
  $enablePatchEditing = Find-AutomationElement `
    -Root $root `
    -Name "Enable patch editing" `
    -ControlType ([System.Windows.Automation.ControlType]::Button) `
    -Deadline ([DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds))
  Invoke-AutomationElement -Element $enablePatchEditing
  $lockPatchEditing = Find-AutomationElement `
    -Root $root `
    -Name "Lock patch editing" `
    -ControlType ([System.Windows.Automation.ControlType]::Button) `
    -Deadline ([DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds))
  Invoke-AutomationElement -Element $lockPatchEditing
  [void](Find-AutomationElement `
    -Root $root `
    -Name "Enable patch editing" `
    -ControlType ([System.Windows.Automation.ControlType]::Button) `
    -Deadline ([DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)))
  Write-Host "PASS: Advanced patch editing starts locked, exposes its warning and read-only summary, and supports unlock/relock through UI Automation."

  $aboutNavigation = Find-AutomationElement `
    -Root $root `
    -Name "About Mod Control settings" `
    -ControlType ([System.Windows.Automation.ControlType]::Button) `
    -Deadline $settingsDeadline
  Invoke-AutomationElement -Element $aboutNavigation
  foreach ($diagnosticValue in @(
      $expectedRuntimeIdentity,
      "Active",
      "Semantic",
      "Runtime provides settings.principal-taxonomy.v1."
    )) {
    [void](Find-AutomationElement `
      -Root $root `
      -Name $diagnosticValue `
      -ControlType ([System.Windows.Automation.ControlType]::Text) `
      -Deadline ([DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)))
  }

  Write-Host "PASS: launcher chrome, typed Data Sync, grouped Hotkeys actions, overflow binding actions, appearance selection, and startup activation diagnostics are UI Automation accessible."
}
catch {
  $smokeFailure = $_
}
finally {
  Stop-OwnedProcess -Process $launcherProcess

  if ($null -ne $configurationHashBefore) {
    if (-not (Test-Path -LiteralPath $configurationPath -PathType Leaf)) {
      $integrityFailure = "The active TOML disappeared during the non-mutating smoke: $configurationPath"
    }
    else {
      $configurationHashAfter = (
        Get-FileHash -LiteralPath $configurationPath -Algorithm SHA256
      ).Hash
      if ($configurationHashAfter -ne $configurationHashBefore) {
        $integrityFailure = "The active TOML changed during the non-mutating smoke: $configurationPath"
      }
      else {
        Write-Host "PASS: active TOML SHA-256 is unchanged."
      }
    }
  }

  if ($windirWasRestored) {
    [Environment]::SetEnvironmentVariable("WINDIR", $null, "Process")
  }

  if ($UseDisposableSyncFixture) {
    Restore-DisposableFixture `
      -SelectionExisted $selectionExisted `
      -SelectionBytes $selectionBytes `
      -SelectionPath $selectionPath `
      -DisposableGameDirectory $disposableGameDirectory
  }
}

if ($null -ne $integrityFailure) {
  throw $integrityFailure
}

if ($null -ne $smokeFailure) {
  throw $smokeFailure
}
