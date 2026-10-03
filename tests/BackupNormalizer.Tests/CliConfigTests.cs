using System.Text.Json;
using System.Diagnostics;
using BackupNormalizer;
using Microsoft.Data.Sqlite;

namespace BackupNormalizer.Tests;

[Collection("Console")]
public sealed class CliConfigTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bn-config-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _environment = new();
    private string Root => Path.Combine(_directory, "data");
    private string DbPath => Path.Combine(_directory, "inventory.db");
    private string ConfigPath => Path.Combine(_directory, "settings.json");

    public CliConfigTests()
    {
        Directory.CreateDirectory(Root);
        foreach (string name in new[] { "BN_CONFIG", "BN_DB", "BN_MFT", "BN_USN", "BN_HASH_ALGO", "BN_PARALLELISM" })
        {
            _environment[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    public void Dispose()
    {
        foreach (var pair in _environment) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        foreach (string path in Directory.EnumerateFiles(_directory, "*.db"))
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, ForeignKeys = true }.ToString());
            SqliteConnection.ClearPool(connection);
        }
        Directory.Delete(_directory, true);
    }

    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        bool originalJson = Log.Json;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            int code = Cli.Run(args);
            return (code, output.ToString(), error.ToString());
        }
        finally { Console.SetOut(originalOut); Console.SetError(originalError); Log.Json = originalJson; }
    }

    private void SaveConfig(string[]? patterns = null) => new AppConfig
    { Database = DbPath, UsnMode = "off", HashParallelism = 3, NoProgress = true, ExcludedPathRegexes = patterns }.Save(ConfigPath);

    private void Register()
    {
        using var db = Database.OpenWritable(DbPath, pooling: false);
        db.UpsertRoot(new("r", "r", Root, true, "fs", "sensitive", Database.UtcNow()));
    }

    private (int Code, string Output, string Error) RunInDirectory(params string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(Cli).Assembly.Location);
        foreach (string argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("CLI config command did not finish.");
        }
        return (process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
    }

    [Fact]
    public void Implicit_Settings_File_Takes_Priority_Over_Legacy_File()
    {
        SaveConfig(["^cache$"]);
        new AppConfig { Database = "./legacy.db" }.Save(Path.Combine(_directory, "backup-normalizer.json"));
        var shown = RunInDirectory("config", "show");
        Assert.Equal(0, shown.Code);
        using var json = JsonDocument.Parse(shown.Output);
        Assert.Equal(DbPath, json.RootElement.GetProperty("database").GetString());
        Assert.Equal("^cache$", json.RootElement.GetProperty("excludedPathRegexes")[0].GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Explicit_Config_Overrides_Implicit_Settings_And_Environment(bool beforeCommand)
    {
        File.WriteAllText(ConfigPath, "{invalid implicit config");
        Environment.SetEnvironmentVariable("BN_CONFIG", Path.Combine(_directory, "missing-environment.json"));
        string explicitPath = Path.Combine(_directory, "other.json");
        new AppConfig { Database = "./explicit.db" }.Save(explicitPath);
        var shown = beforeCommand ? RunInDirectory("--config", explicitPath, "config", "show")
            : RunInDirectory("config", "show", "--config", explicitPath);
        Assert.Equal(0, shown.Code);
        using var json = JsonDocument.Parse(shown.Output);
        Assert.Equal("./explicit.db", json.RootElement.GetProperty("database").GetString());
    }

    [Fact]
    public void Environment_Config_Takes_Priority_Over_Implicit_Settings()
    {
        SaveConfig();
        string environmentPath = Path.Combine(_directory, "environment.json");
        new AppConfig { Database = "./environment.db" }.Save(environmentPath);
        Environment.SetEnvironmentVariable("BN_CONFIG", environmentPath);
        var shown = RunInDirectory("config", "show");
        Assert.Equal(0, shown.Code);
        using var json = JsonDocument.Parse(shown.Output);
        Assert.Equal("./environment.db", json.RootElement.GetProperty("database").GetString());
    }

    [Fact]
    public void Missing_Explicit_Config_Does_Not_Fall_Back_To_Settings()
    {
        SaveConfig();
        var shown = RunInDirectory("config", "show", "--config", "missing.json");
        Assert.Equal(2, shown.Code);
        Assert.Contains("Config file not found", shown.Output + shown.Error);
    }

    [Fact]
    public void Missing_Settings_File_Uses_Legacy_Config_When_Present()
    {
        new AppConfig { Database = "./legacy.db" }.Save(Path.Combine(_directory, "backup-normalizer.json"));
        var shown = RunInDirectory("config", "show");
        Assert.Equal(0, shown.Code);
        using var json = JsonDocument.Parse(shown.Output);
        Assert.Equal("./legacy.db", json.RootElement.GetProperty("database").GetString());
    }

    [Fact]
    public void Missing_Implicit_Files_Use_Built_In_Defaults()
    {
        var shown = RunInDirectory("config", "show");
        Assert.Equal(0, shown.Code);
        using var json = JsonDocument.Parse(shown.Output);
        Assert.Equal(new AppConfig().Database, json.RootElement.GetProperty("database").GetString());
    }

    [Fact]
    public void Config_Init_Defaults_To_Settings_Json()
    {
        Assert.Equal(0, RunInDirectory("config", "init").Code);
        Assert.True(File.Exists(ConfigPath));
        Assert.False(File.Exists(Path.Combine(_directory, "backup-normalizer.json")));
    }

    [Fact]
    public void Config_Init_Creates_Json_Without_A_Database_And_Refuses_Overwrite()
    {
        Assert.Equal(0, Run("config", "init", "--config", ConfigPath, "--db", DbPath).Code);
        Assert.False(File.Exists(DbPath));
        var config = AppConfig.Load(ConfigPath);
        Assert.Equal(DbPath, config.Database);
        byte[] before = File.ReadAllBytes(ConfigPath);
        Assert.Equal(2, Run("config", "init", "--config", ConfigPath).Code);
        Assert.Equal(before, File.ReadAllBytes(ConfigPath));
        var shown = Run("--config", ConfigPath, "config", "show");
        Assert.Equal(0, shown.Code);
        using var json = JsonDocument.Parse(shown.Output);
        Assert.Equal(DbPath, json.RootElement.GetProperty("database").GetString());
    }

    [Fact]
    public void Init_Can_Create_An_Explicit_Config_File()
    {
        Assert.Equal(0, Run("init", "--db", DbPath, "--config", ConfigPath).Code);
        Assert.Equal(DbPath, AppConfig.Load(ConfigPath).Database);
        Assert.True(File.Exists(DbPath));
    }

    [Fact]
    public void Cli_Then_Environment_Then_Json_Select_The_Database()
    {
        SaveConfig();
        string environmentDb = Path.Combine(_directory, "environment.db");
        string cliDb = Path.Combine(_directory, "cli.db");
        Assert.Equal(0, Run("--config", ConfigPath, "root", "add", "json", Root).Code);
        Environment.SetEnvironmentVariable("BN_CONFIG", ConfigPath);
        Environment.SetEnvironmentVariable("BN_DB", environmentDb);
        Assert.Equal(0, Run("root", "add", "environment", Root).Code);
        Assert.Equal(0, Run("root", "add", "cli", Root, "--db", cliDb).Code);
        using var json = Database.OpenReadOnly(DbPath, pooling: false);
        using var environment = Database.OpenReadOnly(environmentDb, pooling: false);
        using var explicitDb = Database.OpenReadOnly(cliDb, pooling: false);
        Assert.Equal("json", Assert.Single(json.ListRoots()).Id);
        Assert.Equal("environment", Assert.Single(environment.ListRoots()).Id);
        Assert.Equal("cli", Assert.Single(explicitDb.ListRoots()).Id);
    }

    [Fact]
    public void Explicit_Missing_Or_Invalid_Config_Fails_Before_Database_Creation()
    {
        var missing = Run("root", "list", "--config", ConfigPath, "--db", DbPath);
        Assert.Equal(2, missing.Code);
        Assert.Contains("Config file not found", missing.Output + missing.Error);
        Assert.False(File.Exists(DbPath));
        Environment.SetEnvironmentVariable("BN_CONFIG", ConfigPath);
        Assert.Equal(2, Run("root", "list", "--db", DbPath).Code);
        File.WriteAllText(ConfigPath, "{broken");
        var invalid = Run("root", "list", "--config", ConfigPath, "--db", DbPath);
        Assert.Equal(2, invalid.Code);
        Assert.Contains("Invalid JSON config", invalid.Output + invalid.Error);
        Assert.False(File.Exists(DbPath));
    }

    [Theory]
    [InlineData("{\"hashParallelism\":0}")]
    [InlineData("{\"mftMode\":\"wrong\"}")]
    [InlineData("{\"usnMode\":null}")]
    [InlineData("{\"hashAlgorithm\":\"wrong\"}")]
    [InlineData("{\"excludedPathRegexes\":[\"[\"]}")]
    [InlineData("{\"excludedPathRegexes\":[null]}")]
    public void Invalid_Config_Values_Are_Rejected(string json)
    {
        File.WriteAllText(ConfigPath, json);
        Assert.ThrowsAny<ArgumentException>(() => AppConfig.Load(ConfigPath));
    }

    [Fact]
    public void Config_Is_Case_Insensitive_And_Rejects_Unknown_Properties()
    {
        File.WriteAllText(ConfigPath, "{\"HashParallelism\":4,\"MftMode\":\"OFF\"}");
        Assert.Equal(4, AppConfig.Load(ConfigPath).HashParallelism);
        File.WriteAllText(ConfigPath, "{\"excludedPathRegex\":[\"cache\"]}");
        Assert.Contains("excludedPathRegex", Assert.Throws<InvalidOperationException>(() => AppConfig.Load(ConfigPath)).Message);
    }

    [Fact]
    public void Scan_Uses_Json_Regexes_Cli_Overrides_And_Explicit_Reset()
    {
        File.WriteAllText(Path.Combine(Root, "keep.txt"), "content");
        File.WriteAllText(Path.Combine(Root, "skip.tmp"), "temporary");
        Directory.CreateDirectory(Path.Combine(Root, "cache"));
        File.WriteAllText(Path.Combine(Root, "cache/child.txt"), "cached");
        SaveConfig(["\\.tmp$"]);
        Register();
        Assert.Equal(0, Run("scan", "r", "--config", ConfigPath).Code);
        using (var db = Database.OpenReadOnly(DbPath, pooling: false))
            Assert.Equal(new[] { "cache/child.txt", "keep.txt" }, db.ListFiles("r").Where(f => f.Status == FileStatus.Ok).Select(f => f.RelativePath).Order());
        Assert.Equal(0, Run("scan", "r", "--config", ConfigPath, "--exclude-path-regex", "^cache$", "--exclude-path-regex", "^keep\\.txt$").Code);
        using (var db = Database.OpenReadOnly(DbPath, pooling: false))
            Assert.Equal("skip.tmp", Assert.Single(db.ListFiles("r"), f => f.Status == FileStatus.Ok).RelativePath);
        Assert.Equal(0, Run("scan", "r", "--config", ConfigPath, "--no-exclusions").Code);
        using var all = Database.OpenReadOnly(DbPath, pooling: false);
        Assert.Equal(3, all.ListFiles("r").Count(f => f.Status == FileStatus.Ok));
        Assert.Empty(all.GetExcludedPathRegexes("r"));
    }

    [Fact]
    public void Invalid_Regex_And_Missing_Option_Values_Are_Actionable_Errors()
    {
        SaveConfig();
        var invalid = Run("scan", "r", "--config", ConfigPath, "--exclude-path-regex", "[");
        Assert.Equal(2, invalid.Code);
        Assert.Contains("Invalid exclusion regex", invalid.Output + invalid.Error);
        Assert.False(File.Exists(DbPath));
        Assert.Equal(2, Run("scan", "r", "--config", ConfigPath, "--exclude-path-regex").Code);
        Assert.Equal(2, Run("scan", "r", "--config").Code);
        Assert.Equal(2, Run("scan", "r", "--config", ConfigPath, "--no-exclusions", "--exclude-path-regex", "cache").Code);
    }

    [Fact]
    public void Hash_Uses_Config_And_Rejects_Invalid_Parallelism_Before_Work()
    {
        File.WriteAllText(Path.Combine(Root, "keep.txt"), "content");
        SaveConfig();
        Register();
        Assert.Equal(0, Run("scan", "r", "--config", ConfigPath).Code);
        Assert.Equal(2, Run("hash", "--needed", "--config", ConfigPath, "--parallelism", "0").Code);
        Assert.Equal(0, Run("hash", "--needed", "--config", ConfigPath).Code);
        using var db = Database.OpenReadOnly(DbPath, pooling: false);
        Assert.True(db.GetInventoryStatus("r").PlanningReady);
    }
}
