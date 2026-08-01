[CmdletBinding()]
param(
  [Parameter(Mandatory)]
  [string]$LauncherPath,

  [ValidateRange(5, 120)]
  [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = "Stop"

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

$resolvedLauncherPath = (Resolve-Path -LiteralPath $LauncherPath).Path
if (-not (Test-Path -LiteralPath $resolvedLauncherPath -PathType Leaf) -or
    [System.IO.Path]::GetExtension($resolvedLauncherPath) -ne ".exe") {
  throw "LauncherPath must identify an existing .exe file: $LauncherPath"
}

$runtimeManifestPath = (
  Resolve-Path -LiteralPath (
    Join-Path `
      $PSScriptRoot `
      "..\..\docs\windows-launcher\runtime-manifest.guffawaffle.v1.json")
).Path
$runtimeManifest = Get-Content -LiteralPath $runtimeManifestPath -Raw |
  ConvertFrom-Json -ErrorAction Stop
$expectedRuntimeIdentity = "Guffawaffle $($runtimeManifest.runtimeVersion)"

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

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

  if ($root.Current.Name -ne "STFC Community Mod Launcher") {
    throw "Unexpected launcher window title '$($root.Current.Name)'."
  }

  [void](Find-AutomationElement `
    -Root $root `
    -Name "Minimize launcher" `
    -ControlType ([System.Windows.Automation.ControlType]::Button) `
    -Deadline $deadline)
  [void](Find-AutomationElement `
    -Root $root `
    -Name "Close launcher" `
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
    $_.Current.Name -match '(?i)^launch (the )?(modded game|game)'
  } | Select-Object -First 1
  if ($null -eq $launchAction) {
    throw "Launcher Home did not expose an accessible game-launch action."
  }
  Write-Host "PASS: launcher Home exposes explicit modded-launch state '$($launchAction.Current.Name)'."

  $diagnosticsEntry = Find-AutomationElement `
    -Root $root `
    -Name "Open redacted launcher diagnostics" `
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
    -Name "Open launcher settings" `
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

  $aboutNavigation = Find-AutomationElement `
    -Root $root `
    -Name "About launcher settings" `
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

  Write-Host "PASS: launcher chrome, grouped Hotkeys actions, overflow binding actions, appearance selection, and startup activation diagnostics are UI Automation accessible."
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
}

if ($null -ne $integrityFailure) {
  throw $integrityFailure
}

if ($null -ne $smokeFailure) {
  throw $smokeFailure
}
