using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BackupNormalizer;

public sealed record MigrationStep(string Id, string ProductVersion, string[] Commands);

public sealed record MigrationManifest(int Format, MigrationStep[] Steps);

[JsonSerializable(typeof(MigrationManifest))]
internal partial class MigrationJsonContext : JsonSerializerContext;

public static class MigrationPackage
{
    private static readonly Lazy<(MigrationManifest Manifest, string Hash)> Package = new(Load);
    public static MigrationManifest Manifest => Package.Value.Manifest;
    public static string Hash => Package.Value.Hash;

    private static (MigrationManifest, string) Load()
    {
        using var stream =
            typeof(MigrationPackage).Assembly.GetManifestResourceStream(
                "BackupNormalizer.Migrations.json"
            )
            ?? throw new InvalidOperationException(
                "This build has no generated migration package."
            );
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        byte[] bytes = buffer.ToArray();
        var manifest =
            JsonSerializer.Deserialize(bytes, MigrationJsonContext.Default.MigrationManifest)
            ?? throw new InvalidOperationException("Invalid migration package.");
        if (
            manifest.Format != 1
            || manifest.Steps.Length == 0
            || manifest.Steps.Select(s => s.Id).Distinct().Count() != manifest.Steps.Length
        )
            throw new InvalidOperationException("Unsupported migration package.");
        return (manifest, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }
}
