using BackupNormalizer;

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
                Assert.Single(db.AppliedMigrations());
                Assert.Empty(db.PendingMigrations());
            }

            using var reopened = new Database(path);
            Assert.Single(reopened.AppliedMigrations());
            Assert.Empty(reopened.PendingMigrations());
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
