# Fixed selected-gate launcher. Caller arguments and execution overrides refuse.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$sourceStream = $null
try {
    if ($args.Count -ne 0) { throw 'No caller launcher arguments.' }
    if (-not $IsWindows -or [IntPtr]::Size -ne 8 -or $PSVersionTable.PSVersion.Major -ne 7) { throw 'Native Windows x64 PowerShell 7 is required.' }
    $owningRoot = [IO.Path]::GetFullPath((Get-Location).ProviderPath).TrimEnd('\')
    $expectedDirectory = [IO.Path]::Combine($owningRoot, 'scripts', 'next')
    if (-not [string]::Equals([IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\'), $expectedDirectory, [StringComparison]::OrdinalIgnoreCase)) { throw 'Canonical owning checkout cwd is required.' }
    if (-not [string]::Equals([IO.Path]::GetFullPath([Environment]::CurrentDirectory).TrimEnd('\'), $owningRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Process cwd and PowerShell location disagree.' }
    if ('StfcBridgeJournalCi.WindowsJournalCi' -as [type]) { throw 'A preloaded launcher binding cannot be reused.' }
    $sourcePath = [IO.Path]::Combine($expectedDirectory, 'windows-journal-ci.cs')
    $sourceStream = [IO.FileStream]::new($sourcePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    if ($sourceStream.Length -le 0 -or $sourceStream.Length -gt 2MB) { throw 'Launcher source exceeds its fixed bound.' }
    $sourceBytes = [byte[]]::new([int]$sourceStream.Length)
    $sourceStream.ReadExactly($sourceBytes)
    $sourceText = [Text.UTF8Encoding]::new($false, $true).GetString($sourceBytes)
    if ($sourceText.StartsWith([char]0xfeff)) { throw 'Launcher source cannot carry a BOM.' }
    Add-Type -TypeDefinition $sourceText -ErrorAction Stop
    $result = [StfcBridgeJournalCi.WindowsJournalCi]::Run()
    [Console]::Out.WriteLine(($result | ConvertTo-Json -Depth 24 -Compress))
    if ($result.Result -ne 'passed') { exit 1 }
    exit 0
} catch {
    [Console]::Error.WriteLine('{"result":"failed","failureCode":"LAUNCHER_BOOTSTRAP_REFUSED","native9Observed":false,"hostedCiProved":false}')
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
} finally {
    if ($null -ne $sourceStream) { $sourceStream.Dispose() }
}
