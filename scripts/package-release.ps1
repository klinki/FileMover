#requires -Version 7.2
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')]
    [string]$Runtime = 'win-x64',
    [string]$OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$buildInputs = @('src/BackupNormalizer', 'src/BackupNormalizer.Core', 'src/BackupNormalizer.Ui', 'scripts/package-release.ps1', 'Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props', 'global.json', 'NuGet.Config', 'nuget.config')
function Get-SourceFingerprint {
    $paths = @(& git -C $workspace -c core.quotePath=false ls-files --cached --others --exclude-standard -- @buildInputs)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot list application build inputs.' }
    $fingerprints = foreach ($path in ($paths | Sort-Object -Unique)) {
        $fullPath = Join-Path $workspace $path
        if (Test-Path -LiteralPath $fullPath -PathType Leaf) {
            $path + ' ' + (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash
        } else { $path + ' missing' }
    }
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($fingerprints -join "`n")))
}
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $workspace 'publish/releases' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$commit = (& git -C $workspace rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') { throw 'Cannot identify the source Git revision.' }
$changes = @(& git -C $workspace status --porcelain --untracked-files=normal -- @buildInputs)
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect application source state.' }
$state = if ($changes.Count -eq 0) { 'clean' } else { 'dirty' }
$fingerprint = Get-SourceFingerprint
[xml]$properties = Get-Content -LiteralPath (Join-Path $workspace 'src/BackupNormalizer.Core/BuildVersion.props')
$version = [string]$properties.Project.PropertyGroup.Version
$label = "BackupNormalizer-$version-$($commit.Substring(0,12))-$Runtime"
if ($state -eq 'dirty') { $label += '-dirty' }
$bundle = Join-Path $OutputDirectory ($label + '-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $bundle -Force | Out-Null
foreach ($component in @('BackupNormalizer', 'BackupNormalizer.Ui')) {
    $folder = if ($component -eq 'BackupNormalizer') { 'cli' } else { 'ui' }
    & dotnet publish (Join-Path $workspace "src/$component/$component.csproj") --configuration Release --runtime $Runtime --self-contained true --output (Join-Path $bundle $folder) '-p:UsedAvaloniaProducts=' "-p:SourceRevisionId=$commit" "-p:BuildSourceState=$state"
    if ($LASTEXITCODE -ne 0) { throw "Publishing $component failed." }
}
$after = @(& git -C $workspace status --porcelain --untracked-files=normal -- @buildInputs)
if ($LASTEXITCODE -ne 0 -or ($changes -join "`n") -cne ($after -join "`n") -or $fingerprint -ne (Get-SourceFingerprint) -or $commit -ne (& git -C $workspace rev-parse HEAD).Trim()) {
    throw 'Application sources changed while packaging. Build again from a stable checkout.'
}
$manifest = [ordered]@{
    product = 'BackupNormalizer'; version = $version; commit = $commit; sourceState = $state
    runtime = $Runtime; configuration = 'Release'; selfContained = $true
    builtUtc = [DateTime]::UtcNow.ToString('o'); sourceChanges = $changes; sourceFingerprint = $fingerprint
}
[IO.File]::WriteAllText((Join-Path $bundle 'build-info.json'), ($manifest | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $bundle 'README.txt'), "BackupNormalizer $version`nGit commit: $commit`nSource state: $state`nRuntime: $Runtime`n`nRun the CLI from cli/ and the desktop planner from ui/. These builds include .NET.`nCLI --version and UI --version report the embedded build identity.`n", [Text.UTF8Encoding]::new($false))
$archive = $bundle + '.zip'
Compress-Archive -LiteralPath (Get-ChildItem -LiteralPath $bundle | Select-Object -ExpandProperty FullName) -DestinationPath $archive
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
[IO.File]::WriteAllText($archive + '.sha256', "$hash  $([IO.Path]::GetFileName($archive))`n", [Text.UTF8Encoding]::new($false))
Write-Output "Release ZIP: $archive"
Write-Output "SHA-256: $hash"
