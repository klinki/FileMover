[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../publish/nas'),
    [string]$BuilderName = 'backup-normalizer-nas',
    [string]$NativeBuildDirectory,
    [string]$ToolchainDirectory = (Join-Path $PSScriptRoot '../publish/nas-test-20261005'),
    [switch]$SkipNativeBuild
)

$ErrorActionPreference = 'Stop'
$repositoryPath = Split-Path $PSScriptRoot -Parent
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
$archivePath = Join-Path $outputPath 'backup-normalizer-qnap-arm32-aot.tar'

if (-not $NativeBuildDirectory) {
    $NativeBuildDirectory = Join-Path $repositoryPath ('publish/qnap-aot-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
}
$nativePath = [IO.Path]::GetFullPath($NativeBuildDirectory)
if (-not $SkipNativeBuild) {
    & python (Join-Path $PSScriptRoot 'prepare-inventory-aot.py') $nativePath
    if ($LASTEXITCODE -ne 0) { throw 'Could not prepare the isolated AOT source snapshot' }
    $linuxScript = & wsl -d Ubuntu -- wslpath -a (Join-Path $PSScriptRoot 'build-inventory-aot.sh')
    if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the WSL build script path' }
    $linuxOutput = & wsl -d Ubuntu -- wslpath -a $nativePath
    if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the WSL output path' }
    $linuxToolchain = & wsl -d Ubuntu -- wslpath -a ([IO.Path]::GetFullPath($ToolchainDirectory))
    if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the WSL toolchain path' }
    & wsl -d Ubuntu -- sh $linuxScript $linuxOutput $linuxToolchain
    if ($LASTEXITCODE -ne 0) { throw 'Native AOT CLI/helper publication failed' }
}

$contextPath = Join-Path $outputPath 'image-context'
New-Item -ItemType Directory -Path $contextPath -Force | Out-Null
foreach ($name in @('BackupNormalizer', 'BackupNormalizer.Migrations', 'libe_sqlite3.so')) {
    $artifactPath = Join-Path $nativePath "app/$name"
    if (-not (Test-Path -LiteralPath $artifactPath -PathType Leaf)) { throw "Missing AOT artifact: $artifactPath" }
    Copy-Item -LiteralPath $artifactPath -Destination (Join-Path $contextPath $name)
}
foreach ($name in @('Dockerfile.qnap', 'qnap-check.sh', 'qnap-inventory.sh')) {
    Copy-Item -LiteralPath (Join-Path $repositoryPath $name) -Destination (Join-Path $contextPath $name)
}

New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
& docker buildx inspect $BuilderName 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
    & docker buildx create --name $BuilderName --driver docker-container | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the container image builder' }
}
& docker buildx build --builder $BuilderName --platform linux/arm/v7 --file (Join-Path $contextPath 'Dockerfile.qnap') `
    --tag backup-normalizer:qnap-arm32-aot --provenance=false --output "type=docker,dest=$archivePath" $contextPath
if ($LASTEXITCODE -ne 0) { throw "Docker image build failed with exit code $LASTEXITCODE" }

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'compose.qnap.yaml') -Destination (Join-Path $outputPath 'compose.yaml')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'qnap.env.example') -Destination (Join-Path $outputPath '.env.example')
Write-Output "NAS deployment bundle: $outputPath"
Write-Output "Import $archivePath into Container Station, then configure the NAS paths in compose.yaml."
