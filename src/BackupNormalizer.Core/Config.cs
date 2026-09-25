using System.Text.Json;

namespace BackupNormalizer;

public sealed class AppConfig
{
    public string Database { get; set; } = "./backup-normalizer.db";
    public string HashAlgorithm { get; set; } = "sha256";
    public int HashParallelism { get; set; } = 2;
    public int CopyParallelism { get; set; } = 1;
    public string TrashDirectoryName { get; set; } = ".backup-normalizer-trash";
    public string MftMode { get; set; } = "off";

    public static string DefaultPath => "./backup-normalizer.json";

    public static AppConfig Load(string? path = null)
    {
        path ??= EnvOrDefault("BN_CONFIG", DefaultPath);
        if (!File.Exists(path)) return new AppConfig();
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<AppConfig>(json, JsonOpts()) ?? new AppConfig();
    }

    public void Save(string? path = null)
    {
        path ??= EnvOrDefault("BN_CONFIG", DefaultPath);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts(true)));
    }

    public static string EnvOrDefault(string name, string fallback)
        => Environment.GetEnvironmentVariable(name) is string v && !string.IsNullOrWhiteSpace(v) ? v : fallback;

    public static JsonSerializerOptions JsonOpts(bool writeIndented = false) => new()
    {
        WriteIndented = writeIndented,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}
