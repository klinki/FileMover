using BackupNormalizer;
using Microsoft.EntityFrameworkCore;

namespace BackupNormalizer.Tests;

public sealed class DatabaseMigrationTests
{
    [Fact]
    public void NewDatabaseAppliesInitialMigrationOnce()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bn-migration-{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new Database(path))
            {
                Assert.Single(db.Context.Database.GetAppliedMigrations());
                Assert.Empty(db.Context.Database.GetPendingMigrations());
            }

            using var reopened = new Database(path);
            Assert.Single(reopened.Context.Database.GetAppliedMigrations());
            Assert.Empty(reopened.Context.Database.GetPendingMigrations());
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
