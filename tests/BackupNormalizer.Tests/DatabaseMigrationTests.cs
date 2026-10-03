using BackupNormalizer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BackupNormalizer.Tests;

public sealed class DatabaseMigrationTests
{
    [Fact]
    public void NewDatabaseAppliesMigrationsOnce()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bn-migration-{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new Database(path))
            {
                Assert.Equal(6, db.AppliedMigrations().Count);
                Assert.Empty(db.PendingMigrations());
            }

            using var reopened = new Database(path);
            Assert.Equal(6, reopened.AppliedMigrations().Count);
            Assert.Empty(reopened.PendingMigrations());
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void Existing_Database_Upgrades_Without_Losing_Plan_Statuses_Or_Logs()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bn-upgrade-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<BackupNormalizerDbContext>().UseSqlite($"Data Source={path}").Options;
            using (var old = new BackupNormalizerDbContext(options))
            {
                old.GetService<IMigrator>().Migrate("20260925052612_InitialCreate");
                old.Database.ExecuteSqlRaw("INSERT INTO Plan (Id, CreatedUtc, SourceDatabasePath, SourceRootId, SourceRootPath, TargetRootId, TargetRootPath, Status) VALUES ('old', 'before', 'source.db', 's', 'source', 't', 'target', 'Partial')");
                old.Database.ExecuteSqlRaw("INSERT INTO PlanOperation (Id, PlanId, Sequence, Type, Status) VALUES (42, 'old', 1, 'MKDIR', 'Completed')");
                old.Database.ExecuteSqlRaw("INSERT INTO ExecutionLog (PlanOperationId, TimestampUtc, Level, Message) VALUES (42, 'before', 'INFO', 'preserved')");
            }
            using var upgraded = new Database(path);
            Assert.Equal(6, upgraded.AppliedMigrations().Count);
            Assert.Empty(upgraded.PendingMigrations());
            var plan = upgraded.GetPlan("old")!;
            Assert.Equal(PlanStatus.Partial, plan.Status);
            Assert.Null(plan.ExecutionSourceRootPath);
            Assert.Null(plan.ExecutionTargetRootPath);
            Assert.Equal(OpStatus.Completed, Assert.Single(upgraded.ListPlanOperations("old")).Status);
            Assert.Equal("preserved", Assert.Single(upgraded.Context.ExecutionLogs).Message);
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
