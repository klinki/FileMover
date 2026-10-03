using BackupNormalizer;
using Microsoft.Data.Sqlite;

namespace BackupNormalizer.Tests;

[Collection("Console")]
public sealed class ScanErrorTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "bn-scan-errors-" + Guid.NewGuid().ToString("N")
    );
    private readonly string _root;
    private readonly string _dbPath;

    public ScanErrorTests()
    {
        _root = Path.Combine(_directory, "data");
        _dbPath = Path.Combine(_directory, "inventory.db");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                ForeignKeys = true,
            }.ToString()
        );
        SqliteConnection.ClearPool(connection);
        Directory.Delete(_directory, true);
    }

    private Database CreateDatabase(string? rootPath = null)
    {
        var db = new Database(_dbPath);
        db.UpsertRoot(
            new StorageRootRow(
                "d",
                "d",
                rootPath ?? _root,
                true,
                "fs",
                "unknown",
                Database.UtcNow()
            )
        );
        return db;
    }

    [Fact]
    public void Enumeration_And_Metadata_Errors_Report_Details_And_Preserve_Unseen_Entries()
    {
        string good = Path.Combine(_root, "good.txt");
        File.WriteAllText(good, "content");
        File.WriteAllText(Path.Combine(_root, "unseen.txt"), "preserve");
        using var db = CreateDatabase();
        Assert.Equal((2, 0), new Scanner(db).ScanRoot("d"));
        string denied = Path.Combine(_root, "protected");
        string invalid = Path.Combine(_root, "invalid\0.txt");
        var diagnostics = new List<ScanError>();
        var scanner = new Scanner(
            db,
            _ =>
                [
                    new FsEntry(
                        denied,
                        true,
                        0,
                        default,
                        default,
                        false,
                        false,
                        "Directory enumeration denied."
                    ),
                    new FsEntry(invalid, false, 0, default, default, false, false, null),
                    new FsEntry(good, false, 0, default, default, false, false, null),
                ]
        );

        Assert.Equal((1, 2), scanner.ScanRoot("d", onError: diagnostics.Add));

        Assert.Equal(2, diagnostics.Count);
        Assert.Equal(new ScanError("d", denied, "Directory enumeration denied."), diagnostics[0]);
        Assert.Equal(invalid, diagnostics[1].Path);
        Assert.Equal("d", diagnostics[1].RootId);
        Assert.False(string.IsNullOrWhiteSpace(diagnostics[1].Message));
        Assert.Equal(ScanStatus.Incomplete, db.LatestScanStatus("d"));
        Assert.Equal(FileStatus.Ok, db.GetFileEntry("d", "unseen.txt")!.Status);
        Assert.Equal(FileStatus.Ok, db.GetFileEntry("d", "good.txt")!.Status);
        Assert.Null(db.GetFileEntry("d", "protected"));
    }

    [Fact]
    public void Failing_Diagnostic_Observers_Do_Not_Change_Scan_Results()
    {
        using var db = CreateDatabase();
        var scanner = new Scanner(
            db,
            _ =>
                [
                    new FsEntry(
                        Path.Combine(_root, "protected"),
                        true,
                        0,
                        default,
                        default,
                        false,
                        false,
                        "Access denied."
                    ),
                ]
        );
        int calls = 0;

        var result = scanner.ScanRoot(
            "d",
            onError: _ =>
            {
                calls++;
                throw new IOException("Diagnostic sink failed.");
            }
        );

        Assert.Equal((0, 1), result);
        Assert.Equal(1, calls);
        Assert.Equal(ScanStatus.Incomplete, db.LatestScanStatus("d"));
    }

    [Fact]
    public void Fatal_Scan_Failures_Report_The_Root_Path_And_Remain_Failed()
    {
        string missingRoot = Path.Combine(_directory, "missing-root");
        using var db = CreateDatabase(missingRoot);
        var diagnostics = new List<ScanError>();

        Assert.Throws<DirectoryNotFoundException>(() =>
            new Scanner(db).ScanRoot("d", onError: diagnostics.Add)
        );

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("d", diagnostic.RootId);
        Assert.Equal(missingRoot, diagnostic.Path);
        Assert.Contains("root path not found", diagnostic.Message);
        Assert.Equal(ScanStatus.Failed, db.LatestScanStatus("d"));
    }

    [Fact]
    public void Unavailable_Link_Metadata_Does_Not_Produce_Scan_Diagnostics()
    {
        using var db = CreateDatabase();
        var scanner = new Scanner(
            db,
            _ =>
                [
                    new FsEntry(
                        Path.Combine(_root, "unavailable-link"),
                        false,
                        0,
                        default,
                        default,
                        false,
                        true,
                        null
                    ),
                ]
        );
        var diagnostics = new List<ScanError>();

        Assert.Equal((1, 0), scanner.ScanRoot("d", onError: diagnostics.Add));

        Assert.Empty(diagnostics);
        Assert.Equal(ScanStatus.Completed, db.LatestScanStatus("d"));
        var entry = db.GetFileEntry("d", "unavailable-link")!;
        Assert.Equal(FileStatus.Ok, entry.Status);
        Assert.Null(entry.Error);
        Assert.False(string.IsNullOrWhiteSpace(entry.LinkNote));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cli_Prints_Failed_Paths_And_Reasons_To_Stderr_With_Or_Without_Progress(
        bool noProgress
    )
    {
        string missingRoot = Path.Combine(_directory, "missing-root");
        using (CreateDatabase(missingRoot)) { }
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            string[] args = noProgress
                ? ["scan", "d", "--db", _dbPath, "--no-progress"]
                : ["scan", "d", "--db", _dbPath];

            Assert.Equal(2, Cli.Run(args));

            Assert.Equal(
                $"scan d: ERROR \"{missingRoot}\": root path not found: {missingRoot}{Environment.NewLine}",
                error.ToString()
            );
            using var inventory = Database.OpenReadOnly(_dbPath, pooling: false);
            Assert.Equal(ScanStatus.Failed, inventory.LatestScanStatus("d"));
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }
    }

    [Fact]
    public void Cli_Diagnostics_Cannot_Emit_Bell_Or_Other_Embedded_Control_Characters()
    {
        using (CreateDatabase(Path.Combine(_directory, "missing\a\u001b-root"))) { }
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);

            Assert.Equal(2, Cli.Run(["scan", "d", "--db", _dbPath, "--no-progress"]));

            Assert.Contains("scan d: ERROR", error.ToString());
            Assert.All(
                error.ToString().Replace(Environment.NewLine, ""),
                c => Assert.False(char.IsControl(c))
            );
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }
    }
}
