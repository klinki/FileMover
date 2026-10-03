using BackupNormalizer.Ui.Models;
using BackupNormalizer.Ui.ViewModels;

namespace BackupNormalizer.Tests;

public sealed class UiInventoryJobTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bn-ui-jobs-" + Guid.NewGuid().ToString("N"));
    private string RootPath => Path.Combine(_directory, "files");
    private string DatabasePath => Path.Combine(_directory, "inventory.db");

    public UiInventoryJobTests() => Directory.CreateDirectory(RootPath);

    public void Dispose()
    {
        Assert.StartsWith(Path.GetTempPath(), Path.GetFullPath(_directory));
        Directory.Delete(_directory, true);
    }

    private void CreateInventory()
    {
        using var db = Database.OpenWritable(DatabasePath, pooling: false);
        db.UpsertRoot(new StorageRootRow("r", "Files", RootPath, true, "local", "sensitive", Database.UtcNow()));
    }

    private void ScanExistingFiles()
    {
        using var db = Database.OpenWritable(DatabasePath, pooling: false);
        new Scanner(db, usnMode: "off").ScanRoot("r");
    }

    private InventoryJobRequest Request(InventoryJobKind kind) =>
        new(DatabasePath, "r", RootPath, kind, Parallelism: 2);

    [Fact]
    public async Task Hash_Needed_Reuses_A_Current_Hash()
    {
        string file = Path.Combine(RootPath, "keep.txt");
        File.WriteAllText(file, "same contents");
        CreateInventory();
        ScanExistingFiles();
        FileHashRow before;
        using (var db = Database.OpenWritable(DatabasePath, pooling: false))
        {
            var scanner = new Scanner(db, usnMode: "off");
            Assert.Equal((1, 0, 0), scanner.HashNeeded("r", parallelism: 1));
            var entry = db.GetFileEntry("r", "keep.txt")!;
            before = db.GetHash(entry.Id, "sha256")!;
        }

        var result = await new InventoryJobRunner().RunAsync(Request(InventoryJobKind.HashNeeded));

        Assert.Equal(InventoryJobOutcome.Completed, result.Outcome);
        Assert.Equal(0, result.Hashed);
        Assert.Equal(1, result.Skipped);
        using var verify = Database.OpenReadOnly(DatabasePath, pooling: false);
        var after = verify.GetHash(verify.GetFileEntry("r", "keep.txt")!.Id, "sha256");
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Scan_Then_Hash_Does_Not_Hash_After_An_Incomplete_Scan()
    {
        string file = Path.Combine(RootPath, "known.txt");
        File.WriteAllText(file, "needs a hash");
        CreateInventory();
        ScanExistingFiles();

        var runner = new InventoryJobRunner(db => new Scanner(db, _ =>
            [new FsEntry(Path.Combine(RootPath, "denied.txt"), false, 0, default, default, false, false, "Access denied.")]));
        var result = await runner.RunAsync(Request(InventoryJobKind.ScanThenHash));

        Assert.Equal(InventoryJobOutcome.Incomplete, result.Outcome);
        Assert.Equal(1, result.ScanErrors);
        Assert.Contains("Hashing was skipped", result.Message);
        using var verify = Database.OpenReadOnly(DatabasePath, pooling: false);
        Assert.Equal(ScanStatus.Incomplete, verify.LatestScanStatus("r"));
        Assert.Null(verify.GetHash(verify.GetFileEntry("r", "known.txt")!.Id, "sha256"));
    }

    [Fact]
    public async Task Hash_Needed_Reports_Remaining_Unreadable_Files_As_Incomplete()
    {
        string file = Path.Combine(RootPath, "gone.txt");
        File.WriteAllText(file, "gone before hashing");
        CreateInventory();
        ScanExistingFiles();
        File.Delete(file);

        var result = await new InventoryJobRunner().RunAsync(Request(InventoryJobKind.HashNeeded));

        Assert.Equal(InventoryJobOutcome.Incomplete, result.Outcome);
        Assert.Contains("1 regular file", result.Message);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public async Task Cancellation_Does_Not_Store_A_Partial_Hash_And_Resume_Completes()
    {
        File.WriteAllBytes(Path.Combine(RootPath, "large.bin"), new byte[8 * 1024 * 1024]);
        CreateInventory();
        ScanExistingFiles();
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress(value =>
        {
            if (value.Stage == InventoryJobStage.Hashing && value.BytesRead > 0)
                cancellation.Cancel();
        });

        var runner = new InventoryJobRunner();
        var canceled = await runner.RunAsync(Request(InventoryJobKind.HashNeeded), progress, cancellation.Token);

        Assert.Equal(InventoryJobOutcome.Canceled, canceled.Outcome);
        using (var verify = Database.OpenReadOnly(DatabasePath, pooling: false))
        {
            var entry = verify.GetFileEntry("r", "large.bin")!;
            Assert.Null(verify.GetHash(entry.Id, "sha256"));
        }

        var resumed = await runner.RunAsync(Request(InventoryJobKind.HashNeeded));
        Assert.Equal(InventoryJobOutcome.Completed, resumed.Outcome);
        Assert.Equal(1, resumed.Hashed);
    }

    [Fact]
    public async Task Missing_Database_Fails_Before_Creating_Or_Modifying_It()
    {
        string missing = Path.Combine(_directory, "absent", "inventory.db");
        var request = new InventoryJobRequest(missing, "r", RootPath, InventoryJobKind.Scan);

        var result = await new InventoryJobRunner().RunAsync(request);

        Assert.Equal(InventoryJobOutcome.Failed, result.Outcome);
        Assert.False(File.Exists(missing));
        Assert.False(Directory.Exists(Path.GetDirectoryName(missing)));
    }

    [Fact]
    public async Task Unavailable_Root_Fails_Without_Modifying_The_Database()
    {
        CreateInventory();
        byte[] before = File.ReadAllBytes(DatabasePath);
        string unavailable = Path.Combine(_directory, "offline-root");

        var result = await new InventoryJobRunner().RunAsync(
            new InventoryJobRequest(DatabasePath, "r", unavailable, InventoryJobKind.Scan));

        Assert.Equal(InventoryJobOutcome.Failed, result.Outcome);
        Assert.Contains("root path is not available", result.Message);
        Assert.Equal(before, File.ReadAllBytes(DatabasePath));
    }

    [Fact]
    public async Task Main_View_Model_Retains_Result_And_Refreshes_Both_Panels_On_The_Same_Database()
    {
        File.WriteAllText(Path.Combine(RootPath, "new.txt"), "hash me");
        CreateInventory();
        ScanExistingFiles();
        var main = new MainViewModel();
        await main.LoadDatabaseAsync("Left", DatabasePath);
        await main.LoadDatabaseAsync("Right", DatabasePath);
        var oldLeft = main.Left.Snapshot;
        var oldRight = main.Right.Snapshot;
        Assert.True(main.CanRunInventoryJob);

        await main.RunInventoryJobAsync(InventoryJobKind.HashNeeded);

        Assert.False(main.IsBusy);
        Assert.Equal(InventoryJobOutcome.Completed, main.Job.LastResult?.Outcome);
        Assert.NotSame(oldLeft, main.Left.Snapshot);
        Assert.NotSame(oldRight, main.Right.Snapshot);
        Assert.Equal(1, main.Left.SelectedInventoryRoot!.HealthStatus!.UsableHashes);
        Assert.Equal(1, main.Right.SelectedInventoryRoot!.HealthStatus!.UsableHashes);
    }

    private sealed class InlineProgress(Action<InventoryJobProgress> report) : IProgress<InventoryJobProgress>
    {
        public void Report(InventoryJobProgress value) => report(value);
    }
}
