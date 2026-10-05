using System.Text.Json;
using BackupNormalizer;

if (args is not [var output])
    throw new ArgumentException("Pass the generated migration manifest path.");
var manifest = MigrationManifestGenerator.Create();
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
File.WriteAllText(
    output,
    JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + "\n"
);
Console.WriteLine($"Generated {manifest.Steps.Length} forward migrations: {output}");
