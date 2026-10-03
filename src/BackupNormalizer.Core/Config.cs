using System.Text.Json;
using System.Text.Json.Serialization;

namespace BackupNormalizer;

public sealed class AppConfig
{
    public string Database { get; set; } = "./backup-normalizer.db";
    public string HashAlgorithm { get; set; } = "sha256";
    public int HashParallelism { get; set; } = 2;
    public int CopyParallelism { get; set; } = 1;
    public string TrashDirectoryName { get; set; } = ".backup-normalizer-trash";
    public string MftMode { get; set; } = "off";
    public string UsnMode { get; set; } = "auto";
    public bool NoProgress { get; set; }
    public string[]? ExcludedPathRegexes { get; set; }

    public static string DefaultPath => "./settings.json";

    public static AppConfig Load(string? path = null, bool allowMissing = false)
    {
        bool selected = path != null || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BN_CONFIG"));
        path ??= EnvOrDefault("BN_CONFIG", DefaultPath);
        if (!selected && !File.Exists(path) && File.Exists("./backup-normalizer.json"))
            path = "./backup-normalizer.json";
        if (!File.Exists(path))
        {
            if (selected && !allowMissing) throw new FileNotFoundException($"Config file not found: {path}", path);
            return new AppConfig();
        }
        AppConfig config;
        try
        {
            config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOpts())
                ?? throw new JsonException("Expected a JSON object.");
        }
        catch (JsonException ex) { throw new InvalidOperationException($"Invalid JSON config '{path}': {ex.Message}", ex); }
        config.Validate();
        return config;
    }

    public void Save(string? path = null, bool overwrite = true)
    {
        Validate();
        path ??= EnvOrDefault("BN_CONFIG", DefaultPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = new FileStream(path, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(stream, this, JsonOpts(true));
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Database)) throw new ArgumentException("Config database must not be empty.");
        if (HashAlgorithm?.ToLowerInvariant() is not ("sha256" or "blake3"))
            throw new ArgumentException("Config hashAlgorithm must be sha256 or blake3.");
        HashAlgorithm = HasherFactory.NormalizeAlgorithm(HashAlgorithm);
        if (HashParallelism < 1 || CopyParallelism < 1) throw new ArgumentException("Config parallelism must be positive.");
        if (string.IsNullOrWhiteSpace(TrashDirectoryName)) throw new ArgumentException("Config trashDirectoryName must not be empty.");
        MftMode = MftMode?.ToLowerInvariant() ?? "";
        UsnMode = UsnMode?.ToLowerInvariant() ?? "";
        if (MftMode is not ("off" or "auto" or "require")) throw new ArgumentException("Config mftMode must be off, auto, or require.");
        if (UsnMode is not ("off" or "auto")) throw new ArgumentException("Config usnMode must be off or auto.");
        if (ExcludedPathRegexes != null) _ = new PathExclusions(ExcludedPathRegexes);
    }

    public static string EnvOrDefault(string name, string fallback)
        => Environment.GetEnvironmentVariable(name) is string v && !string.IsNullOrWhiteSpace(v) ? v : fallback;

    public static JsonSerializerOptions JsonOpts(bool writeIndented = false) => new()
    {
        WriteIndented = writeIndented,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
}
