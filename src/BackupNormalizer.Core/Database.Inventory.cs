using Microsoft.EntityFrameworkCore;

namespace BackupNormalizer;

public sealed record ScanDiagnosticRow(long Id, long ScanId, string Path, string Message, string RecordedUtc);
public sealed record ScanDetailsRow(ScanRow Scan, string? Mode, int? ScannedCount, int? ErrorCount, string? FallbackReason);
public sealed record InventoryStatusRow(StorageRootRow Root, ScanDetailsRow? LatestScan, ScanRow? LastSuccessfulScan,
    int RegularFiles, int Links, int MissingEntries, int EntryErrors, int UsableHashes, int MissingHashes,
    bool PlanningReady, string? BlockingReason, ScanCheckpointRow? Checkpoint, IReadOnlyList<ScanDiagnosticRow> Errors);

public sealed partial class Database
{
    internal void SaveScanDiagnostics(long scanId, string mode, int scanned, string? fallback,
        IReadOnlyList<ScanError> errors)
    {
        EnsureWritable();
        using var transaction = Context.Database.BeginTransaction();
        Context.Scans.Where(s => s.Id == scanId).ExecuteUpdate(setters => setters
            .SetProperty(s => s.Mode, mode).SetProperty(s => s.ScannedCount, scanned)
            .SetProperty(s => s.ErrorCount, errors.Count).SetProperty(s => s.FallbackReason, fallback));
        Context.ScanDiagnostics.AddRange(errors.Select(error => new ScanDiagnosticEntity
        {
            ScanId = scanId, Path = error.Path, Message = error.Message, RecordedUtc = UtcNow()
        }));
        Context.SaveChanges();
        Context.ChangeTracker.Clear();
        transaction.Commit();
    }

    public ScanDetailsRow? GetScanDetails(string rootId, long? scanId = null)
    {
        var query = Context.Scans.AsNoTracking().Where(s => s.StorageRootId == rootId);
        if (scanId.HasValue) query = query.Where(s => s.Id == scanId.Value);
        query = query.OrderByDescending(s => s.Id);
        if (_readOnly && !HasColumn("Scan", "Mode"))
        {
            var scan = query.Select(s => new ScanRow(s.Id, s.StorageRootId, s.StartedUtc, s.CompletedUtc, s.Status)).FirstOrDefault();
            return scan == null ? null : new ScanDetailsRow(scan, null, null, null, null);
        }
        return query.Select(s => new ScanDetailsRow(
            new ScanRow(s.Id, s.StorageRootId, s.StartedUtc, s.CompletedUtc, s.Status),
            s.Mode, s.ScannedCount, s.ErrorCount, s.FallbackReason)).FirstOrDefault();
    }

    public List<ScanDiagnosticRow> ListScanDiagnostics(long scanId)
    {
        if (_readOnly && !HasColumn("ScanDiagnostic", "ScanId")) return [];
        return Context.ScanDiagnostics.AsNoTracking().Where(e => e.ScanId == scanId).OrderBy(e => e.Id)
            .Select(e => new ScanDiagnosticRow(e.Id, e.ScanId, e.Path, e.Message, e.RecordedUtc)).ToList();
    }

    public InventoryStatusRow GetInventoryStatus(string rootId, string algorithm = "sha256")
    {
        var root = GetRoot(rootId) ?? throw new InvalidOperationException($"unknown root '{rootId}'");
        var latest = GetScanDetails(rootId);
        var successful = Context.Scans.AsNoTracking().Where(s => s.StorageRootId == rootId && s.Status == ScanStatus.Completed)
            .OrderByDescending(s => s.Id).Select(s => new ScanRow(s.Id, s.StorageRootId, s.StartedUtc, s.CompletedUtc, s.Status))
            .FirstOrDefault();
        algorithm = HasherFactory.NormalizeAlgorithm(algorithm);
        var entries = Context.FileEntries.AsNoTracking().Where(e => e.StorageRootId == rootId);
        var regularEntries = entries.Where(e => e.Status == FileStatus.Ok);
        if (_hasLinkMetadata) regularEntries = regularEntries.Where(e => e.EntryKind == EntryKind.File);
        int regular = regularEntries.Count();
        int hashes = regularEntries.Count(e => Context.FileHashes.Any(h => h.FileEntryId == e.Id
            && h.Algorithm == algorithm && h.State == HashState.Ok && h.SizeAtHash == e.Size && h.ModifiedUtcAtHash == e.ModifiedUtc));
        int links = _hasLinkMetadata ? entries.Count(e => e.Status == FileStatus.Ok && e.EntryKind != EntryKind.File)
            : entries.Count(e => e.Status == FileStatus.UnsupportedEntry && e.Error == "symlink");
        var errors = entries.Where(e => e.Status != FileStatus.Ok && e.Status != FileStatus.Missing);
        if (!_hasLinkMetadata) errors = errors.Where(e => e.Status != FileStatus.UnsupportedEntry || e.Error != "symlink");
        int entryErrors = errors.Count();
        bool ready = latest?.Scan.Status == ScanStatus.Completed && hashes == regular && entryErrors == 0;
        string? reason = latest?.Scan.Status != ScanStatus.Completed ? "A complete successful scan is required."
            : entryErrors != 0 ? $"{entryErrors} inventory entries have errors."
            : hashes != regular ? $"{regular - hashes} regular files need usable {algorithm} hashes."
            : null;
        return new InventoryStatusRow(root, latest, successful, regular, links,
            entries.Count(e => e.Status == FileStatus.Missing), entryErrors, hashes, regular - hashes,
            ready, reason, GetScanCheckpoint(rootId), latest == null ? [] : ListScanDiagnostics(latest.Scan.Id));
    }
}
