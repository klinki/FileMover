using BackupNormalizer.Ui.Models;
using BackupNormalizer.Ui.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BackupNormalizer.Tests;

public sealed class UiInventoryHealthTests : IDisposable
{
    private readonly string _fixture = Path.Combine(AppContext.BaseDirectory, "bn-ui-health-" + Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_fixture, "inventory.db");

    public UiInventoryHealthTests() => Directory.CreateDirectory(_fixture);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Assert.StartsWith(AppContext.BaseDirectory, Path.GetFullPath(_fixture));
        Directory.Delete(_fixture, true);
    }

    [Fact]
    public void Snapshot_Exposes_Scan_Readiness_And_Diagnostics_Without_Writing()
    {
        const string started = "2026-10-03T12:00:00.0000000Z";
        using (var db = new Database(DbPath))
        {
            db.UpsertRoot(new StorageRootRow("photos", "Photos", "E:\\Photos", false, "unknown", "insensitive", started));
            long scanId = db.BeginScan("photos");
            db.FinishScan(scanId, ScanStatus.Incomplete);
            db.SaveScanDiagnostics(scanId, "Recursive", 4, "USN journal unavailable; used full scan.",
                [new ScanError("photos", "E:\\Photos\\locked.jpg", "Access denied.")]);
            db.UpsertFileEntry(new FileEntryRow(0, "photos", "visible.jpg", "visible.jpg", 12,
                started, null, null, scanId, FileStatus.Ok, null));
        }
        SqliteConnection.ClearAllPools();
        byte[] before = File.ReadAllBytes(DbPath);

        var snapshot = InventorySnapshot.Load(DbPath);
        var viewModel = new InventoryHealthViewModel(Assert.Single(snapshot.Roots),
            new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal("Incomplete", viewModel.ScanStatus);
        Assert.EndsWith("days ago", viewModel.ScanAge);
        Assert.Equal("Full scan (recursive)", viewModel.ScanMode);
        Assert.Contains("USN journal unavailable", viewModel.FallbackReason);
        Assert.Equal("4", viewModel.ScannedCount);
        Assert.Equal("1", viewModel.ScanErrorCount);
        Assert.Equal("1", viewModel.RegularFiles);
        Assert.Equal("0", viewModel.Links);
        Assert.Contains("0 of 1", viewModel.HashReadiness);
        Assert.Equal("Blocked", viewModel.PlanningReadiness);
        Assert.Contains("complete successful scan", viewModel.PlanningBlocker);
        Assert.Contains("1 scan error", viewModel.DiagnosticsSummary);
        var error = Assert.Single(viewModel.Errors);
        Assert.Equal("E:\\Photos\\locked.jpg", error.Path);
        Assert.Equal("Access denied.", error.Message);

        SqliteConnection.ClearAllPools();
        Assert.Equal(before, File.ReadAllBytes(DbPath));
    }

    [Fact]
    public void Legacy_Snapshot_Shows_Unknown_Diagnostics_Without_Migration()
    {
        var options = new DbContextOptionsBuilder<BackupNormalizerDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = DbPath, Pooling = false }.ToString()).Options;
        using (var context = new BackupNormalizerDbContext(options))
        {
            context.GetService<IMigrator>().Migrate("20261002173131_TrackUsnCheckpoints");
            context.Database.ExecuteSqlRaw("INSERT INTO StorageRoot (Id,Name,Path,CreatedUtc) VALUES ('old','Old root','/offline','before')");
            context.Database.ExecuteSqlRaw("INSERT INTO Scan (StorageRootId,StartedUtc,Status) VALUES ('old','before','Incomplete')");
        }
        SqliteConnection.ClearAllPools();
        byte[] before = File.ReadAllBytes(DbPath);

        var snapshot = InventorySnapshot.Load(DbPath);
        var viewModel = new InventoryHealthViewModel(Assert.Single(snapshot.Roots));

        Assert.Equal("Unknown (older inventory)", viewModel.ScanMode);
        Assert.Equal("Unknown (older inventory)", viewModel.ScanErrorCount);
        Assert.Equal("Diagnostic count was not recorded by this inventory version.", viewModel.DiagnosticsSummary);
        Assert.Empty(viewModel.Errors);
        Assert.Equal("Blocked", viewModel.PlanningReadiness);
        SqliteConnection.ClearAllPools();
        Assert.Equal(before, File.ReadAllBytes(DbPath));
    }

    [Fact]
    public void Main_View_Model_Creates_Health_Report_For_Selected_Database_Root()
    {
        using (var db = new Database(DbPath))
        {
            db.UpsertRoot(new StorageRootRow("photos", "Photos", "E:\\Photos", false, "unknown", "sensitive", Database.UtcNow()));
            long scanId = db.BeginScan("photos");
            db.FinishScan(scanId, ScanStatus.Completed);
        }

        var main = new MainViewModel();
        main.Left.LoadSnapshot(InventorySnapshot.Load(DbPath));

        var report = main.CreateInventoryHealthViewModel("Left");
        Assert.NotNull(report);
        Assert.Equal("Photos", report.RootName);
    }
}
