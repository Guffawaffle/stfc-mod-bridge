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
    $elements = $Root.FindAll(
      [System.Windows.Automation.TreeScope]::Descendants,
      [System.Windows.Automation.Condition]::TrueCondition)
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

  $appearance = Find-ColorModeSelector -Root $root -Deadline $deadline
  Write-Host "Appearance selector exposes selected value: $($appearance.SelectedMode)"

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
  [void](Find-AutomationElement `
    -Root $root `
    -Name "Hotkey settings" `
    -ControlType ([System.Windows.Automation.ControlType]::Button) `
    -Deadline $settingsDeadline)

  Invoke-AutomationElement -Element $notificationsNavigation
  [void](Find-AutomationElement `
    -Root $root `
    -Name "Choose which events alert you and how." `
    -Deadline ([DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)))

  Write-Host "PASS: launcher chrome, Settings entry, settings navigation, and appearance selection are UI Automation accessible."
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
