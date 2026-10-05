#!/bin/sh
# Generate from ordinary Linux assemblies, then publish CLI and helper for QNAP.
set -eu
OUTPUT="$(realpath "${1:?Pass the isolated build directory under publish}")"
TOOLCHAIN="$(realpath "${2:?Pass the tested ARM toolchain directory}")"
cd "$OUTPUT/source"
export DOTNET_CLI_HOME="$TOOLCHAIN/linux-dotnet-home"
export DOTNET_ROOT="$TOOLCHAIN/linux-sdk"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet run --project tools/BackupNormalizer.MigrationGenerator -c Release \
    -p:RestoreConfigFile="$TOOLCHAIN/NuGet.Linux.Config" -p:NuGetAudit=false \
    -- src/BackupNormalizer.Schema/GeneratedMigrations.json
dotnet build src/BackupNormalizer/BackupNormalizer.csproj -c Release -p:NativeAotBuild=false \
    -p:RestoreConfigFile="$TOOLCHAIN/NuGet.Linux.Config" -p:NuGetAudit=false
python3 "$(dirname "$0")/prepare-execution-fixture.py" "$OUTPUT"
export NativeAotBuild=true
dotnet build src/BackupNormalizer/BackupNormalizer.csproj -c Release \
    -p:RestoreConfigFile="$TOOLCHAIN/NuGet.Linux.Config" -p:NuGetAudit=false
EF_TOOL="${EF_TOOL:-$OUTPUT/ef-tools/dotnet-ef}"
if [ ! -x "$EF_TOOL" ]; then
    dotnet tool install dotnet-ef --version 10.0.12 --tool-path "$OUTPUT/ef-tools" \
        --configfile "$TOOLCHAIN/NuGet.Linux.Config"
fi
"$EF_TOOL" dbcontext optimize --project src/BackupNormalizer.Core/BackupNormalizer.Core.csproj \
    --configuration Release --no-build --nativeaot --precompile-queries \
    --output-dir GeneratedModel --namespace BackupNormalizer.CompiledModel
# Keep the pinned generator's alias and setter-order corrections in one place.
python3 "$(dirname "$0")/fix-ef-interceptors.py" src/BackupNormalizer.Core/GeneratedModel
dotnet build src/BackupNormalizer/BackupNormalizer.csproj -c Release \
    -p:RestoreConfigFile="$TOOLCHAIN/NuGet.Linux.Config" -p:NuGetAudit=false
dotnet build src/BackupNormalizer.Migrations/BackupNormalizer.Migrations.csproj -c Release \
    -p:RestoreConfigFile="$TOOLCHAIN/NuGet.Linux.Config" -p:NuGetAudit=false
python3 "$(dirname "$0")/verify-precompiled-inventory.py" "$OUTPUT"
python3 "$(dirname "$0")/verify-precompiled-execution.py" "$OUTPUT"
for project in BackupNormalizer BackupNormalizer.Migrations; do
    dotnet publish "src/$project/$project.csproj" -c Release -r linux-musl-arm \
        -p:PublishAot=true -p:StripSymbols=false -p:InvariantGlobalization=true -p:NuGetAudit=false \
        -p:RestoreConfigFile="$TOOLCHAIN/NuGet.Linux.Config" \
        -p:CppCompilerAndLinker="$TOOLCHAIN/arm-clang.sh" -p:LinkerFlavor=lld \
        -o "$OUTPUT/app"
done
