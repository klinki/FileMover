using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BackupNormalizer;

public sealed record StorageRootRow(string Id, string Name, string Path, bool Writable, string FileSystemId, string CaseSensitivity, string CreatedUtc);
public sealed record ScanRow(long Id, string StorageRootId, string StartedUtc, string? CompletedUtc, string Status);
public sealed record FileEntryRow(long Id, string StorageRootId, string RelativePath, string Name, long Size, string ModifiedUtc, string? CreatedUtc, string? FileIdentity, long LastSeenScanId, string Status, string? Error);
public sealed record FileHashRow(long FileEntryId, string Algorithm, string Digest, long SizeAtHash, string ModifiedUtcAtHash, string CalculatedUtc, string State);

public sealed class Database : IDisposable
{
    private readonly bool _readOnly;

    public string DbPath { get; }

    // Lifetime policy: one DbContext per Database facade. All reads are
    // AsNoTracking projections and all writes are ExecuteUpdate/AddAndSave
    // (detached immediately), so the change tracker stays empty outside
    // explicit transactions. Never hold entities across calls.
    internal BackupNormalizerDbContext Context { get; }

    public Database(string dbPath) : this(dbPath, readOnly: false) { }

    public static Database OpenReadOnly(string dbPath) => new(dbPath, readOnly: true);

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
                Context.Database.Migrate();
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
        var previousPath = Context.StorageRoots.Where(x => x.Id == r.Id).Select(x => x.Path).FirstOrDefault();
        if (previousPath != null)
        {
            Context.StorageRoots
                .Where(x => x.Id == r.Id)
                .ExecuteUpdate(setters => setters
                    .SetProperty(x => x.Name, r.Name)
                    .SetProperty(x => x.Path, r.Path)
                    .SetProperty(x => x.Writable, r.Writable)
                    .SetProperty(x => x.FileSystemId, r.FileSystemId)
                    .SetProperty(x => x.CaseSensitivity, r.CaseSensitivity));
            if (!Paths.PathEquals(previousPath, r.Path))
            {
                Context.FileHashes
                    .Where(hash => Context.FileEntries.Any(entry =>
                        entry.Id == hash.FileEntryId && entry.StorageRootId == r.Id))
                    .ExecuteUpdate(setters => setters.SetProperty(hash => hash.State, HashState.Stale));
                var now = UtcNow();
                AddAndSave(Context.Scans, new ScanEntity
                {
                    StorageRootId = r.Id,
                    StartedUtc = now,
                    CompletedUtc = now,
                    Status = ScanStatus.Invalidated,
                });
            }
            return;
        }

        AddAndSave(Context.StorageRoots, new StorageRootEntity
        {
            Id = r.Id,
            Name = r.Name,
            Path = r.Path,
            Writable = r.Writable,
            FileSystemId = r.FileSystemId,
            CaseSensitivity = r.CaseSensitivity,
            CreatedUtc = r.CreatedUtc
        });
    }

    public List<StorageRootRow> ListRoots() => Context.StorageRoots
        .AsNoTracking()
        .OrderBy(x => x.Id)
        .Select(x => new StorageRootRow(x.Id, x.Name, x.Path, x.Writable, x.FileSystemId, x.CaseSensitivity, x.CreatedUtc))
        .ToList();

    public StorageRootRow? GetRoot(string id) => Context.StorageRoots
        .AsNoTracking()
        .Where(x => x.Id == id)
        .Select(x => new StorageRootRow(x.Id, x.Name, x.Path, x.Writable, x.FileSystemId, x.CaseSensitivity, x.CreatedUtc))
        .FirstOrDefault();

    public long BeginScan(string rootId)
    {
        EnsureWritable();
        var scan = new ScanEntity { StorageRootId = rootId, StartedUtc = UtcNow(), Status = ScanStatus.Started };
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

    public string? LatestScanStatus(string rootId) => Context.Scans.AsNoTracking()
        .Where(x => x.StorageRootId == rootId)
        .OrderByDescending(x => x.Id)
        .Select(x => x.Status)
        .FirstOrDefault();

    public int MarkUnseenFilesMissing(string rootId, long scanId)
    {
        EnsureWritable();
        return Context.FileEntries
            .Where(x => x.StorageRootId == rootId && x.LastSeenScanId != scanId && x.Status != FileStatus.Missing)
            .ExecuteUpdate(setters => setters
                .SetProperty(x => x.Status, FileStatus.Missing)
                .SetProperty(x => x.Error, (string?)null));
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

    public sealed record FileWithHashRow(long Id, string StorageRootId, string RelativePath, string Name,
        long Size, string ModifiedUtc, string? CreatedUtc, string? FileIdentity, long LastSeenScanId,
        string Status, string? Error, string? Digest);

    /// <summary>
    /// File entries with their usable full-file digest in a single query.
    /// A digest counts only when it is fresh (Ok state, matching size+mtime).
    /// </summary>
    public List<FileWithHashRow> ListFilesWithHashes(string? rootId, string algorithm)
    {
        var entries = Context.FileEntries.AsNoTracking();
        if (rootId != null) entries = entries.Where(x => x.StorageRootId == rootId);
        return entries
            .OrderBy(x => x.StorageRootId)
            .ThenBy(x => x.RelativePath)
            .GroupJoin(Context.FileHashes.AsNoTracking().Where(h => h.Algorithm == algorithm),
                entry => entry.Id, hash => hash.FileEntryId,
                (entry, hashes) => new { entry, digest = hashes
                    .Where(h => h.State == HashState.Ok && h.SizeAtHash == entry.Size && h.ModifiedUtcAtHash == entry.ModifiedUtc)
                    .Select(h => h.Digest)
                    .FirstOrDefault() })
            .Select(x => new FileWithHashRow(x.entry.Id, x.entry.StorageRootId, x.entry.RelativePath,
                x.entry.Name, x.entry.Size, x.entry.ModifiedUtc, x.entry.CreatedUtc, x.entry.FileIdentity,
                x.entry.LastSeenScanId, x.entry.Status, x.entry.Error, x.digest))
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
            .ExecuteUpdate(setters => setters.SetProperty(x => x.State, HashState.Stale));
    }

    // ---- Plans ----
    public void InsertPlan(string planId, string sourceDatabasePath, string sourceRootId, string sourceRootPath,
        string targetRootId, string targetRootPath, long estBytes, string status = PlanStatus.Planned)
    {
        EnsureWritable();
        AddAndSave(Context.Plans, new PlanEntity
        {
            Id = planId,
            CreatedUtc = UtcNow(),
            SourceDatabasePath = sourceDatabasePath,
            SourceRootId = sourceRootId,
            SourceRootPath = sourceRootPath,
            TargetRootId = targetRootId,
            TargetRootPath = targetRootPath,
            Status = status,
            EstimatedBytesCopied = estBytes
        });
    }

    public void InsertOperation(string planId, int seq, string type, string? sourceKind, string? srcRoot,
        string? srcPath, string? dstRoot, string? dstPath, long size, string? hash, string status = OpStatus.Planned)
    {
        EnsureWritable();
        AddAndSave(Context.PlanOperations, new PlanOperationEntity
        {
            PlanId = planId,
            Sequence = seq,
            Type = type,
            SourceKind = sourceKind,
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

    public sealed record PlanOperationRow(long Id, int Sequence, string Type, string? SourceKind,
        string? SourceRoot, string? SourcePath, string? DestRoot, string? DestPath,
        long ExpectedSize, string? ExpectedHash, string Status, string? Error);

    public List<PlanOperationRow> ListPlanOperations(string planId, bool onlyProblems = false)
    {
        var query = Context.PlanOperations.AsNoTracking().Where(x => x.PlanId == planId);
        if (onlyProblems)
            query = query.Where(x => x.Status == OpStatus.Conflict || x.Status == OpStatus.Failed || x.Status == OpStatus.Skipped);

        return query.OrderBy(x => x.Sequence)
            .Select(x => new PlanOperationRow(x.Id, x.Sequence, x.Type, x.SourceKind, x.SourceRootId, x.SourcePath,
                x.DestinationRootId, x.DestinationPath, x.ExpectedSize, x.ExpectedHash, x.Status, x.Error))
            .ToList();
    }

    public sealed record PlanInfo(string Id, string CreatedUtc, string SourceDatabasePath,
        string SourceRootId, string SourceRootPath, string TargetRootId, string TargetRootPath,
        string Status, long EstimatedBytesCopied);

    public PlanInfo? GetPlan(string planId) => Context.Plans.AsNoTracking()
        .Where(x => x.Id == planId)
        .Select(x => new PlanInfo(x.Id, x.CreatedUtc, x.SourceDatabasePath, x.SourceRootId,
            x.SourceRootPath, x.TargetRootId, x.TargetRootPath, x.Status, x.EstimatedBytesCopied))
        .FirstOrDefault();

    public void UpdatePlanStatus(string planId, string status)
    {
        EnsureWritable();
        Context.Plans
            .Where(x => x.Id == planId)
            .ExecuteUpdate(setters => setters.SetProperty(x => x.Status, status));
    }

    public Dictionary<string, int> GetOperationCounts(string planId) => Context.PlanOperations
        .AsNoTracking()
        .Where(x => x.PlanId == planId)
        .GroupBy(x => x.Type)
        .Select(g => new { g.Key, Count = g.Count() })
        .ToDictionary(x => x.Key, x => x.Count);

    public void MarkOperationStarted(long operationId, string startedUtc)
    {
        EnsureWritable();
        Context.PlanOperations
            .Where(x => x.Id == operationId)
            .ExecuteUpdate(setters => setters
                .SetProperty(x => x.Status, OpStatus.Started)
                .SetProperty(x => x.StartedUtc, startedUtc));
    }

    public void MarkOperation(long operationId, string status, string? error, string completedUtc)
    {
        EnsureWritable();
        Context.PlanOperations
            .Where(x => x.Id == operationId)
            .ExecuteUpdate(setters => setters
                .SetProperty(x => x.Status, status)
                .SetProperty(x => x.CompletedUtc, completedUtc)
                .SetProperty(x => x.Error, error));
    }

    public void AddExecutionLog(long operationId, string level, string message, string timestampUtc)
    {
        EnsureWritable();
        AddAndSave(Context.ExecutionLogs, new ExecutionLogEntity
        {
            PlanOperationId = operationId,
            Level = level,
            Message = message,
            TimestampUtc = timestampUtc,
        });
    }

    public sealed record CopyCandidate(string RootId, string RelativePath, long Size);

    public List<CopyCandidate> ListCompletedCopies(string planId, string hash) => Context.PlanOperations
        .AsNoTracking()
        .Where(x => x.PlanId == planId && x.Status == OpStatus.Completed && x.ExpectedHash == hash
            && (x.Type == OpType.Copy || x.Type == OpType.Move)
            && x.DestinationRootId != null && x.DestinationPath != null)
        .Select(x => new CopyCandidate(x.DestinationRootId!, x.DestinationPath!, x.ExpectedSize))
        .ToList();

    public List<CopyCandidate> ListContentCopies(string targetRootId, string excludeRootId, string excludePath, string hash) =>
        (from fileHash in Context.FileHashes.AsNoTracking()
         join entry in Context.FileEntries.AsNoTracking() on fileHash.FileEntryId equals entry.Id
         where fileHash.Digest == hash && fileHash.State == HashState.Ok && entry.Status == FileStatus.Ok
            && entry.StorageRootId == targetRootId
            && !(entry.StorageRootId == excludeRootId && entry.RelativePath == excludePath)
         select new CopyCandidate(entry.StorageRootId, entry.RelativePath, entry.Size))
        .ToList();

    public sealed record PlanOperationSeed(int Sequence, string Type, string? SourceKind,
        string? SourceRoot, string? SourcePath, string? DestRoot, string? DestPath,
        long ExpectedSize, string? ExpectedHash);

    public void AddPlanWithOperations(string planId, string createdUtc, string sourceDatabasePath,
        string sourceRootId, string sourceRootPath, string targetRootId, string targetRootPath,
        string status, long estimatedBytesCopied, IEnumerable<PlanOperationSeed> operations)
    {
        EnsureWritable();
        using var transaction = Context.Database.BeginTransaction();
        Context.Plans.Add(new PlanEntity
        {
            Id = planId,
            CreatedUtc = createdUtc,
            SourceDatabasePath = sourceDatabasePath,
            SourceRootId = sourceRootId,
            SourceRootPath = sourceRootPath,
            TargetRootId = targetRootId,
            TargetRootPath = targetRootPath,
            Status = status,
            EstimatedBytesCopied = estimatedBytesCopied,
        });
        foreach (var op in operations)
        {
            Context.PlanOperations.Add(new PlanOperationEntity
            {
                PlanId = planId,
                Sequence = op.Sequence,
                Type = op.Type,
                SourceKind = op.SourceKind,
                SourceRootId = op.SourceRoot,
                SourcePath = op.SourcePath,
                DestinationRootId = op.DestRoot,
                DestinationPath = op.DestPath,
                ExpectedSize = op.ExpectedSize,
                ExpectedHash = op.ExpectedHash,
                Status = OpStatus.Planned,
            });
        }
        Context.SaveChanges();
        transaction.Commit();
        Context.ChangeTracker.Clear();
    }

    public sealed class DatabaseTransaction : IDisposable
    {
        private readonly Database _database;
        private readonly Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction _transaction;
        private bool _completed;

        internal DatabaseTransaction(Database database)
        {
            _database = database;
            _transaction = database.Context.Database.BeginTransaction();
        }

        public void Commit() { _transaction.Commit(); _completed = true; }
        public void Rollback() { _transaction.Rollback(); _completed = true; _database.Context.ChangeTracker.Clear(); }

        public void Dispose()
        {
            if (!_completed)
            {
                try { _transaction.Rollback(); } catch { }
                _database.Context.ChangeTracker.Clear();
            }
            _transaction.Dispose();
        }
    }

    public DatabaseTransaction BeginTransaction()
    {
        EnsureWritable();
        return new DatabaseTransaction(this);
    }

    public List<string> AppliedMigrations() => Context.Database.GetAppliedMigrations().ToList();
    public List<string> PendingMigrations() => Context.Database.GetPendingMigrations().ToList();

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
