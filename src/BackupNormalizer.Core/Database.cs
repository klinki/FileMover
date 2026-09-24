using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BackupNormalizer;

public sealed record StorageRootRow(string Id, string Name, string Path, string Role, bool Writable, string FileSystemId, string CaseSensitivity, string CreatedUtc);
public sealed record ScanRow(long Id, string StorageRootId, string StartedUtc, string? CompletedUtc, string Status);
public sealed record FileEntryRow(long Id, string StorageRootId, string RelativePath, string Name, long Size, string ModifiedUtc, string? CreatedUtc, string? FileIdentity, long LastSeenScanId, string Status, string? Error);
public sealed record FileHashRow(long FileEntryId, string Algorithm, string Digest, long SizeAtHash, string ModifiedUtcAtHash, string CalculatedUtc, string State);

public sealed class Database : IDisposable
{
    private readonly bool _readOnly;

    public string DbPath { get; }
    public BackupNormalizerDbContext Context { get; }

    public Database(string dbPath) : this(dbPath, readOnly: false) { }

    internal Database(string dbPath, bool readOnly)
    {
        DbPath = Path.GetFullPath(dbPath);
        _readOnly = readOnly;

        if (readOnly)
        {
            if (!File.Exists(DbPath)) throw new FileNotFoundException("Database file not found.", DbPath);
        }
        else
        {
            var dir = Path.GetDirectoryName(DbPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true
        }.ToString();
        var options = new DbContextOptionsBuilder<BackupNormalizerDbContext>()
            .UseSqlite(connectionString)
            .Options;
        Context = new BackupNormalizerDbContext(options);

        try
        {
            // Keep the connection open so connection-scoped settings such as synchronous=NORMAL
            // remain in effect for the lifetime of this database facade.
            Context.Database.OpenConnection();
            if (!readOnly)
            {
                Context.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
                Context.Database.ExecuteSqlRaw("PRAGMA synchronous=NORMAL;");
                Context.Database.EnsureCreated();
            }
        }
        catch
        {
            Context.Dispose();
            throw;
        }
    }

    public void Dispose() => Context.Dispose();

    public static string UtcNow() => DateTime.UtcNow.ToString("o");

    // ---- Roots ----
    public void UpsertRoot(StorageRootRow r)
    {
        EnsureWritable();
        var exists = Context.StorageRoots.Any(x => x.Id == r.Id);
        if (exists)
        {
            Context.StorageRoots
                .Where(x => x.Id == r.Id)
                .ExecuteUpdate(setters => setters
                    .SetProperty(x => x.Name, r.Name)
                    .SetProperty(x => x.Path, r.Path)
                    .SetProperty(x => x.Role, r.Role)
                    .SetProperty(x => x.Writable, r.Writable)
                    .SetProperty(x => x.FileSystemId, r.FileSystemId)
                    .SetProperty(x => x.CaseSensitivity, r.CaseSensitivity));
            return;
        }

        AddAndSave(Context.StorageRoots, new StorageRootEntity
        {
            Id = r.Id,
            Name = r.Name,
            Path = r.Path,
            Role = r.Role,
            Writable = r.Writable,
            FileSystemId = r.FileSystemId,
            CaseSensitivity = r.CaseSensitivity,
            CreatedUtc = r.CreatedUtc
        });
    }

    public List<StorageRootRow> ListRoots() => Context.StorageRoots
        .AsNoTracking()
        .OrderBy(x => x.Id)
        .Select(x => new StorageRootRow(x.Id, x.Name, x.Path, x.Role, x.Writable, x.FileSystemId, x.CaseSensitivity, x.CreatedUtc))
        .ToList();

    public StorageRootRow? GetRoot(string id) => Context.StorageRoots
        .AsNoTracking()
        .Where(x => x.Id == id)
        .Select(x => new StorageRootRow(x.Id, x.Name, x.Path, x.Role, x.Writable, x.FileSystemId, x.CaseSensitivity, x.CreatedUtc))
        .FirstOrDefault();

    public long BeginScan(string rootId)
    {
        EnsureWritable();
        var scan = new ScanEntity { StorageRootId = rootId, StartedUtc = UtcNow(), Status = "Started" };
        AddAndSave(Context.Scans, scan);
        return scan.Id;
    }

    public void FinishScan(long scanId, string status)
    {
        EnsureWritable();
        Context.Scans
            .Where(x => x.Id == scanId)
            .ExecuteUpdate(setters => setters
                .SetProperty(x => x.CompletedUtc, UtcNow())
                .SetProperty(x => x.Status, status));
    }

    public FileEntryRow? GetFileEntry(string rootId, string rel) => Context.FileEntries
        .AsNoTracking()
        .Where(x => x.StorageRootId == rootId && x.RelativePath == rel)
        .Select(ToFileEntryRow())
        .FirstOrDefault();

    public long UpsertFileEntry(FileEntryRow e)
    {
        EnsureWritable();
        var id = Context.FileEntries
            .Where(x => x.StorageRootId == e.StorageRootId && x.RelativePath == e.RelativePath)
            .Select(x => x.Id)
            .FirstOrDefault();
        if (id != 0)
        {
            Context.FileEntries
                .Where(x => x.Id == id)
                .ExecuteUpdate(setters => setters
                    .SetProperty(x => x.Name, e.Name)
                    .SetProperty(x => x.Size, e.Size)
                    .SetProperty(x => x.ModifiedUtc, e.ModifiedUtc)
                    .SetProperty(x => x.CreatedUtc, e.CreatedUtc)
                    .SetProperty(x => x.FileIdentity, e.FileIdentity)
                    .SetProperty(x => x.LastSeenScanId, e.LastSeenScanId)
                    .SetProperty(x => x.Status, e.Status)
                    .SetProperty(x => x.Error, e.Error));
            return id;
        }

        var entry = new FileEntryEntity
        {
            StorageRootId = e.StorageRootId,
            RelativePath = e.RelativePath,
            Name = e.Name,
            Size = e.Size,
            ModifiedUtc = e.ModifiedUtc,
            CreatedUtc = e.CreatedUtc,
            FileIdentity = e.FileIdentity,
            LastSeenScanId = e.LastSeenScanId,
            Status = e.Status,
            Error = e.Error
        };
        AddAndSave(Context.FileEntries, entry);
        return entry.Id;
    }

    public List<FileEntryRow> ListFiles(string? rootId = null)
    {
        var query = Context.FileEntries.AsNoTracking();
        if (rootId != null) query = query.Where(x => x.StorageRootId == rootId);
        return query
            .OrderBy(x => x.StorageRootId)
            .ThenBy(x => x.RelativePath)
            .Select(ToFileEntryRow())
            .ToList();
    }

    public FileHashRow? GetHash(long fileEntryId, string algo) => Context.FileHashes
        .AsNoTracking()
        .Where(x => x.FileEntryId == fileEntryId && x.Algorithm == algo)
        .Select(x => new FileHashRow(x.FileEntryId, x.Algorithm, x.Digest, x.SizeAtHash, x.ModifiedUtcAtHash, x.CalculatedUtc, x.State))
        .FirstOrDefault();

    public void UpsertHash(FileHashRow h)
    {
        EnsureWritable();
        var exists = Context.FileHashes.Any(x => x.FileEntryId == h.FileEntryId && x.Algorithm == h.Algorithm);
        if (exists)
        {
            Context.FileHashes
                .Where(x => x.FileEntryId == h.FileEntryId && x.Algorithm == h.Algorithm)
                .ExecuteUpdate(setters => setters
                    .SetProperty(x => x.Digest, h.Digest)
                    .SetProperty(x => x.SizeAtHash, h.SizeAtHash)
                    .SetProperty(x => x.ModifiedUtcAtHash, h.ModifiedUtcAtHash)
                    .SetProperty(x => x.CalculatedUtc, h.CalculatedUtc)
                    .SetProperty(x => x.State, h.State));
            return;
        }

        AddAndSave(Context.FileHashes, new FileHashEntity
        {
            FileEntryId = h.FileEntryId,
            Algorithm = h.Algorithm,
            Digest = h.Digest,
            SizeAtHash = h.SizeAtHash,
            ModifiedUtcAtHash = h.ModifiedUtcAtHash,
            CalculatedUtc = h.CalculatedUtc,
            State = h.State
        });
    }

    public void MarkHashStale(long fileEntryId, string algo)
    {
        EnsureWritable();
        Context.FileHashes
            .Where(x => x.FileEntryId == fileEntryId && x.Algorithm == algo)
            .ExecuteUpdate(setters => setters.SetProperty(x => x.State, "Stale"));
    }

    // ---- Plans ----
    public void InsertPlan(string planId, string canonicalRootId, long estBytes, string status = "Planned")
    {
        EnsureWritable();
        AddAndSave(Context.Plans, new PlanEntity
        {
            Id = planId,
            CreatedUtc = UtcNow(),
            CanonicalRootId = canonicalRootId,
            Status = status,
            EstimatedBytesCopied = estBytes
        });
    }

    public void InsertOperation(string planId, int seq, string type, string? srcRoot, string? srcPath, string? dstRoot, string? dstPath, long size, string? hash, string status = "Planned")
    {
        EnsureWritable();
        AddAndSave(Context.PlanOperations, new PlanOperationEntity
        {
            PlanId = planId,
            Sequence = seq,
            Type = type,
            SourceRootId = srcRoot,
            SourcePath = srcPath,
            DestinationRootId = dstRoot,
            DestinationPath = dstPath,
            ExpectedSize = size,
            ExpectedHash = hash,
            Status = status
        });
    }

    public bool PlanExists(string planId) => Context.Plans.AsNoTracking().Any(x => x.Id == planId);

    public sealed record PlanOperationRow(long Id, int Sequence, string Type,
        string? SourceRoot, string? SourcePath, string? DestRoot, string? DestPath,
        long ExpectedSize, string? ExpectedHash, string Status, string? Error);

    public List<PlanOperationRow> ListPlanOperations(string planId, bool onlyProblems = false)
    {
        var query = Context.PlanOperations.AsNoTracking().Where(x => x.PlanId == planId);
        if (onlyProblems)
            query = query.Where(x => x.Status == "Conflict" || x.Status == "Failed" || x.Status == "Skipped");

        return query.OrderBy(x => x.Sequence)
            .Select(x => new PlanOperationRow(x.Id, x.Sequence, x.Type, x.SourceRootId, x.SourcePath,
                x.DestinationRootId, x.DestinationPath, x.ExpectedSize, x.ExpectedHash, x.Status, x.Error))
            .ToList();
    }

    private static System.Linq.Expressions.Expression<Func<FileEntryEntity, FileEntryRow>> ToFileEntryRow() => x =>
        new FileEntryRow(x.Id, x.StorageRootId, x.RelativePath, x.Name, x.Size, x.ModifiedUtc,
            x.CreatedUtc, x.FileIdentity, x.LastSeenScanId, x.Status, x.Error);

    private void AddAndSave<TEntity>(DbSet<TEntity> set, TEntity entity) where TEntity : class
    {
        set.Add(entity);
        Context.SaveChanges();
        Context.Entry(entity).State = EntityState.Detached;
    }

    private void EnsureWritable()
    {
        if (_readOnly) throw new InvalidOperationException("The database was opened read-only.");
    }
}
