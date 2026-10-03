param([string]$ResultsDirectory = '')
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $IsWindows) { throw 'Native NTFS verification requires Windows and PowerShell 7.' }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script in an elevated PowerShell 7 terminal.'
}
if (-not $ResultsDirectory) { $ResultsDirectory = Join-Path $workspace ('publish/native-ntfs-' + [Guid]::NewGuid().ToString('N')) }
$ResultsDirectory = [IO.Path]::GetFullPath($ResultsDirectory)
if (-not $ResultsDirectory.StartsWith($workspace + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The results directory must be beneath the workspace.'
}
New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null
Push-Location $workspace
try {
    & dotnet test tests/BackupNormalizer.Tests --configuration Release --no-restore '-p:UsedAvaloniaProducts=' --filter 'FullyQualifiedName~UsnWindowsTests' --logger 'console;verbosity=detailed' --logger 'trx;LogFileName=native.trx' --results-directory $ResultsDirectory *> (Join-Path $ResultsDirectory 'native.log')
    if ($LASTEXITCODE -ne 0) { throw "Native tests failed. See $ResultsDirectory/native.log" }
    [xml]$result = Get-Content -LiteralPath (Join-Path $ResultsDirectory 'native.trx')
    $counts = $result.TestRun.ResultSummary.Counters
    if ([int]$counts.passed -lt 2 -or [int]$counts.notExecuted -ne 0) {
        throw 'Native checks were skipped; an existing accessible NTFS journal is required.'
    }
    Write-Output "Native USN/MFT parity checks passed. Results: $ResultsDirectory"
}
finally { Pop-Location }
