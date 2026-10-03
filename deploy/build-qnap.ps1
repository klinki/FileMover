[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../publish/nas'),
    [string]$BuilderName = 'backup-normalizer-nas'
)

$ErrorActionPreference = 'Stop'
$repositoryPath = Split-Path $PSScriptRoot -Parent
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
$archivePath = Join-Path $outputPath 'backup-normalizer-qnap-arm32.tar'

New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
& docker buildx inspect $BuilderName 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
    & docker buildx create --name $BuilderName --driver docker-container | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the container image builder' }
}
& docker buildx build --builder $BuilderName --platform linux/arm/v7 --file (Join-Path $repositoryPath 'Dockerfile.qnap') `
    --tag backup-normalizer:qnap-arm32 --provenance=false --output "type=docker,dest=$archivePath" $repositoryPath
if ($LASTEXITCODE -ne 0) { throw "Docker image build failed with exit code $LASTEXITCODE" }

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'compose.qnap.yaml') -Destination (Join-Path $outputPath 'compose.yaml')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'qnap.env.example') -Destination (Join-Path $outputPath '.env.example')
Write-Output "NAS deployment bundle: $outputPath"
Write-Output "Import $archivePath into Container Station, then configure the NAS paths in compose.yaml."
