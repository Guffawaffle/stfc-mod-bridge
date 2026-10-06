# Pure parser/context/argument controls. No process launch or token mutation.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($args.Count -ne 0) { throw 'No caller control-test arguments.' }
if (-not $IsWindows -or [IntPtr]::Size -ne 8 -or $PSVersionTable.PSVersion.Major -ne 7) { throw 'Windows x64 PowerShell 7 is required.' }
if ('StfcBridgeJournalCi.WindowsJournalCi' -as [type]) { throw 'A preloaded launcher binding cannot establish current-source tests.' }
$sourcePath = [IO.Path]::Combine($PSScriptRoot, '..', 'windows-journal-ci.cs')
$sourceText = [IO.File]::ReadAllText($sourcePath, [Text.UTF8Encoding]::new($false, $true))
Add-Type -TypeDefinition $sourceText -ErrorAction Stop
$passed = [StfcBridgeJournalCi.WindowsJournalCi]::ControlTests()
if ($passed.Count -ne 179) { throw 'The complete fixed control inventory did not execute.' }
[Console]::Out.WriteLine((@{ result = 'passed'; tests = $passed; native9Observed = $false; hostedCiProved = $false } | ConvertTo-Json -Depth 4 -Compress))
