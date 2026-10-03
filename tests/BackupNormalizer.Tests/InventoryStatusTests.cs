using System.Text.Json;
using BackupNormalizer;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BackupNormalizer.Tests;

[Collection("Console")]
public sealed class InventoryStatusTests : IDisposable
{
    private readonly string _fixture = Path.Combine(
        AppContext.BaseDirectory,
        "bn-status-" + Guid.NewGuid().ToString("N")
    );
    private string Root => Path.Combine(_fixture, "data");
    private string DbPath => Path.Combine(_fixture, "inventory.db");

    public InventoryStatusTests() => Directory.CreateDirectory(Root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Assert.StartsWith(AppContext.BaseDirectory, Path.GetFullPath(_fixture));
        Directory.Delete(_fixture, true);
    }

    private Database Open()
    {
        var db = new Database(DbPath);
        db.UpsertRoot(new StorageRootRow("r", "r", Root, true, "fs", "unknown", Database.UtcNow()));
        return db;
    }

    [Fact]
    public void Failure_Details_Survive_Reopening_And_A_Successful_Rescan()
    {
        File.WriteAllText(Path.Combine(Root, "keep.txt"), "keep");
        long failedId;
        string denied = Path.Combine(Root, "denied");
        using (var db = Open())
        {
            new Scanner(db, usnMode: "off").ScanRoot("r");
            var scanner = new Scanner(
                db,
                _ =>
                    [new FsEntry(denied, true, 0, default, default, false, false, "Access denied.")]
            );
            Assert.Equal((0, 1), scanner.ScanRoot("r"));
            var failed = db.GetScanDetails("r")!;
            failedId = failed.Scan.Id;
            Assert.Equal("Recursive", failed.Mode);
            Assert.Equal(1, failed.ErrorCount);
            Assert.NotNull(failed.FallbackReason);
        }
        using (var db = Database.OpenReadOnly(DbPath, pooling: false))
        {
            var status = db.GetInventoryStatus("r");
            Assert.False(status.PlanningReady);
            Assert.Equal(ScanStatus.Completed, status.LastSuccessfulScan!.Status);
            var error = Assert.Single(status.Errors);
            Assert.Equal(denied, error.Path);
            Assert.Equal("Access denied.", error.Message);
            Assert.True(DateTime.TryParse(error.RecordedUtc, out _));
            Assert.Equal(FileStatus.Ok, db.GetFileEntry("r", "keep.txt")!.Status);
        }
        using (var db = Open())
        {
            Assert.Equal((1, 0), new Scanner(db, usnMode: "off").ScanRoot("r"));
            Assert.Empty(db.GetInventoryStatus("r").Errors);
            Assert.Single(db.ListScanDiagnostics(failedId));
        }
    }

    [Fact]
    public void Readiness_Requires_Current_Hashes_And_Excludes_Links()
    {
        File.WriteAllText(Path.Combine(Root, "keep.txt"), "keep");
        using var db = Open();
        var scanner = new Scanner(db, usnMode: "off");
        scanner.ScanRoot("r");
        Assert.Equal(1, db.GetInventoryStatus("r").MissingHashes);
        Assert.False(db.GetInventoryStatus("r").PlanningReady);
        scanner.HashNeeded("r");
        long scanId = db.LatestScan("r")!.Id;
        db.UpsertFileEntry(
            new FileEntryRow(
                0,
                "r",
                "link",
                "link",
                0,
                Database.UtcNow(),
                null,
                null,
                scanId,
                FileStatus.Ok,
                null,
                EntryKind.FileLink
            )
        );
        var ready = db.GetInventoryStatus("r");
        Assert.True(ready.PlanningReady);
        Assert.Equal(1, ready.Links);
        Assert.Equal(1, ready.UsableHashes);
        Assert.Equal(0, ready.MissingHashes);
        db.MarkRootHashesStale("r");
        Assert.False(db.GetInventoryStatus("r").PlanningReady);
    }

    [Fact]
    public void Fatal_Root_Failures_Are_Persisted()
    {
        using var db = Open();
        Directory.Delete(Root);
        Assert.Throws<DirectoryNotFoundException>(() => new Scanner(db).ScanRoot("r"));
        var status = db.GetInventoryStatus("r");
        Assert.Equal(ScanStatus.Failed, status.LatestScan!.Scan.Status);
        Assert.Equal("NotStarted", status.LatestScan.Mode);
        Assert.Equal(Root, Assert.Single(status.Errors).Path);
        Assert.Equal(1, status.LatestScan.ErrorCount);
    }

    [Fact]
    public void Historical_ReadOnly_Diagnostics_Are_Unknown_Without_Migration()
    {
        var options = new DbContextOptionsBuilder<BackupNormalizerDbContext>()
            .UseSqlite(
                new SqliteConnectionStringBuilder
                {
                    DataSource = DbPath,
                    Pooling = false,
                }.ToString()
            )
            .Options;
        using (var old = new BackupNormalizerDbContext(options))
        {
            old.GetService<IMigrator>().Migrate("20261002173131_TrackUsnCheckpoints");
            old.Database.ExecuteSqlRaw(
                "INSERT INTO StorageRoot (Id,Name,Path,CreatedUtc) VALUES ('r','r','unavailable','before')"
            );
            old.Database.ExecuteSqlRaw(
                "INSERT INTO Scan (StorageRootId,StartedUtc,Status) VALUES ('r','before','Incomplete')"
            );
        }
        byte[] before = File.ReadAllBytes(DbPath);
        using (var db = Database.OpenReadOnly(DbPath, pooling: false))
        {
            Assert.Equal(4, db.AppliedMigrations().Count);
            var status = db.GetInventoryStatus("r");
            Assert.Null(status.LatestScan!.ErrorCount);
            Assert.Empty(status.Errors);
            Assert.False(status.PlanningReady);
        }
        Assert.Equal(before, File.ReadAllBytes(DbPath));
        using var upgraded = new Database(DbPath);
        Assert.Equal(6, upgraded.AppliedMigrations().Count);
        Assert.Null(upgraded.GetScanDetails("r")!.ErrorCount);
    }

    [Fact]
    public void Cli_Reports_Readiness_And_Historical_Errors_In_Json()
    {
        using (var db = Open())
        {
            var scanner = new Scanner(
                db,
                _ =>
                    [
                        new FsEntry(
                            Path.Combine(Root, "denied"),
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
            scanner.ScanRoot("r");
        }
        var original = Console.Out;
        bool originalJson = Log.Json;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Assert.Equal(3, Cli.Run(["status", "r", "--db", DbPath, "--json"]));
            using var status = JsonDocument.Parse(output.ToString());
            Assert.False(status.RootElement[0].GetProperty("planningReady").GetBoolean());
            Assert.Equal(
                1,
                status.RootElement[0].GetProperty("latestScan").GetProperty("errorCount").GetInt32()
            );
            output.GetStringBuilder().Clear();
            Assert.Equal(0, Cli.Run(["scan", "errors", "r", "--db", DbPath, "--json"]));
            using var errors = JsonDocument.Parse(output.ToString());
            Assert.Equal(
                "Access denied.",
                errors.RootElement.GetProperty("errors")[0].GetProperty("message").GetString()
            );
            output.GetStringBuilder().Clear();
            Assert.Equal(0, Cli.Run(["scan", "errors", "r", "--db", DbPath]));
            Assert.Contains("Access denied.", output.ToString());
        }
        finally
        {
            Console.SetOut(original);
            Log.Json = originalJson;
        }
    }
}
