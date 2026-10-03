namespace BackupNormalizer.Tests;

public sealed class ScannerCancellationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bn-cancellation-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string _database;

    public ScannerCancellationTests()
    {
        _root = Path.Combine(_directory, "files");
        _database = Path.Combine(_directory, "inventory.db");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, true);
    }

    private Database Open()
    {
        var db = Database.OpenWritable(_database, pooling: false);
        db.UpsertRoot(new StorageRootRow("r", "Files", _root, true, "unknown", "sensitive", Database.UtcNow()));
        return db;
    }

    [Fact]
    public void Cancellation_Before_Start_Does_Not_Create_A_Scan()
    {
        using var db = Open();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new Scanner(db).ScanRoot("r", cancellationToken: cancellation.Token));
        Assert.Null(db.LatestScan("r"));
    }

    [Fact]
    public void Canceled_Scan_Preserves_Unseen_Entries_And_Records_Incompleteness()
    {
        string first = Path.Combine(_root, "first.txt");
        string unseen = Path.Combine(_root, "unseen.txt");
        File.WriteAllText(first, "first");
        File.WriteAllText(unseen, "unseen");
        using var db = Open();
        new Scanner(db, usnMode: "off").ScanRoot("r");
        File.Delete(unseen);
        using var cancellation = new CancellationTokenSource();
        IEnumerable<FsEntry> Enumerate(string _)
        {
            yield return new FsEntry(first, false, 0, default, default, false, false, null);
            cancellation.Cancel();
        }

        Assert.Throws<OperationCanceledException>(() => new Scanner(db, Enumerate)
            .ScanRoot("r", cancellationToken: cancellation.Token));
        Assert.Equal(ScanStatus.Incomplete, db.LatestScanStatus("r"));
        Assert.Equal(FileStatus.Ok, db.GetFileEntry("r", "unseen.txt")!.Status);
        var status = db.GetInventoryStatus("r");
        Assert.Equal(0, status.LatestScan!.ErrorCount);
        Assert.Contains("canceled", status.LatestScan.FallbackReason);
    }

    private sealed class InlineProgress(Action<HashProgress> report) : IProgress<HashProgress>
    {
        public void Report(HashProgress value) => report(value);
    }

    [Fact]
    public void Canceling_During_Read_Does_Not_Store_A_Partial_Hash_And_Resume_Works()
    {
        File.WriteAllBytes(Path.Combine(_root, "large.bin"), new byte[12 * 1024 * 1024]);
        using var db = Open();
        var scanner = new Scanner(db, usnMode: "off");
        scanner.ScanRoot("r");
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress(p => { if (p.BytesRead > 0) cancellation.Cancel(); });
        Assert.Throws<OperationCanceledException>(() => scanner.HashNeeded("r", parallelism: 1,
            progress: progress, cancellationToken: cancellation.Token));
        Assert.Null(db.GetHash(db.GetFileEntry("r", "large.bin")!.Id, "sha256"));
        Assert.Equal((1, 0, 0), scanner.HashNeeded("r", parallelism: 1));
    }
}
