using BackupNormalizer.Ui.Models;
using BackupNormalizer.Ui.ViewModels;

namespace BackupNormalizer.Tests;

[Collection("Console")]
public sealed class UiInventoryCreationTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "bn-ui-create-" + Guid.NewGuid().ToString("N")
    );
    private string RootPath => Path.Combine(_directory, "files");
    private string DatabasePath => Path.Combine(_directory, "inventory.db");

    public UiInventoryCreationTests() => Directory.CreateDirectory(RootPath);

    public void Dispose()
    {
        Assert.StartsWith(Path.GetTempPath(), Path.GetFullPath(_directory));
        Directory.Delete(_directory, true);
    }

    private InventoryJobRequest Request(InventoryJobKind kind = InventoryJobKind.ScanThenHash) =>
        new(
            DatabasePath,
            "r",
            RootPath,
            kind,
            CreateInventory: true,
            Configuration: new AppConfig { MftMode = "off", UsnMode = "off" }
        );

    [Fact]
    public async Task New_Inventory_Registers_Scans_And_Hashes_The_Folder_With_Config_Exclusions()
    {
        File.WriteAllText(Path.Combine(RootPath, "keep.txt"), "keep this");
        Directory.CreateDirectory(Path.Combine(RootPath, "cache"));
        File.WriteAllText(Path.Combine(RootPath, "cache", "skip.txt"), "excluded");
        var request = Request();
        request.Configuration!.ExcludedPathRegexes = ["^cache(/|$)"];

        var result = await new InventoryJobRunner().RunAsync(request);

        Assert.Equal(InventoryJobOutcome.Completed, result.Outcome);
        Assert.Equal(1, result.Hashed);
        using var db = Database.OpenReadOnly(DatabasePath, pooling: false);
        Assert.Equal(RootPath, db.GetRoot("r")!.Path);
        Assert.Equal(ScanStatus.Completed, db.LatestScanStatus("r"));
        Assert.NotNull(db.GetHash(db.GetFileEntry("r", "keep.txt")!.Id, "sha256"));
        Assert.Null(db.GetFileEntry("r", "cache/skip.txt"));
        Assert.Equal(["^cache(/|$)"], db.GetExcludedPathRegexes("r"));
        Assert.Equal("keep this", File.ReadAllText(Path.Combine(RootPath, "keep.txt")));
    }

    [Fact]
    public async Task Scan_Only_Does_Not_Hash_And_Excludes_Its_Own_Database()
    {
        File.WriteAllText(Path.Combine(RootPath, "keep.txt"), "contents");
        string destination = Path.Combine(RootPath, "inventory.db");

        var result = await new InventoryJobRunner().RunAsync(
            Request(InventoryJobKind.Scan) with
            {
                DatabasePath = destination,
            }
        );

        Assert.Equal(InventoryJobOutcome.Completed, result.Outcome);
        Assert.Equal(0, result.Hashed);
        using var db = Database.OpenReadOnly(destination, pooling: false);
        Assert.Null(db.GetHash(db.GetFileEntry("r", "keep.txt")!.Id, "sha256"));
        Assert.Null(db.GetFileEntry("r", "inventory.db"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-wal")]
    [InlineData("-shm")]
    [InlineData("-journal")]
    public async Task Creation_Refuses_Existing_Databases_And_Sidecars(string suffix)
    {
        byte[] original = [1, 2, 3];
        string existing = DatabasePath + suffix;
        File.WriteAllBytes(existing, original);

        var result = await new InventoryJobRunner().RunAsync(Request());

        Assert.Equal(InventoryJobOutcome.Failed, result.Outcome);
        Assert.Equal(original, File.ReadAllBytes(existing));
        if (suffix.Length > 0)
        {
            Assert.False(File.Exists(DatabasePath));
        }
    }

    [Fact]
    public async Task Invalid_Config_Or_Unavailable_Folder_Fails_Before_Creating_Files()
    {
        var request = Request() with
        {
            DatabasePath = Path.Combine(_directory, "new", "inventory.db"),
        };
        request.Configuration!.HashParallelism = 0;
        var invalid = await new InventoryJobRunner().RunAsync(request);
        Assert.Equal(InventoryJobOutcome.Failed, invalid.Outcome);
        Assert.False(Directory.Exists(Path.GetDirectoryName(request.DatabasePath)));

        var unavailable = await new InventoryJobRunner().RunAsync(
            Request() with
            {
                RecordedRootPath = Path.Combine(_directory, "offline"),
            }
        );
        Assert.Equal(InventoryJobOutcome.Failed, unavailable.Outcome);
        Assert.False(File.Exists(DatabasePath));
    }

    [Fact]
    public async Task Canceled_New_Scan_Leaves_A_Browsable_Incomplete_Inventory()
    {
        for (int i = 0; i < 5; i++)
        {
            File.WriteAllText(Path.Combine(RootPath, i + ".txt"), "contents");
        }
        using var cancellation = new CancellationTokenSource();
        var progress = new CancelAfterFirstEntry(cancellation);

        var result = await new InventoryJobRunner().RunAsync(
            Request(),
            progress,
            cancellation.Token
        );

        Assert.Equal(InventoryJobOutcome.Canceled, result.Outcome);
        using var db = Database.OpenReadOnly(DatabasePath, pooling: false);
        Assert.Equal(ScanStatus.Incomplete, db.LatestScanStatus("r"));
        Assert.NotNull(db.GetRoot("r"));
        Assert.NotEmpty(InventorySnapshot.Load(DatabasePath).Roots);
    }

    [Fact]
    public async Task Cancellation_Before_Starting_Does_Not_Create_A_Database()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await new InventoryJobRunner().RunAsync(
            Request(),
            cancellationToken: cancellation.Token
        );
        Assert.Equal(InventoryJobOutcome.Canceled, result.Outcome);
        Assert.False(File.Exists(DatabasePath));
    }

    [Fact]
    public async Task Desktop_Config_Defaults_Create_A_New_Inventory_In_The_Active_Panel()
    {
        File.WriteAllText(Path.Combine(RootPath, "keep.txt"), "contents");
        string configPath = Path.Combine(_directory, "settings.json");
        new AppConfig
        {
            Database = Path.GetRelativePath(Environment.CurrentDirectory, DatabasePath),
            HashParallelism = 1,
            MftMode = "off",
            UsnMode = "off",
        }.Save(configPath);
        var main = new MainViewModel { IsLeftActive = false };
        main.Right.CurrentPath = RootPath;
        Assert.True(main.LoadConfiguration(configPath));
        var setup = new InventoryScanViewModel(main) { RootId = "r" };
        var request = setup.CreateRequest();
        Assert.Equal(DatabasePath, request.DatabasePath);
        Assert.Equal(RootPath, request.RecordedRootPath);
        Assert.Equal(1, request.Parallelism);
        Assert.False(File.Exists(DatabasePath));

        await main.RunNewInventoryJobAsync(request);

        Assert.Equal(InventoryJobOutcome.Completed, main.Job.LastResult!.Outcome);
        Assert.True(main.Left.IsLive);
        Assert.Equal(DatabasePath, main.Right.Snapshot!.DatabasePath);
        Assert.Equal("r", main.Right.SelectedInventoryRoot!.Root.Id);
        Assert.Equal(1, main.Right.SelectedInventoryRoot.HealthStatus!.UsableHashes);
        Assert.False(main.IsBusy);
    }

    [Fact]
    public async Task Existing_Jobs_Use_Config_Settings_And_Keep_Their_Selected_Database()
    {
        File.WriteAllText(Path.Combine(RootPath, "keep.txt"), "contents");
        File.WriteAllText(Path.Combine(RootPath, "skip.tmp"), "excluded on rescan");
        await new InventoryJobRunner().RunAsync(Request(InventoryJobKind.Scan));
        string configPath = Path.Combine(_directory, "settings.json");
        new AppConfig
        {
            Database = Path.Combine(_directory, "other.db"),
            MftMode = "off",
            UsnMode = "off",
            ExcludedPathRegexes = ["\\.tmp$"],
        }.Save(configPath);
        var main = new MainViewModel();
        Assert.True(main.LoadConfiguration(configPath));
        await main.LoadDatabaseAsync("Left", DatabasePath);

        await main.RunInventoryJobAsync(InventoryJobKind.ScanThenHash);

        Assert.Equal(InventoryJobOutcome.Completed, main.Job.LastResult!.Outcome);
        Assert.Equal(DatabasePath, main.Left.Snapshot!.DatabasePath);
        Assert.False(File.Exists(main.Configuration.Database));
        using var db = Database.OpenReadOnly(DatabasePath, pooling: false);
        Assert.Equal(["\\.tmp$"], db.GetExcludedPathRegexes("r"));
        Assert.Equal(FileStatus.Missing, db.GetFileEntry("r", "skip.tmp")!.Status);
    }

    [Fact]
    public void Config_Load_Errors_Keep_The_Previous_Settings()
    {
        string configPath = Path.Combine(_directory, "settings.json");
        new AppConfig { Database = DatabasePath, HashParallelism = 3 }.Save(configPath);
        var main = new MainViewModel();
        Assert.True(main.LoadConfiguration(configPath));
        var original = main.Configuration;
        Assert.False(main.LoadConfiguration(Path.Combine(_directory, "missing.json")));
        foreach (
            string json in new[]
            {
                "{",
                "{\"unknown\":1}",
                "{\"hashParallelism\":0}",
                "{\"excludedPathRegexes\":[\"[\"]}",
            }
        )
        {
            File.WriteAllText(configPath, json);
            Assert.False(main.LoadConfiguration(configPath));
            Assert.Same(original, main.Configuration);
        }
        Assert.Equal(configPath, main.ConfigurationPath);
        Assert.False(File.Exists(DatabasePath));
    }

    private sealed class CancelAfterFirstEntry(CancellationTokenSource cancellation)
        : IProgress<InventoryJobProgress>
    {
        public void Report(InventoryJobProgress value)
        {
            if (value.Stage == InventoryJobStage.Scanning && value.Scanned > 0)
            {
                cancellation.Cancel();
            }
        }
    }
}
