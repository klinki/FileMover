using BackupNormalizer;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BackupNormalizer.Tests;

public sealed class DatabaseMigrationTests
{
    [Fact]
    public void Existing_V1_Database_Is_Adopted_Without_Losing_Data()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bn-legacy-{Guid.NewGuid():N}.db");
        try
        {
            using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE SchemaVersion(Version INTEGER NOT NULL);
                    CREATE TABLE StorageRoot(
                      Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Path TEXT NOT NULL, Role TEXT NOT NULL DEFAULT 'Unknown',
                      Writable INTEGER NOT NULL DEFAULT 1, FileSystemId TEXT NOT NULL DEFAULT 'unknown',
                      CaseSensitivity TEXT NOT NULL DEFAULT 'unknown', CreatedUtc TEXT NOT NULL);
                    CREATE TABLE Scan(
                      Id INTEGER PRIMARY KEY AUTOINCREMENT, StorageRootId TEXT NOT NULL, StartedUtc TEXT NOT NULL,
                      CompletedUtc TEXT, Status TEXT NOT NULL DEFAULT 'Started',
                      FOREIGN KEY(StorageRootId) REFERENCES StorageRoot(Id));
                    CREATE TABLE FileEntry(
                      Id INTEGER PRIMARY KEY AUTOINCREMENT, StorageRootId TEXT NOT NULL, RelativePath TEXT NOT NULL,
                      Name TEXT NOT NULL, Size INTEGER NOT NULL, ModifiedUtc TEXT NOT NULL, CreatedUtc TEXT,
                      FileIdentity TEXT, LastSeenScanId INTEGER NOT NULL, Status TEXT NOT NULL DEFAULT 'Ok', Error TEXT,
                      UNIQUE(StorageRootId, RelativePath),
                      FOREIGN KEY(StorageRootId) REFERENCES StorageRoot(Id));
                    CREATE INDEX IX_FileEntry_Root ON FileEntry(StorageRootId);
                    CREATE INDEX IX_FileEntry_Size ON FileEntry(Size);
                    CREATE TABLE FileHash(
                      FileEntryId INTEGER NOT NULL, Algorithm TEXT NOT NULL, Digest TEXT NOT NULL,
                      SizeAtHash INTEGER NOT NULL, ModifiedUtcAtHash TEXT NOT NULL, CalculatedUtc TEXT NOT NULL,
                      State TEXT NOT NULL DEFAULT 'Ok',
                      PRIMARY KEY(FileEntryId, Algorithm),
                      FOREIGN KEY(FileEntryId) REFERENCES FileEntry(Id) ON DELETE CASCADE);
                    CREATE INDEX IX_FileHash_Digest ON FileHash(Algorithm, Digest);
                    CREATE TABLE CanonicalEntry(
                      Id INTEGER PRIMARY KEY AUTOINCREMENT, RelativePath TEXT NOT NULL,
                      Size INTEGER NOT NULL, ExpectedHash TEXT, SourceFileEntryId INTEGER);
                    CREATE TABLE Plan(
                      Id TEXT PRIMARY KEY, CreatedUtc TEXT NOT NULL, CanonicalRootId TEXT NOT NULL,
                      Status TEXT NOT NULL DEFAULT 'Planned', EstimatedBytesCopied INTEGER NOT NULL DEFAULT 0);
                    CREATE TABLE PlanOperation(
                      Id INTEGER PRIMARY KEY AUTOINCREMENT, PlanId TEXT NOT NULL, Sequence INTEGER NOT NULL,
                      Type TEXT NOT NULL, SourceRootId TEXT, SourcePath TEXT,
                      DestinationRootId TEXT, DestinationPath TEXT,
                      ExpectedSize INTEGER NOT NULL DEFAULT 0, ExpectedHash TEXT,
                      Status TEXT NOT NULL DEFAULT 'Planned', StartedUtc TEXT, CompletedUtc TEXT, Error TEXT,
                      FOREIGN KEY(PlanId) REFERENCES Plan(Id) ON DELETE CASCADE);
                    CREATE INDEX IX_PlanOp_Plan ON PlanOperation(PlanId, Sequence);
                    CREATE TABLE ExecutionLog(
                      Id INTEGER PRIMARY KEY AUTOINCREMENT, PlanOperationId INTEGER NOT NULL,
                      TimestampUtc TEXT NOT NULL, Level TEXT NOT NULL, Message TEXT NOT NULL);
                    INSERT INTO SchemaVersion VALUES(1);
                    INSERT INTO StorageRoot VALUES('disk','Disk','/tmp','Backup',1,'fs','sensitive','2026-01-01T00:00:00Z');
                    INSERT INTO Scan VALUES(1,'disk','2026-01-01T00:00:00Z','2026-01-01T00:01:00Z','Completed');
                    INSERT INTO FileEntry VALUES(1,'disk','a.txt','a.txt',3,'2026-01-01T00:00:00Z',NULL,NULL,1,'Ok',NULL);
                    INSERT INTO FileHash VALUES(1,'sha256','abc',3,'2026-01-01T00:00:00Z','2026-01-01T00:01:00Z','Ok');
                    INSERT INTO Plan VALUES('legacy-plan','2026-01-01T00:02:00Z','disk','Planned',0);
                    INSERT INTO PlanOperation VALUES(1,'legacy-plan',1,'KEEP','disk','a.txt','disk','a.txt',3,'abc','Planned',NULL,NULL,NULL);
                    INSERT INTO ExecutionLog VALUES(1,1,'2026-01-01T00:03:00Z','INFO','legacy entry');
                    """;
                command.ExecuteNonQuery();
            }

            using var db = new Database(path);
            Assert.Equal("Disk", db.GetRoot("disk")?.Name);
            Assert.Equal("a.txt", Assert.Single(db.ListFiles("disk")).RelativePath);
            Assert.Equal("abc", db.GetHash(1, "sha256")?.Digest);
            Assert.True(db.PlanExists("legacy-plan"));
            Assert.Single(db.ListPlanOperations("legacy-plan"));
            Assert.Equal("legacy entry", Assert.Single(db.Context.ExecutionLogs).Message);
            Assert.Single(db.Context.Database.GetAppliedMigrations());
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
