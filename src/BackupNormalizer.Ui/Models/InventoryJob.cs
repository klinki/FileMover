using System;

namespace BackupNormalizer.Ui.Models;

public enum InventoryJobKind
{
    Scan,
    HashNeeded,
    ScanThenHash,
}

public enum InventoryJobOutcome
{
    Completed,
    Incomplete,
    Canceled,
    Failed,
}

public enum InventoryJobStage
{
    Validating,
    Scanning,
    Hashing,
}

public sealed record InventoryJobRequest(string DatabasePath, string RootId, string RecordedRootPath,
    InventoryJobKind Kind, bool FullScan = false, int Parallelism = 2);

public sealed record InventoryJobProgress(InventoryJobStage Stage, string CurrentPath, TimeSpan Elapsed,
    int Scanned = 0, int ScanErrors = 0, bool Incremental = false, int TotalFiles = 0,
    int Hashed = 0, int Skipped = 0, int Unstable = 0, long BytesRead = 0);

public sealed record InventoryJobResult(InventoryJobKind Kind, InventoryJobOutcome Outcome, string Message,
    TimeSpan Elapsed, int Scanned = 0, int ScanErrors = 0, bool Incremental = false,
    int Hashed = 0, int Skipped = 0, int Unstable = 0, long BytesRead = 0, int TotalFiles = 0)
{
    public string CountsText => Kind switch
    {
        InventoryJobKind.Scan => $"{Scanned:N0} entries scanned, {ScanErrors:N0} errors",
        InventoryJobKind.HashNeeded => HashCounts,
        _ => $"{Scanned:N0} entries scanned, {ScanErrors:N0} errors; {HashCounts}",
    };

    private string HashCounts => $"{Hashed + Skipped + Unstable:N0} / {TotalFiles:N0} entries processed, " +
        $"{Hashed:N0} hashed, {Skipped:N0} skipped, {Unstable:N0} unstable";
}
