using BackupNormalizer;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BackupNormalizer.Tests;

[Collection("Console")]
public sealed class AotMigrationTests
{
    private static string NewPath() =>
        Path.Combine(
            AppContext.BaseDirectory,
            "bn-aot-migration-" + Guid.NewGuid().ToString("N") + ".db"
        );

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Generated_SQL_Upgrades_Every_Known_Schema_And_Preserves_Data(int previousCount)
    {
        string path = NewPath();
        var manifest = MigrationManifestGenerator.Create();
        if (previousCount > 0)
        {
            using var old = new BackupNormalizerDbContextFactory().CreateDbContext([]);
            old.Database.SetConnectionString("Data Source=" + path);
            old.GetService<IMigrator>().Migrate(manifest.Steps[previousCount - 1].Id);
            old.Database.ExecuteSqlRaw(
                "INSERT INTO StorageRoot VALUES ('r', 'saved', '/fixture', 1, 'fs', 'sensitive', 'before');"
            );
        }
        string? backup = DatabaseSchema.Upgrade(path, manifest, "same", "same");
        using var upgraded = DatabaseSchema.Open(path, SqliteOpenMode.ReadOnly);
        Assert.Equal(6, DatabaseSchema.ReadHistory(upgraded, manifest).Count);
        Assert.Equal(previousCount is > 0 and < 6, backup != null);
        if (previousCount > 0)
        {
            using var command = upgraded.CreateCommand();
            command.CommandText = "SELECT Name FROM StorageRoot WHERE Id='r';";
            Assert.Equal("saved", command.ExecuteScalar());
        }
        if (backup != null)
        {
            using var saved = DatabaseSchema.Open(backup, SqliteOpenMode.ReadOnly);
            Assert.Equal(previousCount, DatabaseSchema.ReadHistory(saved, manifest).Count);
        }
        // Existing application migrations must accept the helper-produced database without upgrading it.
        using var normal = new Database(path);
        Assert.Empty(normal.PendingMigrations());
    }

    [Fact]
    public void Unknown_And_Incomplete_History_Are_Rejected_Without_Modification()
    {
        var manifest = MigrationManifestGenerator.Create();
        foreach (string id in new[] { "20990101000000_Future", manifest.Steps[1].Id })
        {
            string path = NewPath();
            using var db = DatabaseSchema.Open(path, SqliteOpenMode.ReadWriteCreate);
            using var command = db.CreateCommand();
            command.CommandText =
                "CREATE TABLE __EFMigrationsHistory (MigrationId TEXT PRIMARY KEY, ProductVersion TEXT); INSERT INTO __EFMigrationsHistory VALUES ($id, '10.0.12');";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
            Assert.Throws<InvalidOperationException>(() =>
                DatabaseSchema.Upgrade(path, manifest, "same", "same")
            );
            command.CommandText = "SELECT COUNT(*) FROM __EFMigrationsHistory;";
            Assert.Equal(1L, command.ExecuteScalar());
        }
    }

    [Fact]
    public void Package_Mismatch_Does_Not_Create_A_Database()
    {
        string path = NewPath();
        Assert.Throws<InvalidOperationException>(() =>
            DatabaseSchema.Upgrade(
                path,
                MigrationManifestGenerator.Create(),
                "application",
                "other-helper"
            )
        );
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Failed_Migration_Rolls_Back_And_Can_Be_Retried()
    {
        string path = NewPath();
        var manifest = MigrationManifestGenerator.Create();
        var failed = new MigrationManifest(
            1,
            [
                manifest.Steps[0],
                manifest.Steps[1] with
                {
                    Commands = ["CREATE TABLE ShouldRollback (Id INTEGER);", "INVALID SQL;"],
                },
            ]
        );
        Assert.Throws<IOException>(() => DatabaseSchema.Upgrade(path, failed, "same", "same"));
        using (var db = DatabaseSchema.Open(path, SqliteOpenMode.ReadOnly))
        {
            Assert.Single(DatabaseSchema.ReadHistory(db, manifest));
            using var check = db.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='ShouldRollback';";
            Assert.Equal(0L, check.ExecuteScalar());
        }
        Assert.NotNull(DatabaseSchema.Upgrade(path, manifest, "same", "same"));
    }

    [Fact]
    public void Root_List_Fails_Fast_On_A_Legacy_Database_Without_Upgrading_It()
    {
        string path = NewLegacyDatabase();
        var manifest = MigrationManifestGenerator.Create();
        Assert.Equal(2, Cli.Run(["root", "list", "--db", path]));
        Assert.Throws<DatabaseNeedsMigrationException>(() =>
            Database.OpenReadOnly(path, pooling: false)
        );
        using var check = DatabaseSchema.Open(path, SqliteOpenMode.ReadOnly);
        Assert.Single(DatabaseSchema.ReadHistory(check, manifest));
        Assert.False(File.Exists(path + ".bn-migration.lock"));
    }

    [Fact]
    public void Db_Migrate_Upgrades_A_Legacy_Database_So_It_Opens_Read_Only()
    {
        string path = NewLegacyDatabase();
        Assert.Equal(0, Cli.Run(["db", "migrate", "--db", path]));
        using var db = Database.OpenReadOnly(path, pooling: false);
        Assert.Equal("saved", Assert.Single(db.ListRoots()).Name);
    }

    private static string NewLegacyDatabase()
    {
        string path = NewPath();
        var manifest = MigrationManifestGenerator.Create();
        using var old = new BackupNormalizerDbContextFactory().CreateDbContext([]);
        old.Database.SetConnectionString("Data Source=" + path);
        old.GetService<IMigrator>().Migrate(manifest.Steps[0].Id);
        old.Database.ExecuteSqlRaw(
            "INSERT INTO StorageRoot VALUES ('r', 'saved', '/fixture', 1, 'fs', 'sensitive', 'before');"
        );
        return path;
    }

    [Fact]
    public void Existing_Helper_Lock_Prevents_A_Competing_Upgrade()
    {
        string path = NewPath();
        using var held = new FileStream(
            path + ".bn-migration.lock",
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None
        );
        Assert.Throws<IOException>(() =>
            DatabaseSchema.Upgrade(path, MigrationManifestGenerator.Create(), "same", "same")
        );
        Assert.False(File.Exists(path));
    }
}
