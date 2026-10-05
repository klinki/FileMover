using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BackupNormalizer;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

if (args.Length != 2 || args[0] is not ("--prepare" or "--run" or "--run-static"))
{
    Console.Error.WriteLine(
        "Usage: BackupNormalizer.AotTrial --prepare|--run|--run-static <fixture-directory>"
    );
    return 2;
}

string fixtures = Path.GetFullPath(args[1]);
if (args[0] == "--run-static")
    return StaticTrial.Run(fixtures);
const string now = "2026-10-05T12:00:00.0000000Z";
if (args[0] == "--prepare")
{
    if (!RuntimeFeature.IsDynamicCodeSupported)
        throw new InvalidOperationException("Prepare fixtures using the ordinary managed build.");
    Directory.CreateDirectory(fixtures);
    string current = Path.Combine(fixtures, "current.db");
    string legacy = Path.Combine(fixtures, "legacy.db");
    if (File.Exists(current) || File.Exists(legacy))
        throw new IOException("Fixture databases already exist; use a new directory.");
    using (var db = new Database(current))
    {
        db.UpsertRoot(Root("seed"));
        long scan = db.BeginScan("seed");
        db.UpsertFileEntry(FileRow("seed", scan));
        db.FinishScan(scan, ScanStatus.Completed);
        db.SaveScanCheckpoint(new("seed", "/fixture/seed", "volume", "root", "journal", 1, scan));
    }
    var options = new DbContextOptionsBuilder<BackupNormalizerDbContext>()
        .UseSqlite(new SqliteConnectionStringBuilder { DataSource = legacy }.ToString())
        .Options;
    using (var context = new BackupNormalizerDbContext(options))
    {
        context.GetService<IMigrator>().Migrate("20260925052612_InitialCreate");
        context.Database.ExecuteSqlInterpolated(
            $"INSERT INTO StorageRoot (Id,Name,Path,Writable,FileSystemId,CaseSensitivity,CreatedUtc) VALUES ({"seed"},{"seed"},{"/fixture/seed"},{true},{"unknown"},{"sensitive"},{now})"
        );
    }
    Console.WriteLine("Prepared current and initial-migration fixture databases.");
    return 0;
}

Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"Architecture: {RuntimeInformation.ProcessArchitecture}");
Console.WriteLine($"Page size: {Environment.SystemPageSize}");
Console.WriteLine($"Dynamic code supported: {RuntimeFeature.IsDynamicCodeSupported}");
int failures = 0;
Check("open current database", _ => { });
Check("read root", db => Require(db.GetRoot("seed")?.Name == "seed"));
Check(
    "insert root",
    db =>
    {
        db.UpsertRoot(Root("insert"));
        Require(Count(db, "SELECT COUNT(*) FROM StorageRoot WHERE Id='insert'") == 1);
    }
);
Check(
    "update root",
    db =>
    {
        db.UpsertRoot(Root("seed") with { Name = "updated" });
        Require(Text(db, "SELECT Name FROM StorageRoot WHERE Id='seed'") == "updated");
    }
);
Check("count inventory files", db => Require(db.CountFiles("seed") == 1));
Check(
    "insert file",
    db =>
    {
        long id = db.UpsertFileEntry(
            FileRow("seed", 1) with
            {
                RelativePath = "insert.txt",
                Name = "insert.txt",
            }
        );
        Require(
            id > 0
                && Count(db, "SELECT COUNT(*) FROM FileEntry WHERE RelativePath='insert.txt'") == 1
        );
    }
);
Check(
    "update file",
    db =>
    {
        db.UpsertFileEntry(FileRow("seed", 1) with { Size = 42 });
        Require(Count(db, "SELECT Size FROM FileEntry WHERE RelativePath='seed.txt'") == 42);
    }
);
Check("read file projection", db => Require(db.GetFileEntry("seed", "seed.txt")?.Size == 4));
Check("list inventory files", db => Require(db.ListFiles("seed").Count == 1));
Check(
    "delete checkpoint",
    db =>
    {
        db.ClearScanCheckpoint("seed");
        Require(Count(db, "SELECT COUNT(*) FROM ScanCheckpoint") == 0);
    }
);
Check(
    "transaction commit",
    db =>
    {
        using (var tx = db.BeginTransaction())
        {
            db.UpsertRoot(Root("committed"));
            tx.Commit();
        }
        Require(Count(db, "SELECT COUNT(*) FROM StorageRoot WHERE Id='committed'") == 1);
    }
);
Check(
    "transaction rollback",
    db =>
    {
        using (var tx = db.BeginTransaction())
        {
            db.UpsertRoot(Root("rolled-back"));
            tx.Rollback();
        }
        Require(Count(db, "SELECT COUNT(*) FROM StorageRoot WHERE Id='rolled-back'") == 0);
    }
);
Check("inventory status query", db => Require(db.GetInventoryStatus("seed").RegularFiles == 1));
Check("legacy read-only root", db => Require(db.GetRoot("seed")?.Id == "seed"), legacy: true);
Console.WriteLine($"RESULT: {failures} failed checks");
return failures == 0 ? 0 : 1;

StorageRootRow Root(string id) => new(id, id, "/fixture/" + id, true, "unknown", "sensitive", now);
FileEntryRow FileRow(string rootId, long scanId) =>
    new(0, rootId, "seed.txt", "seed.txt", 4, now, null, null, scanId, FileStatus.Ok, null);

void Check(string name, Action<Database> action, bool legacy = false)
{
    string path = Path.Combine(
        Path.GetTempPath(),
        "bn-ef-aot-" + Guid.NewGuid().ToString("N") + ".db"
    );
    try
    {
        File.Copy(Path.Combine(fixtures, legacy ? "legacy.db" : "current.db"), path);
        using var db = legacy
            ? Database.OpenReadOnly(path, pooling: false)
            : Database.OpenWritable(path, pooling: false);
        action(db);
        Console.WriteLine($"PASS: {name}");
    }
    catch (Exception error)
    {
        failures++;
        Console.WriteLine($"FAIL: {name}: {error}");
    }
}

static void Require(bool success)
{
    if (!success)
        throw new InvalidOperationException("Unexpected database result.");
}

static long Count(Database db, string sql)
{
    using var connection = new SqliteConnection(
        new SqliteConnectionStringBuilder
        {
            DataSource = db.DbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString()
    );
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    return Convert.ToInt64(command.ExecuteScalar());
}

static string? Text(Database db, string sql)
{
    using var connection = new SqliteConnection(
        new SqliteConnectionStringBuilder
        {
            DataSource = db.DbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString()
    );
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    return command.ExecuteScalar() as string;
}
