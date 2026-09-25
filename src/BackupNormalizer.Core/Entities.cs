namespace BackupNormalizer;

public sealed class StorageRootEntity
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool Writable { get; set; }
    public string FileSystemId { get; set; } = string.Empty;
    public string CaseSensitivity { get; set; } = string.Empty;
    public string CreatedUtc { get; set; } = string.Empty;
}

public sealed class ScanEntity
{
    public long Id { get; set; }
    public string StorageRootId { get; set; } = string.Empty;
    public string StartedUtc { get; set; } = string.Empty;
    public string? CompletedUtc { get; set; }
    public string Status { get; set; } = string.Empty;
}

public sealed class FileEntryEntity
{
    public long Id { get; set; }
    public string StorageRootId { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public long Size { get; set; }
    public string ModifiedUtc { get; set; } = string.Empty;
    public string? CreatedUtc { get; set; }
    public string? FileIdentity { get; set; }
    public long LastSeenScanId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Error { get; set; }
}

public sealed class FileHashEntity
{
    public long FileEntryId { get; set; }
    public string Algorithm { get; set; } = string.Empty;
    public string Digest { get; set; } = string.Empty;
    public long SizeAtHash { get; set; }
    public string ModifiedUtcAtHash { get; set; } = string.Empty;
    public string CalculatedUtc { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
}

public sealed class PlanEntity
{
    public string Id { get; set; } = string.Empty;
    public string CreatedUtc { get; set; } = string.Empty;
    public string SourceDatabasePath { get; set; } = string.Empty;
    public string SourceRootId { get; set; } = string.Empty;
    public string SourceRootPath { get; set; } = string.Empty;
    public string TargetRootId { get; set; } = string.Empty;
    public string TargetRootPath { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public long EstimatedBytesCopied { get; set; }
}

public sealed class PlanOperationEntity
{
    public long Id { get; set; }
    public string PlanId { get; set; } = string.Empty;
    public int Sequence { get; set; }
    public string Type { get; set; } = string.Empty;
    public string? SourceKind { get; set; }
    public string? SourceRootId { get; set; }
    public string? SourcePath { get; set; }
    public string? DestinationRootId { get; set; }
    public string? DestinationPath { get; set; }
    public long ExpectedSize { get; set; }
    public string? ExpectedHash { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? StartedUtc { get; set; }
    public string? CompletedUtc { get; set; }
    public string? Error { get; set; }
}

public sealed class ExecutionLogEntity
{
    public long Id { get; set; }
    public long PlanOperationId { get; set; }
    public string TimestampUtc { get; set; } = string.Empty;
    public string Level { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}
