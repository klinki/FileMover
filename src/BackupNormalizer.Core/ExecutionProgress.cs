namespace BackupNormalizer;

public sealed record ExecutionProgress(
    int Position,
    int Total,
    string Operation,
    string Path,
    string Status,
    int Completed,
    int Failed,
    int Skipped,
    int Conflicts,
    long BytesCopied,
    long BytesRead,
    TimeSpan Elapsed,
    string? Message = null
);
