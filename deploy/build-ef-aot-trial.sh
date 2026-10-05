#!/bin/sh
# Run from the repository root in WSL/Linux. Uses the existing isolated cross-toolchain.
set -eu
TRIAL_DIR="$(realpath "${1:?Pass the trial output directory under publish}")"
TOOLCHAIN_DIR="$(realpath "${2:?Pass the existing NAS AOT toolchain directory}")"
cd "$TRIAL_DIR/source"
export DOTNET_CLI_HOME="$TOOLCHAIN_DIR/linux-dotnet-home"
export DOTNET_ROOT="$TOOLCHAIN_DIR/linux-sdk"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
if [ "${3:-}" = "--manual" ]; then
    "$DOTNET_ROOT/dotnet" tool install dotnet-ef --version 10.0.12 \
        --tool-path "$TRIAL_DIR/ef-tools" --configfile "$TOOLCHAIN_DIR/NuGet.Linux.Config"
    "$DOTNET_ROOT/dotnet" build tools/BackupNormalizer.AotTrial/BackupNormalizer.AotTrial.csproj \
        -c Release -p:RestoreConfigFile="$TOOLCHAIN_DIR/NuGet.Linux.Config" -p:NuGetAudit=false
    "$TRIAL_DIR/ef-tools/dotnet-ef" dbcontext optimize \
        --project src/BackupNormalizer.Core/BackupNormalizer.Core.csproj --configuration Release \
        --no-build --nativeaot --output-dir GeneratedModel --namespace BackupNormalizer.CompiledModel
    "$DOTNET_ROOT/dotnet" build tools/BackupNormalizer.AotTrial/BackupNormalizer.AotTrial.csproj \
        -c Release -p:RestoreConfigFile="$TOOLCHAIN_DIR/NuGet.Linux.Config" -p:NuGetAudit=false
    "$TRIAL_DIR/ef-tools/dotnet-ef" dbcontext optimize \
        --project tools/BackupNormalizer.AotTrial/BackupNormalizer.AotTrial.csproj --configuration Release \
        --no-build --nativeaot --no-scaffold --precompile-queries --output-dir GeneratedQueries
fi
"$TOOLCHAIN_DIR/linux-sdk/dotnet" publish tools/BackupNormalizer.AotTrial/BackupNormalizer.AotTrial.csproj \
    -c Release -r linux-musl-arm -p:PublishAot=true -p:StripSymbols=false \
    -p:InvariantGlobalization=true -p:NuGetAudit=false \
    -p:RestoreConfigFile="$TOOLCHAIN_DIR/NuGet.Linux.Config" \
    -p:CppCompilerAndLinker="$TOOLCHAIN_DIR/arm-clang.sh" -p:LinkerFlavor=lld \
    -o "$TRIAL_DIR/app"
