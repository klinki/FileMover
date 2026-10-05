using Microsoft.EntityFrameworkCore;

namespace BackupNormalizer;

public sealed record ScanDiagnosticRow(
    long Id,
    long ScanId,
    string Path,
    string Message,
    string RecordedUtc
);

public sealed record ScanDetailsRow(
    ScanRow Scan,
    string? Mode,
    int? ScannedCount,
    int? ErrorCount,
    string? FallbackReason
);

public sealed record InventoryStatusRow(
    StorageRootRow Root,
    ScanDetailsRow? LatestScan,
    ScanRow? LastSuccessfulScan,
    int RegularFiles,
    int Links,
    int MissingEntries,
    int EntryErrors,
    int UsableHashes,
    int MissingHashes,
    bool PlanningReady,
    string? BlockingReason,
    ScanCheckpointRow? Checkpoint,
    IReadOnlyList<ScanDiagnosticRow> Errors,
    IReadOnlyList<string>? ExcludedPathRegexes = null
);

public sealed partial class Database
{
    internal void SaveScanDiagnostics(
        long scanId,
        string mode,
        int scanned,
        string? fallback,
        IReadOnlyList<ScanError> errors
    )
    {
        var queryScanId = scanId;
        var queryMode = mode;
        var queryScanned = scanned;
        var queryFallback = fallback;
        var queryErrors = errors;
        int? errorCount = errors.Count;
        var context = Context;
        EnsureWritable();
        using var transaction = context.Database.BeginTransaction();
        context
            .Scans.Where(s => s.Id == queryScanId)
            .ExecuteUpdate(setters =>
                setters
                    .SetProperty(s => s.Mode, s => queryMode)
                    .SetProperty(s => s.ScannedCount, s => (int?)queryScanned)
                    .SetProperty(s => s.ErrorCount, s => errorCount)
                    .SetProperty(s => s.FallbackReason, s => queryFallback)
            );
        context.ScanDiagnostics.AddRange(
            queryErrors.Select(error => new ScanDiagnosticEntity
            {
                ScanId = queryScanId,
                Path = error.Path,
                Message = error.Message,
                RecordedUtc = UtcNow(),
            })
        );
        context.SaveChanges();
        context.ChangeTracker.Clear();
        transaction.Commit();
    }

    public ScanDetailsRow? GetScanDetails(string rootId, long? scanId = null)
    {
        var queryRootId = rootId;
        bool filterByScanId = scanId.HasValue;
        long selectedScanId = scanId.GetValueOrDefault();
        var context = Context;
        return filterByScanId
            ? context
                .Scans.AsNoTracking()
                .Where(s => s.StorageRootId == queryRootId && s.Id == selectedScanId)
                .OrderByDescending(s => s.Id)
                .Select(s => new ScanDetailsRow(
                    new ScanRow(s.Id, s.StorageRootId, s.StartedUtc, s.CompletedUtc, s.Status),
                    s.Mode,
                    s.ScannedCount,
                    s.ErrorCount,
                    s.FallbackReason
                ))
                .FirstOrDefault()
            : context
                .Scans.AsNoTracking()
                .Where(s => s.StorageRootId == queryRootId)
                .OrderByDescending(s => s.Id)
                .Select(s => new ScanDetailsRow(
                    new ScanRow(s.Id, s.StorageRootId, s.StartedUtc, s.CompletedUtc, s.Status),
                    s.Mode,
                    s.ScannedCount,
                    s.ErrorCount,
                    s.FallbackReason
                ))
                .FirstOrDefault();
    }

    public List<ScanDiagnosticRow> ListScanDiagnostics(long scanId)
    {
        var queryScanId = scanId;
        var context = Context;

        return context
            .ScanDiagnostics.AsNoTracking()
            .Where(e => e.ScanId == queryScanId)
            .OrderBy(e => e.Id)
            .Select(e => new ScanDiagnosticRow(e.Id, e.ScanId, e.Path, e.Message, e.RecordedUtc))
            .ToList();
    }

    public InventoryStatusRow GetInventoryStatus(string rootId, string algorithm = "sha256")
    {
        var queryRootId = rootId;
        var queryAlgorithm = algorithm;
        var context = Context;
        var root =
            GetRoot(queryRootId)
            ?? throw new InvalidOperationException($"unknown root '{queryRootId}'");
        var latest = GetScanDetails(queryRootId);
        var successful = context
            .Scans.AsNoTracking()
            .Where(s => s.StorageRootId == queryRootId && s.Status == ScanStatus.Completed)
            .OrderByDescending(s => s.Id)
            .Select(s => new ScanRow(s.Id, s.StorageRootId, s.StartedUtc, s.CompletedUtc, s.Status))
            .FirstOrDefault();
        queryAlgorithm = HasherFactory.NormalizeAlgorithm(queryAlgorithm);
        int regular = context.FileEntries.Count(e =>
            e.StorageRootId == queryRootId
            && e.Status == FileStatus.Ok
            && e.EntryKind == EntryKind.File
        );
        int hashes = context.FileEntries.Count(e =>
            e.StorageRootId == queryRootId
            && e.Status == FileStatus.Ok
            && e.EntryKind == EntryKind.File
            && context.FileHashes.Any(h =>
                h.FileEntryId == e.Id
                && h.Algorithm == queryAlgorithm
                && h.State == HashState.Ok
                && h.SizeAtHash == e.Size
                && h.ModifiedUtcAtHash == e.ModifiedUtc
            )
        );
        int links = context.FileEntries.Count(e =>
            e.StorageRootId == queryRootId
            && e.Status == FileStatus.Ok
            && e.EntryKind != EntryKind.File
        );
        int entryErrors = context.FileEntries.Count(e =>
            e.StorageRootId == queryRootId
            && e.Status != FileStatus.Ok
            && e.Status != FileStatus.Missing
        );
        bool ready =
            latest?.Scan.Status == ScanStatus.Completed && hashes == regular && entryErrors == 0;
        string? reason =
            latest?.Scan.Status != ScanStatus.Completed ? "A complete successful scan is required."
            : entryErrors != 0 ? $"{entryErrors} inventory entries have errors."
            : hashes != regular
                ? $"{regular - hashes} regular files need usable {queryAlgorithm} hashes."
            : null;
        return new InventoryStatusRow(
            root,
            latest,
            successful,
            regular,
            links,
            context.FileEntries.Count(e =>
                e.StorageRootId == queryRootId && e.Status == FileStatus.Missing
            ),
            entryErrors,
            hashes,
            regular - hashes,
            ready,
            reason,
            GetScanCheckpoint(queryRootId),
            latest == null ? [] : ListScanDiagnostics(latest.Scan.Id),
            GetExcludedPathRegexes(queryRootId)
        );
    }
}
