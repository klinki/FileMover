using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BackupNormalizer;

public sealed record StorageRootRow(
    string Id,
    string Name,
    string Path,
    bool Writable,
    string FileSystemId,
    string CaseSensitivity,
    string CreatedUtc
);

public sealed record ScanRow(
    long Id,
    string StorageRootId,
    string StartedUtc,
    string? CompletedUtc,
    string Status
);

public sealed record ScanCheckpointRow(
    string StorageRootId,
    string RootPath,
    string VolumeIdentity,
    string RootIdentity,
    string JournalId,
    long NextUsn,
    long ScanId
);

public sealed record FileEntryRow(
    long Id,
    string StorageRootId,
    string RelativePath,
    string Name,
    long Size,
    string ModifiedUtc,
    string? CreatedUtc,
    string? FileIdentity,
    long LastSeenScanId,
    string Status,
    string? Error,
    string EntryKind = BackupNormalizer.EntryKind.File,
    string? LinkTarget = null,
    string? TargetPath = null,
    string? LinkNote = null
);

public sealed record FileHashRow(
    long FileEntryId,
    string Algorithm,
    string Digest,
    long SizeAtHash,
    string ModifiedUtcAtHash,
    string CalculatedUtc,
    string State
);

public sealed partial class Database : IDisposable
{
    private readonly bool _readOnly;

    public string DbPath { get; }

    // Lifetime policy: one DbContext per Database facade. All reads are
    // AsNoTracking projections and all writes are ExecuteUpdate/AddAndSave
    // (detached immediately), so the change tracker stays empty outside
    // explicit transactions. Never hold entities across calls.
    internal BackupNormalizerDbContext Context { get; }

    // The tested EF AOT generator requires local context/scalar captures and
    // expression setters. Keep each query complete so both builds use the same SQL.

    public Database(string dbPath)
        : this(dbPath, readOnly: false) { }

    public static Database OpenReadOnly(string dbPath, bool pooling = true) =>
        new(dbPath, readOnly: true, pooling: pooling);

    public static Database OpenWritable(string dbPath, bool pooling = true) =>
        new(dbPath, readOnly: false, pooling: pooling);

    internal Database(string dbPath, bool readOnly, bool pooling = true)
    {
        DbPath = Path.GetFullPath(dbPath);
        _readOnly = readOnly;

        if (readOnly)
        {
            if (!File.Exists(DbPath))
            {
                throw new FileNotFoundException("Database file not found.", DbPath);
            }
        }
        else
        {
            var dir = Path.GetDirectoryName(DbPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }

        var connectionStringBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
        };
        if (!pooling)
        {
            connectionStringBuilder.Pooling = false;
        }

        string connectionString = connectionStringBuilder.ToString();
#if NATIVE_AOT
        if (readOnly)
        {
            // Older inventories are never read best-effort; they must be migrated first.
            DatabaseSchema.RequireCurrent(DbPath);
        }
        else
        {
            DatabaseSchema.EnsureCurrent(DbPath);
        }
#endif
        var options = new DbContextOptionsBuilder<BackupNormalizerDbContext>()
            .UseSqlite(connectionString)
            .Options;
        Context = new BackupNormalizerDbContext(options);

        try
        {
            // Keep the connection open so connection-scoped settings such as synchronous=NORMAL
            // remain in effect for the lifetime of this database facade.
            Context.Database.OpenConnection();
#if !NATIVE_AOT
            if (readOnly)
            {
                int applied = Context.Database.GetAppliedMigrations().Count();
                int pending = Context.Database.GetPendingMigrations().Count();
                if (pending != 0)
                {
                    throw new DatabaseNeedsMigrationException(DbPath, applied, applied + pending);
                }
            }
#endif
            ((SqliteConnection)Context.Database.GetDbConnection()).CreateCollation(
                "BN_PATH",
                (left, right) =>
                    (
                        OperatingSystem.IsWindows()
                            ? StringComparer.OrdinalIgnoreCase
                            : StringComparer.Ordinal
                    ).Compare(left, right)
            );
            if (!readOnly)
            {
                Context.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
                Context.Database.ExecuteSqlRaw("PRAGMA synchronous=NORMAL;");
#if !NATIVE_AOT
                Context.Database.Migrate();
#endif
            }
        }
        catch
        {
            Context.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        var context = Context;
        context.Dispose();
    }

    public static string UtcNow() => DateTime.UtcNow.ToString("o");


    // ---- Roots ----
    public void UpsertRoot(StorageRootRow r)
    {
        var rootId = r.Id;
        var rootName = r.Name;
        var rootPath = r.Path;
        var rootWritable = r.Writable;
        var rootFileSystemId = r.FileSystemId;
        var rootCaseSensitivity = r.CaseSensitivity;
        var rootCreatedUtc = r.CreatedUtc;
        var context = Context;
        EnsureWritable();
        var previousPath = context
            .StorageRoots.Where(x => x.Id == rootId)
            .Select(x => x.Path)
            .FirstOrDefault();
        if (previousPath != null)
        {
            context
                .StorageRoots.Where(x => x.Id == rootId)
                .ExecuteUpdate(setters =>
                    setters
                        .SetProperty(x => x.Name, x => rootName)
                        .SetProperty(x => x.Path, x => rootPath)
                        .SetProperty(x => x.Writable, x => rootWritable)
                        .SetProperty(x => x.FileSystemId, x => rootFileSystemId)
                        .SetProperty(x => x.CaseSensitivity, x => rootCaseSensitivity)
                );
            if (!Paths.PathEquals(previousPath, rootPath))
            {
                ClearScanCheckpoint(rootId);
                context
                    .FileHashes.Where(hash =>
                        context.FileEntries.Any(entry =>
                            entry.Id == hash.FileEntryId && entry.StorageRootId == rootId
                        )
                    )
                    .ExecuteUpdate(setters =>
                        setters.SetProperty(hash => hash.State, hash => HashState.Stale)
                    );
                var now = UtcNow();
                AddAndSave(
                    context.Scans,
                    new ScanEntity
                    {
                        StorageRootId = rootId,
                        StartedUtc = now,
                        CompletedUtc = now,
                        Status = ScanStatus.Invalidated,
                    }
                );
            }
            return;
        }

        AddAndSave(
            context.StorageRoots,
            new StorageRootEntity
            {
                Id = rootId,
                Name = rootName,
                Path = rootPath,
                Writable = rootWritable,
                FileSystemId = rootFileSystemId,
                CaseSensitivity = rootCaseSensitivity,
                CreatedUtc = rootCreatedUtc,
            }
        );
    }

    public List<StorageRootRow> ListRoots()
    {
        var context = Context;
        return context
            .StorageRoots.AsNoTracking()
            .OrderBy(x => x.Id)
            .Select(x => new StorageRootRow(
                x.Id,
                x.Name,
                x.Path,
                x.Writable,
                x.FileSystemId,
                x.CaseSensitivity,
                x.CreatedUtc
            ))
            .ToList();
    }

    public StorageRootRow? GetRoot(string id)
    {
        var context = Context;
        return context
            .StorageRoots.AsNoTracking()
            .Where(x => x.Id == id)
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
    }

    public long BeginScan(string rootId)
    {
        var context = Context;
        EnsureWritable();
        var scan = new ScanEntity
        {
            StorageRootId = rootId,
            StartedUtc = UtcNow(),
            Status = ScanStatus.Started,
        };
        AddAndSave(context.Scans, scan);
        return scan.Id;
    }

    public void FinishScan(long scanId, string status)
    {
        var completedUtc = UtcNow();
        var context = Context;
        EnsureWritable();
        context
            .Scans.Where(x => x.Id == scanId)
            .ExecuteUpdate(setters =>
                setters
                    .SetProperty(x => x.CompletedUtc, x => completedUtc)
                    .SetProperty(x => x.Status, x => status)
            );
    }

    public string? LatestScanStatus(string rootId)
    {
        var context = Context;
        return context
            .Scans.AsNoTracking()
            .Where(x => x.StorageRootId == rootId)
            .OrderByDescending(x => x.Id)
            .Select(x => x.Status)
            .FirstOrDefault();
    }

    public ScanRow? LatestScan(string rootId)
    {
        var context = Context;
        return context
            .Scans.AsNoTracking()
            .Where(x => x.StorageRootId == rootId)
            .OrderByDescending(x => x.Id)
            .Select(x => new ScanRow(x.Id, x.StorageRootId, x.StartedUtc, x.CompletedUtc, x.Status))
            .FirstOrDefault();
    }

    public ScanCheckpointRow? GetScanCheckpoint(string rootId)
    {
        var context = Context;
        return context
            .ScanCheckpoints.AsNoTracking()
            .Where(x => x.StorageRootId == rootId)
            .Select(x => new ScanCheckpointRow(
                x.StorageRootId,
                x.RootPath,
                x.VolumeIdentity,
                x.RootIdentity,
                x.JournalId,
                x.NextUsn,
                x.ScanId
            ))
            .FirstOrDefault();
    }

    public void SaveScanCheckpoint(ScanCheckpointRow checkpoint)
    {
        var context = Context;
        EnsureWritable();
        ClearScanCheckpoint(checkpoint.StorageRootId);
        AddAndSave(
            context.ScanCheckpoints,
            new ScanCheckpointEntity
            {
                StorageRootId = checkpoint.StorageRootId,
                RootPath = checkpoint.RootPath,
                VolumeIdentity = checkpoint.VolumeIdentity,
                RootIdentity = checkpoint.RootIdentity,
                JournalId = checkpoint.JournalId,
                NextUsn = checkpoint.NextUsn,
                ScanId = checkpoint.ScanId,
            }
        );
    }

    public void ClearScanCheckpoint(string rootId)
    {
        var context = Context;
        EnsureWritable();
        context.ScanCheckpoints.Where(x => x.StorageRootId == rootId).ExecuteDelete();
    }

    public void MarkRootHashesStale(string rootId)
    {
        var context = Context;
        EnsureWritable();
        context
            .FileHashes.Where(h =>
                context.FileEntries.Any(e =>
                    e.Id == h.FileEntryId && e.StorageRootId == rootId
                )
            )
            .ExecuteUpdate(setters => setters.SetProperty(h => h.State, h => HashState.Stale));
    }

    public void MarkPathHashesStale(string rootId, string relativePath)
    {
        var context = Context;
        EnsureWritable();
        context
            .FileHashes.Where(h =>
                context.FileEntries.Any(e =>
                    e.Id == h.FileEntryId
                    && e.StorageRootId == rootId
                    && e.RelativePath == relativePath
                )
            )
            .ExecuteUpdate(setters => setters.SetProperty(h => h.State, h => HashState.Stale));
    }

    public void MarkPathMissing(string rootId, string relativePath)
    {
        var context = Context;
        EnsureWritable();
        context
            .FileEntries.Where(e =>
                e.StorageRootId == rootId && e.RelativePath == relativePath
            )
            .ExecuteUpdate(setters =>
                setters
                    .SetProperty(e => e.Status, e => FileStatus.Missing)
                    .SetProperty(e => e.Error, e => (string?)null)
            );
    }

    internal void RetireDatabasePaths(string rootId, string[] relativePaths)
    {
        var queryRelativePaths = relativePaths.ToList();
        var context = Context;
        EnsureWritable();
        if (queryRelativePaths.Count == 0)
            return;
        context
            .FileHashes.Where(h =>
                context.FileEntries.Any(e =>
                    e.Id == h.FileEntryId
                    && e.StorageRootId == rootId
                    && queryRelativePaths.Contains(EF.Functions.Collate(e.RelativePath, "BN_PATH"))
                )
            )
            .ExecuteUpdate(setters => setters.SetProperty(h => h.State, h => HashState.Stale));
        context
            .FileEntries.Where(e =>
                e.StorageRootId == rootId
                && queryRelativePaths.Contains(EF.Functions.Collate(e.RelativePath, "BN_PATH"))
                && e.Status != FileStatus.Missing
            )
            .ExecuteUpdate(setters =>
                setters
                    .SetProperty(e => e.Status, e => FileStatus.Missing)
                    .SetProperty(e => e.Error, e => (string?)null)
            );
    }

    public int MarkUnseenFilesMissing(string rootId, long scanId)
    {
        var context = Context;
        EnsureWritable();
        return context
            .FileEntries.Where(x =>
                x.StorageRootId == rootId
                && x.LastSeenScanId != scanId
                && x.Status != FileStatus.Missing
            )
            .ExecuteUpdate(setters =>
                setters
                    .SetProperty(x => x.Status, x => FileStatus.Missing)
                    .SetProperty(x => x.Error, x => (string?)null)
            );
    }

    public FileEntryRow? GetFileEntry(string rootId, string rel)
    {
        var context = Context;
        return context
            .FileEntries.AsNoTracking()
            .Where(x => x.StorageRootId == rootId && x.RelativePath == rel)
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
    }

    public long UpsertFileEntry(FileEntryRow e)
    {
        var entryStorageRootId = e.StorageRootId;
        var entryRelativePath = e.RelativePath;
        var entryName = e.Name;
        var entrySize = e.Size;
        var entryModifiedUtc = e.ModifiedUtc;
        var entryCreatedUtc = e.CreatedUtc;
        var entryFileIdentity = e.FileIdentity;
        var entryLastSeenScanId = e.LastSeenScanId;
        var entryStatus = e.Status;
        var entryError = e.Error;
        var entryEntryKind = e.EntryKind;
        var entryLinkTarget = e.LinkTarget;
        var entryTargetPath = e.TargetPath;
        var entryLinkNote = e.LinkNote;
        var context = Context;
        EnsureWritable();
        var previous = context
            .FileEntries.Where(x =>
                x.StorageRootId == entryStorageRootId && x.RelativePath == entryRelativePath
            )
            .Select(x => new { x.Id, x.EntryKind })
            .FirstOrDefault();
        if (previous != null)
        {
            long id = previous.Id;
            if (previous.EntryKind != entryEntryKind)
            {
                MarkFileHashesStale(id);
            }

            context
                .FileEntries.Where(x => x.Id == id)
                .ExecuteUpdate(setters =>
                    setters
                        .SetProperty(x => x.Name, x => entryName)
                        .SetProperty(x => x.Size, x => entrySize)
                        .SetProperty(x => x.ModifiedUtc, x => entryModifiedUtc)
                        .SetProperty(x => x.CreatedUtc, x => entryCreatedUtc)
                        .SetProperty(x => x.FileIdentity, x => entryFileIdentity)
                        .SetProperty(x => x.LastSeenScanId, x => entryLastSeenScanId)
                        .SetProperty(x => x.Status, x => entryStatus)
                        .SetProperty(x => x.Error, x => entryError)
                        .SetProperty(x => x.EntryKind, x => entryEntryKind)
                        .SetProperty(x => x.LinkTarget, x => entryLinkTarget)
                        .SetProperty(x => x.TargetPath, x => entryTargetPath)
                        .SetProperty(x => x.LinkNote, x => entryLinkNote)
                );
            return id;
        }

        var entry = new FileEntryEntity
        {
            StorageRootId = entryStorageRootId,
            RelativePath = entryRelativePath,
            Name = entryName,
            Size = entrySize,
            ModifiedUtc = entryModifiedUtc,
            CreatedUtc = entryCreatedUtc,
            FileIdentity = entryFileIdentity,
            LastSeenScanId = entryLastSeenScanId,
            Status = entryStatus,
            Error = entryError,
            EntryKind = entryEntryKind,
            LinkTarget = entryLinkTarget,
            TargetPath = entryTargetPath,
            LinkNote = entryLinkNote,
        };
        AddAndSave(context.FileEntries, entry);
        return entry.Id;
    }

    public List<FileEntryRow> ListFiles(string? rootId = null)
    {
        var context = Context;
        return context
            .FileEntries.AsNoTracking()
            .Where(x => rootId == null || x.StorageRootId == rootId)
            .OrderBy(x => x.StorageRootId)
            .ThenBy(x => x.RelativePath)
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
            .ToList();
    }

    public int CountFiles(string rootId)
    {
        var context = Context;
        return context
            .FileEntries.AsNoTracking()
            .Where(x => x.StorageRootId == rootId)
            .Count();
    }

    public sealed record FileWithHashRow(
        long Id,
        string StorageRootId,
        string RelativePath,
        string Name,
        long Size,
        string ModifiedUtc,
        string? CreatedUtc,
        string? FileIdentity,
        long LastSeenScanId,
        string Status,
        string? Error,
        string? Digest,
        string EntryKind = BackupNormalizer.EntryKind.File,
        string? LinkTarget = null,
        string? TargetPath = null,
        string? LinkNote = null
    );

    /// <summary>
    /// File entries with their usable full-file digest in a single query.
    /// A digest counts only when it is fresh (Ok state, matching size+mtime).
    /// </summary>
    public List<FileWithHashRow> ListFilesWithHashes(string? rootId, string algorithm)
    {
        var context = Context;
        return context
                .FileEntries.AsNoTracking()
            .Where(entry => rootId == null || entry.StorageRootId == rootId)
            .OrderBy(entry => entry.StorageRootId)
            .ThenBy(entry => entry.RelativePath)
            .Select(entry => new FileWithHashRow(
                entry.Id,
                entry.StorageRootId,
                entry.RelativePath,
                entry.Name,
                entry.Size,
                entry.ModifiedUtc,
                entry.CreatedUtc,
                entry.FileIdentity,
                entry.LastSeenScanId,
                entry.Status,
                entry.Error,
                entry.Status == FileStatus.Ok && entry.EntryKind == EntryKind.File
                    ? context
                        .FileHashes.Where(h =>
                            h.FileEntryId == entry.Id
                            && h.Algorithm == algorithm
                            && h.State == HashState.Ok
                            && h.SizeAtHash == entry.Size
                            && h.ModifiedUtcAtHash == entry.ModifiedUtc
                        )
                        .Select(h => h.Digest)
                        .FirstOrDefault()
                    : null,
                entry.EntryKind,
                entry.LinkTarget,
                entry.TargetPath,
                entry.LinkNote
            ))
            .ToList();
    }

    public FileHashRow? GetHash(long fileEntryId, string algo)
    {
        var context = Context;
        return context
            .FileHashes.AsNoTracking()
            .Where(x => x.FileEntryId == fileEntryId && x.Algorithm == algo)
            .Select(x => new FileHashRow(
                x.FileEntryId,
                x.Algorithm,
                x.Digest,
                x.SizeAtHash,
                x.ModifiedUtcAtHash,
                x.CalculatedUtc,
                x.State
            ))
            .FirstOrDefault();
    }

    public void UpsertHash(FileHashRow h)
    {
        var hashFileEntryId = h.FileEntryId;
        var hashAlgorithm = h.Algorithm;
        var hashDigest = h.Digest;
        var hashSizeAtHash = h.SizeAtHash;
        var hashModifiedUtcAtHash = h.ModifiedUtcAtHash;
        var hashCalculatedUtc = h.CalculatedUtc;
        var hashState = h.State;
        var context = Context;
        EnsureWritable();
        var exists = context.FileHashes.Any(x =>
            x.FileEntryId == hashFileEntryId && x.Algorithm == hashAlgorithm
        );
        if (exists)
        {
            context
                .FileHashes.Where(x =>
                    x.FileEntryId == hashFileEntryId && x.Algorithm == hashAlgorithm
                )
                .ExecuteUpdate(setters =>
                    setters
                        .SetProperty(x => x.Digest, x => hashDigest)
                        .SetProperty(x => x.SizeAtHash, x => hashSizeAtHash)
                        .SetProperty(x => x.ModifiedUtcAtHash, x => hashModifiedUtcAtHash)
                        .SetProperty(x => x.CalculatedUtc, x => hashCalculatedUtc)
                        .SetProperty(x => x.State, x => hashState)
                );
            return;
        }

        AddAndSave(
            context.FileHashes,
            new FileHashEntity
            {
                FileEntryId = hashFileEntryId,
                Algorithm = hashAlgorithm,
                Digest = hashDigest,
                SizeAtHash = hashSizeAtHash,
                ModifiedUtcAtHash = hashModifiedUtcAtHash,
                CalculatedUtc = hashCalculatedUtc,
                State = hashState,
            }
        );
    }

    public void MarkHashStale(long fileEntryId, string algo)
    {
        var context = Context;
        EnsureWritable();
        context
            .FileHashes.Where(x => x.FileEntryId == fileEntryId && x.Algorithm == algo)
            .ExecuteUpdate(setters => setters.SetProperty(x => x.State, x => HashState.Stale));
    }

    public void MarkFileHashesStale(long fileEntryId)
    {
        var context = Context;
        EnsureWritable();
        context
            .FileHashes.Where(x => x.FileEntryId == fileEntryId)
            .ExecuteUpdate(setters => setters.SetProperty(x => x.State, x => HashState.Stale));
    }

    // ---- Plans ----
    public void InsertPlan(
        string planId,
        string sourceDatabasePath,
        string sourceRootId,
        string sourceRootPath,
        string targetRootId,
        string targetRootPath,
        long estBytes,
        string status = PlanStatus.Planned
    )
    {
        var context = Context;
        EnsureWritable();
        AddAndSave(
            context.Plans,
            new PlanEntity
            {
                Id = planId,
                CreatedUtc = UtcNow(),
                SourceDatabasePath = sourceDatabasePath,
                SourceRootId = sourceRootId,
                SourceRootPath = sourceRootPath,
                TargetRootId = targetRootId,
                TargetRootPath = targetRootPath,
                Status = status,
                EstimatedBytesCopied = estBytes,
            }
        );
    }

    public void InsertOperation(
        string planId,
        int seq,
        string type,
        string? sourceKind,
        string? srcRoot,
        string? srcPath,
        string? dstRoot,
        string? dstPath,
        long size,
        string? hash,
        string status = OpStatus.Planned,
        string? skipReason = null
    )
    {
        var context = Context;
        EnsureWritable();
        AddAndSave(
            context.PlanOperations,
            new PlanOperationEntity
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
                Status = status,
                SkipReason = skipReason,
            }
        );
    }

    public bool PlanExists(string planId)
    {
        var context = Context;
        return context.Plans.AsNoTracking().Any(x => x.Id == planId);
    }

    public sealed record PlanOperationRow(
        long Id,
        int Sequence,
        string Type,
        string? SourceKind,
        string? SourceRoot,
        string? SourcePath,
        string? DestRoot,
        string? DestPath,
        long ExpectedSize,
        string? ExpectedHash,
        string Status,
        string? Error,
        string? SkipReason = null
    );

    public List<PlanOperationRow> ListPlanOperations(string planId, bool onlyProblems = false)
    {
        var context = Context;
        // One query: the optional status filter is a captured-scalar predicate, not LINQ composition.
        return context
            .PlanOperations.AsNoTracking()
            .Where(x =>
                x.PlanId == planId
                && (
                    !onlyProblems
                    || x.Status == OpStatus.Conflict
                    || x.Status == OpStatus.Failed
                    || x.Status == OpStatus.Skipped
                )
            )
            .OrderBy(x => x.Sequence)
            .Select(x => new PlanOperationRow(
                x.Id,
                x.Sequence,
                x.Type,
                x.SourceKind,
                x.SourceRootId,
                x.SourcePath,
                x.DestinationRootId,
                x.DestinationPath,
                x.ExpectedSize,
                x.ExpectedHash,
                x.Status,
                x.Error,
                x.SkipReason
            ))
            .ToList();
    }

    public sealed record PlanInfo(
        string Id,
        string CreatedUtc,
        string SourceDatabasePath,
        string SourceRootId,
        string SourceRootPath,
        string TargetRootId,
        string TargetRootPath,
        string Status,
        long EstimatedBytesCopied,
        string? ExecutionSourceRootPath,
        string? ExecutionTargetRootPath
    );

    public PlanInfo? GetPlan(string planId)
    {
        var context = Context;
        return context
            .Plans.AsNoTracking()
            .Where(x => x.Id == planId)
            .Select(x => new PlanInfo(
                x.Id,
                x.CreatedUtc,
                x.SourceDatabasePath,
                x.SourceRootId,
                x.SourceRootPath,
                x.TargetRootId,
                x.TargetRootPath,
                x.Status,
                x.EstimatedBytesCopied,
                x.ExecutionSourceRootPath,
                x.ExecutionTargetRootPath
            ))
            .FirstOrDefault();
    }

    public void BindPlanExecution(string planId, string? sourcePath, string targetPath)
    {
        var context = Context;
        EnsureWritable();
        sourcePath = sourcePath == null ? null : Path.GetFullPath(sourcePath);
        targetPath = Path.GetFullPath(targetPath);
        using var transaction = context.Database.BeginTransaction();
        var plan =
            GetPlan(planId)
            ?? throw new InvalidOperationException($"unknown plan '{planId}'");
        const string replayAdvice =
            "Import the original plan JSON into a fresh database to execute on another root.";
        if (plan.ExecutionTargetRootPath != null)
        {
            if (
                !Paths.PathEquals(plan.ExecutionTargetRootPath, targetPath)
                || (
                    sourcePath != null
                    && (
                        plan.ExecutionSourceRootPath == null
                        || !Paths.PathEquals(plan.ExecutionSourceRootPath, sourcePath)
                    )
                )
            )
            {
                throw new InvalidOperationException(
                    $"Plan '{planId}' is bound to different execution roots. {replayAdvice}"
                );
            }
        }
        else
        {
            if (
                context.PlanOperations.Any(x =>
                    x.PlanId == planId && x.Status != OpStatus.Planned
                )
            )
            {
                throw new InvalidOperationException(
                    $"Plan '{planId}' has historical execution without recorded roots. {replayAdvice}"
                );
            }

            context
                .Plans.Where(x => x.Id == planId)
                .ExecuteUpdate(setters =>
                    setters
                        .SetProperty(x => x.ExecutionSourceRootPath, x => sourcePath)
                        .SetProperty(x => x.ExecutionTargetRootPath, x => targetPath)
                );
        }
        transaction.Commit();
    }

    public void MarkFilesMissing(long[] fileIds)
    {
        var queryFileIds = fileIds.ToList();
        var context = Context;
        if (queryFileIds.Count == 0)
        {
            return;
        }

        EnsureWritable();
        using var transaction = context.Database.BeginTransaction();
        context
            .FileHashes.Where(x => queryFileIds.Contains(x.FileEntryId))
            .ExecuteUpdate(setters => setters.SetProperty(x => x.State, x => HashState.Stale));
        context
            .FileEntries.Where(x => queryFileIds.Contains(x.Id))
            .ExecuteUpdate(setters =>
                setters
                    .SetProperty(x => x.Status, x => FileStatus.Missing)
                    .SetProperty(x => x.Error, x => (string?)null)
            );
        transaction.Commit();
    }

    public void UpdatePlanStatus(string planId, string status)
    {
        var context = Context;
        EnsureWritable();
        context
            .Plans.Where(x => x.Id == planId)
            .ExecuteUpdate(setters => setters.SetProperty(x => x.Status, x => status));
    }

    public Dictionary<string, int> GetOperationCounts(string planId)
    {
        var context = Context;
        return context
            .PlanOperations.AsNoTracking()
            .Where(x => x.PlanId == planId)
            .GroupBy(x => x.Type)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToList()
            .ToDictionary(x => x.Key, x => x.Count);
    }

    public void MarkOperationStarted(long operationId, string startedUtc)
    {
        var context = Context;
        EnsureWritable();
        context
            .PlanOperations.Where(x => x.Id == operationId)
            .ExecuteUpdate(setters =>
                setters
                    .SetProperty(x => x.Status, x => OpStatus.Started)
                    .SetProperty(x => x.StartedUtc, x => startedUtc)
            );
    }

    public void MarkOperation(long operationId, string status, string? error, string completedUtc)
    {
        var context = Context;
        EnsureWritable();
        context
            .PlanOperations.Where(x => x.Id == operationId)
            .ExecuteUpdate(setters =>
                setters
                    .SetProperty(x => x.Status, x => status)
                    .SetProperty(x => x.CompletedUtc, x => completedUtc)
                    .SetProperty(x => x.Error, x => error)
            );
    }

    public void MarkOperationSkipped(long operationId, string reason)
    {
        var completedUtc = UtcNow();
        var context = Context;
        EnsureWritable();
        context
            .PlanOperations.Where(x => x.Id == operationId)
            .ExecuteUpdate(setters =>
                setters
                    .SetProperty(x => x.Status, x => OpStatus.Skipped)
                    .SetProperty(x => x.CompletedUtc, x => completedUtc)
                    .SetProperty(x => x.Error, x => (string?)null)
                    .SetProperty(x => x.SkipReason, x => reason)
            );
    }

    public void AddExecutionLog(long operationId, string level, string message, string timestampUtc)
    {
        var context = Context;
        EnsureWritable();
        AddAndSave(
            context.ExecutionLogs,
            new ExecutionLogEntity
            {
                PlanOperationId = operationId,
                Level = level,
                Message = message,
                TimestampUtc = timestampUtc,
            }
        );
    }

    public sealed record CopyCandidate(string RootId, string RelativePath, long Size);

    public List<CopyCandidate> ListCompletedCopies(string planId, string hash)
    {
        var context = Context;
        return context
            .PlanOperations.AsNoTracking()
            .Where(x =>
                x.PlanId == planId
                && x.Status == OpStatus.Completed
                && x.ExpectedHash == hash
                && (x.Type == OpType.Copy || x.Type == OpType.Move || x.Type == OpType.Keep)
                && x.DestinationRootId != null
                && x.DestinationPath != null
            )
            .Select(x => new CopyCandidate(
                x.DestinationRootId!,
                x.DestinationPath!,
                x.ExpectedSize
            ))
            .ToList();
    }

    public List<CopyCandidate> ListContentCopies(
        string targetRootId,
        string excludeRootId,
        string excludePath,
        string hash
    )
    {
        var context = Context;
        return context
            .FileHashes.AsNoTracking()
            .Join(
                context.FileEntries.AsNoTracking(),
                fileHash => fileHash.FileEntryId,
                entry => entry.Id,
                (fileHash, entry) => new { fileHash = fileHash, entry = entry }
            )
            .Where(x =>
                x.fileHash.Digest == hash
                && x.fileHash.State == HashState.Ok
                && x.entry.Status == FileStatus.Ok
                && x.entry.EntryKind == EntryKind.File
                && x.entry.StorageRootId == targetRootId
                && !(
                    x.entry.StorageRootId == excludeRootId
                    && x.entry.RelativePath == excludePath
                )
            )
            .Select(x => new CopyCandidate(
                x.entry.StorageRootId,
                x.entry.RelativePath,
                x.entry.Size
            ))
            .ToList();
    }

    public sealed record PlanOperationSeed(
        int Sequence,
        string Type,
        string? SourceKind,
        string? SourceRoot,
        string? SourcePath,
        string? DestRoot,
        string? DestPath,
        long ExpectedSize,
        string? ExpectedHash,
        string? SkipReason = null
    );

    public void AddPlanWithOperations(
        string planId,
        string createdUtc,
        string sourceDatabasePath,
        string sourceRootId,
        string sourceRootPath,
        string targetRootId,
        string targetRootPath,
        string status,
        long estimatedBytesCopied,
        IEnumerable<PlanOperationSeed> operations
    )
    {
        var context = Context;
        EnsureWritable();
        using var transaction = context.Database.BeginTransaction();
        context.Plans.Add(
            new PlanEntity
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
            }
        );
        foreach (var op in operations)
        {
            context.PlanOperations.Add(
                new PlanOperationEntity
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
                    SkipReason = op.SkipReason,
                    Status = OpStatus.Planned,
                }
            );
        }
        context.SaveChanges();
        transaction.Commit();
        context.ChangeTracker.Clear();
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

        public void Commit()
        {
            _transaction.Commit();
            _completed = true;
        }

        public void Rollback()
        {
            _transaction.Rollback();
            _completed = true;
            _database.Context.ChangeTracker.Clear();
        }

        public void Dispose()
        {
            if (!_completed)
            {
                try
                {
                    _transaction.Rollback();
                }
                catch { }
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

    public List<string> AppliedMigrations()
    {
        var context = Context;
        return context.Database.GetAppliedMigrations().ToList();
    }

    public List<string> PendingMigrations()
    {
        var context = Context;
        return context.Database.GetPendingMigrations().ToList();
    }

    private void AddAndSave<TEntity>(DbSet<TEntity> set, TEntity entity)
        where TEntity : class
    {
        set.Add(entity);
        Context.SaveChanges();
        Context.Entry(entity).State = EntityState.Detached;
    }

    private void EnsureWritable()
    {
        if (_readOnly)
        {
            throw new InvalidOperationException("The database was opened read-only.");
        }
    }
}
