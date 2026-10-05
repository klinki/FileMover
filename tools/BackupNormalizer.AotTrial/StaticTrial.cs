using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BackupNormalizer;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage;

// Uses the production context/entities with complete, statically discoverable queries.
// This proves the proposed query shapes, not compatibility of the existing facade.
internal static class StaticTrial
{
    private const string Now = "2026-10-05T12:00:00.0000000Z";

    public static int Run(string fixtures)
    {
        Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"Architecture: {RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($"Page size: {Environment.SystemPageSize}");
        Console.WriteLine($"Dynamic code supported: {RuntimeFeature.IsDynamicCodeSupported}");
        int failures = 0;
        Check("read root", ReadRoot);
        Check("insert root", InsertRoot);
        Check("update root", UpdateRoot);
        Check("count inventory files", CountFiles);
        Check("insert file", InsertFile);
        Check("update file", UpdateFile);
        Check("read file projection", ReadFile);
        Check("persist and read hash", SaveHash);
        Check("update hash", UpdateHash);
        Check("delete checkpoint", DeleteCheckpoint);
        Check("transaction commit", Commit);
        Check("transaction rollback", Rollback);
        Check("inventory aggregate", InventoryAggregate);
        Check("legacy read-only root", ReadRoot, legacy: true);
        Console.WriteLine($"RESULT: {failures} failed checks");
        return failures == 0 ? 0 : 1;

        void Check(string name, Action<BackupNormalizerDbContext> action, bool legacy = false)
        {
            string path = Path.Combine(
                Path.GetTempPath(),
                "bn-ef-static-" + Guid.NewGuid().ToString("N") + ".db"
            );
            try
            {
                File.Copy(Path.Combine(fixtures, legacy ? "legacy.db" : "current.db"), path);
                var options = new DbContextOptionsBuilder<BackupNormalizerDbContext>()
                    .UseSqlite(
                        new SqliteConnectionStringBuilder
                        {
                            DataSource = path,
                            Mode = legacy ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
                            Pooling = false,
                            ForeignKeys = true,
                        }.ToString()
                    )
                    .Options;
                using var context = new BackupNormalizerDbContext(options);
                context.Database.OpenConnection();
                action(context);
                Console.WriteLine($"PASS: {name}");
            }
            catch (Exception error)
            {
                failures++;
                Console.WriteLine($"FAIL: {name}: {error}");
            }
        }
    }

    private static void ReadRoot(BackupNormalizerDbContext contextInput)
    {
        var context = contextInput;
        var root = context
            .StorageRoots.AsNoTracking()
            .Where(x => x.Id == "seed")
            .Select(x => new StorageRootRow(
                x.Id,
                x.Name,
                x.Path,
                x.Writable,
                x.FileSystemId,
                x.CaseSensitivity,
                x.CreatedUtc
            ))
            .FirstOrDefault();
        Require(root?.Name == "seed");
    }

    private static void InsertRoot(BackupNormalizerDbContext contextInput)
    {
        var context = contextInput;
        context.StorageRoots.Add(
            new StorageRootEntity
            {
                Id = "insert",
                Name = "insert",
                Path = "/fixture/insert",
                Writable = true,
                FileSystemId = "unknown",
                CaseSensitivity = "sensitive",
                CreatedUtc = Now,
            }
        );
        context.SaveChanges();
        context.ChangeTracker.Clear();
        Require(Scalar(context, "SELECT COUNT(*) FROM StorageRoot WHERE Id='insert'") == 1);
    }

    private static void UpdateRoot(BackupNormalizerDbContext contextInput)
    {
        var context = contextInput;
        Require(
            context
                .StorageRoots.Where(x => x.Id == "seed")
                .ExecuteUpdate(setters => setters.SetProperty(x => x.Name, "updated")) == 1
        );
        using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT Name FROM StorageRoot WHERE Id='seed'";
        Require(command.ExecuteScalar() as string == "updated");
    }

    private static void CountFiles(BackupNormalizerDbContext contextInput)
    {
        var context = contextInput;
        Require(
            context.FileEntries.AsNoTracking().Where(x => x.StorageRootId == "seed").Count() == 1
        );
    }

    private static void InsertFile(BackupNormalizerDbContext contextInput)
    {
        var context = contextInput;
        var file = new FileEntryEntity
        {
            StorageRootId = "seed",
            RelativePath = "insert.txt",
            Name = "insert.txt",
            Size = 4,
            ModifiedUtc = Now,
            LastSeenScanId = 1,
            Status = FileStatus.Ok,
            EntryKind = EntryKind.File,
        };
        context.FileEntries.Add(file);
        context.SaveChanges();
        Require(
            file.Id > 0
                && Scalar(context, "SELECT COUNT(*) FROM FileEntry WHERE RelativePath='insert.txt'")
                    == 1
        );
    }

    private static void UpdateFile(BackupNormalizerDbContext contextInput)
    {
        var context = contextInput;
        long updatedSize = 42;
        Require(
            context
                .FileEntries.Where(x => x.StorageRootId == "seed" && x.RelativePath == "seed.txt")
                .ExecuteUpdate(setters => setters.SetProperty(x => x.Size, x => updatedSize)) == 1
        );
        Require(Scalar(context, "SELECT Size FROM FileEntry WHERE RelativePath='seed.txt'") == 42);
    }

    private static void ReadFile(BackupNormalizerDbContext contextInput)
    {
        var context = contextInput;
        var file = context
            .FileEntries.AsNoTracking()
            .Where(x => x.StorageRootId == "seed" && x.RelativePath == "seed.txt")
            .Select(x => new FileEntryRow(
                x.Id,
                x.StorageRootId,
                x.RelativePath,
                x.Name,
                x.Size,
                x.ModifiedUtc,
                x.CreatedUtc,
                x.FileIdentity,
                x.LastSeenScanId,
                x.Status,
                x.Error,
                x.EntryKind,
                x.LinkTarget,
                x.TargetPath,
                x.LinkNote
            ))
            .FirstOrDefault();
        Require(file?.Size == 4);
    }

    private static void SaveHash(BackupNormalizerDbContext contextInput)
    {
        var context = contextInput;
        long id = context
            .FileEntries.Where(x => x.RelativePath == "seed.txt")
            .Select(x => x.Id)
            .Single();
        context.FileHashes.Add(
            new FileHashEntity
            {
                FileEntryId = id,
                Algorithm = "sha256",
                Digest = "fixture-digest",
                SizeAtHash = 4,
                ModifiedUtcAtHash = Now,
                CalculatedUtc = Now,
                State = HashState.Ok,
            }
        );
        context.SaveChanges();
        context.ChangeTracker.Clear();
        var hash = context
            .FileHashes.AsNoTracking()
            .Where(x => x.FileEntryId == id && x.Algorithm == "sha256")
            .Select(x => new FileHashRow(
                x.FileEntryId,
                x.Algorithm,
                x.Digest,
                x.SizeAtHash,
                x.ModifiedUtcAtHash,
                x.CalculatedUtc,
                x.State
            ))
            .Single();
        Require(hash.Digest == "fixture-digest" && hash.SizeAtHash == 4);
    }

    private static void UpdateHash(BackupNormalizerDbContext contextInput)
    {
        var context = contextInput;
        SaveHash(context);
        Require(
            context
                .FileHashes.Where(x => x.Algorithm == "sha256")
                .ExecuteUpdate(setters => setters.SetProperty(x => x.State, HashState.Stale)) == 1
        );
        Require(Scalar(context, "SELECT COUNT(*) FROM FileHash WHERE State='Stale'") == 1);
    }

    private static void DeleteCheckpoint(BackupNormalizerDbContext contextInput)
    {
        var context = contextInput;
        Require(context.ScanCheckpoints.Where(x => x.StorageRootId == "seed").ExecuteDelete() == 1);
        Require(Scalar(context, "SELECT COUNT(*) FROM ScanCheckpoint") == 0);
    }

    private static void Commit(BackupNormalizerDbContext contextInput)
    {
        var context = contextInput;
        using (var transaction = context.Database.BeginTransaction())
        {
            InsertRoot(context);
            transaction.Commit();
        }
        Require(Scalar(context, "SELECT COUNT(*) FROM StorageRoot WHERE Id='insert'") == 1);
    }

    private static void Rollback(BackupNormalizerDbContext contextInput)
    {
        var context = contextInput;
        using (var transaction = context.Database.BeginTransaction())
        {
            InsertRoot(context);
            transaction.Rollback();
            context.ChangeTracker.Clear();
        }
        Require(Scalar(context, "SELECT COUNT(*) FROM StorageRoot WHERE Id='insert'") == 0);
    }

    private static void InventoryAggregate(BackupNormalizerDbContext contextInput)
    {
        var context = contextInput;
        Require(
            context
                .FileEntries.AsNoTracking()
                .Where(x =>
                    x.StorageRootId == "seed"
                    && x.Status == FileStatus.Ok
                    && x.EntryKind == EntryKind.File
                )
                .Count() == 1
        );
    }

    private static long Scalar(BackupNormalizerDbContext context, string sql)
    {
        using var command = context.Database.GetDbConnection().CreateCommand();
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void Require(bool success)
    {
        if (!success)
            throw new InvalidOperationException("Unexpected database result.");
    }
}

// Allows the query-generation task to discover a context defined in the core assembly.
public sealed class TrialDbContextFactory : IDesignTimeDbContextFactory<BackupNormalizerDbContext>
{
    public BackupNormalizerDbContext CreateDbContext(string[] args) =>
        new BackupNormalizerDbContextFactory().CreateDbContext(args);
}
