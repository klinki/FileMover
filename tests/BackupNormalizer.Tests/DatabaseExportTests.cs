using BackupNormalizer;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BackupNormalizer.Tests;

[Collection("Console")]
public sealed class DatabaseExportTests : IDisposable
{
    private readonly string _fixture = Path.Combine(
        AppContext.BaseDirectory,
        "bn-export-" + Guid.NewGuid().ToString("N")
    );
    private string Source => Path.Combine(_fixture, "source.db");
    private string Destination => Path.Combine(_fixture, "portable.db");

    public DatabaseExportTests() => Directory.CreateDirectory(_fixture);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Assert.StartsWith(AppContext.BaseDirectory, Path.GetFullPath(_fixture));
        Directory.Delete(_fixture, true);
    }

    [Fact]
    public void Live_Wal_Export_Is_Independent_And_Preserves_All_Inventory_Data()
    {
        string root = Path.Combine(_fixture, "data");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "file.txt"), "content");
        using var db = new Database(Source);
        db.UpsertRoot(new StorageRootRow("r", "r", root, true, "fs", "unknown", Database.UtcNow()));
        var scanner = new Scanner(db, usnMode: "off");
        scanner.ScanRoot("r");
        scanner.HashNeeded("r");
        db.SaveScanCheckpoint(
            new ScanCheckpointRow(
                "r",
                root,
                "volume",
                "root",
                "journal",
                100,
                db.LatestScan("r")!.Id
            )
        );
        db.UpsertRoot(
            new StorageRootRow(
                "bad",
                "bad",
                Path.Combine(_fixture, "absent-root"),
                false,
                "fs",
                "unknown",
                Database.UtcNow()
            )
        );
        Assert.Throws<DirectoryNotFoundException>(() => scanner.ScanRoot("bad"));
        db.InsertPlan("p", "source.db", "s", "source", "r", root, 0);
        db.InsertOperation(
            "p",
            1,
            OpType.SkipLink,
            SourceScope.Source,
            "s",
            "link",
            "r",
            "link",
            0,
            null,
            skipReason: "source link"
        );
        db.AddExecutionLog(1, "INFO", "preserved", Database.UtcNow());
        Assert.True(File.Exists(Source + "-wal"));

        Database.ExportSnapshot(Source, Destination);

        Assert.False(File.Exists(Destination + "-wal"));
        Assert.False(File.Exists(Destination + "-shm"));
        using var exported = Database.OpenReadOnly(Destination, pooling: false);
        Assert.Equal(db.ListRoots(), exported.ListRoots());
        Assert.Equal(db.ListFiles(), exported.ListFiles());
        Assert.Equal(
            db.GetHash(db.ListFiles()[0].Id, "sha256"),
            exported.GetHash(db.ListFiles()[0].Id, "sha256")
        );
        Assert.Equal(db.GetScanDetails("r"), exported.GetScanDetails("r"));
        Assert.Equal(
            db.ListScanDiagnostics(db.LatestScan("bad")!.Id),
            exported.ListScanDiagnostics(db.LatestScan("bad")!.Id)
        );
        Assert.Equal(db.GetScanCheckpoint("r"), exported.GetScanCheckpoint("r"));
        Assert.Equal(db.GetPlan("p"), exported.GetPlan("p"));
        Assert.Equal(db.ListPlanOperations("p"), exported.ListPlanOperations("p"));
        Assert.Equal("preserved", Assert.Single(exported.Context.ExecutionLogs).Message);
        Assert.Equal(db.AppliedMigrations(), exported.AppliedMigrations());
        Assert.Empty(Directory.GetFiles(_fixture, ".bn-export-*"));
        using var transaction = db.BeginTransaction();
        db.UpsertRoot(
            new StorageRootRow(
                "uncommitted",
                "uncommitted",
                root,
                false,
                "fs",
                "unknown",
                Database.UtcNow()
            )
        );
        string second = Path.Combine(_fixture, "second.db");
        Database.ExportSnapshot(Source, second);
        using var snapshot = Database.OpenReadOnly(second, pooling: false);
        Assert.Null(snapshot.GetRoot("uncommitted"));
        transaction.Rollback();
    }

    [Fact]
    public void Legacy_Schema_Is_Exported_Without_Migrating_Source_Or_Destination()
    {
        var options = new DbContextOptionsBuilder<BackupNormalizerDbContext>()
            .UseSqlite(
                new SqliteConnectionStringBuilder
                {
                    DataSource = Source,
                    Pooling = false,
                }.ToString()
            )
            .Options;
        using (var old = new BackupNormalizerDbContext(options))
        {
            old.GetService<IMigrator>().Migrate("20260925052612_InitialCreate");
            old.Database.ExecuteSqlRaw(
                "INSERT INTO StorageRoot (Id,Name,Path,CreatedUtc) VALUES ('r','r','unavailable','before')"
            );
        }
        byte[] original = File.ReadAllBytes(Source);
        Database.ExportSnapshot(Source, Destination);
        Assert.Equal(original, File.ReadAllBytes(Source));
        using var exported = Database.OpenReadOnly(Destination, pooling: false);
        Assert.Single(exported.AppliedMigrations());
        Assert.Equal("r", Assert.Single(exported.ListRoots()).Id);
        Assert.Single(exported.PendingMigrations(), m => m.EndsWith("RecordScanDiagnostics"));
    }

    [Fact]
    public void Collisions_And_Invalid_Inputs_Preserve_Existing_Files_And_Leave_No_Temporary_Files()
    {
        using var db = new Database(Source);
        File.WriteAllText(Destination, "preserve");
        Assert.Throws<IOException>(() => Database.ExportSnapshot(Source, Destination));
        Assert.Equal("preserve", File.ReadAllText(Destination));
        Assert.Throws<ArgumentException>(() => Database.ExportSnapshot(Source, Source));
        Assert.Throws<ArgumentException>(() => Database.ExportSnapshot(Source, Source + "-wal"));
        Assert.Throws<FileNotFoundException>(() =>
            Database.ExportSnapshot(Source + ".missing", Path.Combine(_fixture, "absent.db"))
        );
        string companionOutput = Path.Combine(_fixture, "companion-output.db");
        File.WriteAllText(companionOutput + "-wal", "preserve companion");
        Assert.Throws<IOException>(() => Database.ExportSnapshot(Source, companionOutput));
        Assert.Equal("preserve companion", File.ReadAllText(companionOutput + "-wal"));
        string invalid = Path.Combine(_fixture, "invalid.db");
        File.WriteAllText(invalid, "not SQLite");
        string invalidOutput = Path.Combine(_fixture, "invalid-output.db");
        Assert.Throws<SqliteException>(() => Database.ExportSnapshot(invalid, invalidOutput));
        Assert.False(File.Exists(invalidOutput));
        Assert.Empty(Directory.GetFiles(_fixture, ".bn-export-*"));
    }

    [Fact]
    public void Cli_Exports_One_File_And_Requires_An_Output_Path()
    {
        using (var db = new Database(Source)) { }
        var original = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Assert.Equal(0, Cli.Run(["db", "export", "--db", Source, "--output", Destination]));
            Assert.Contains("exported inventory snapshot", output.ToString());
            Assert.True(File.Exists(Destination));
            Assert.Equal(2, Cli.Run(["db", "export", "--db", Source]));
        }
        finally
        {
            Console.SetOut(original);
        }
    }
}
