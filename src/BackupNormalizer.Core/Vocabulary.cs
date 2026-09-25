namespace BackupNormalizer;

/// <summary>
/// Single source of truth for operation types, statuses and scopes persisted
/// in SQLite. Never inline these literals; tests assert on the values.
/// </summary>
public static class OpType
{
    public const string Mkdir = "MKDIR";
    public const string Keep = "KEEP";
    public const string Move = "MOVE";
    public const string Copy = "COPY";
    public const string Verify = "VERIFY";
    public const string Trash = "TRASH";
}

public static class OpStatus
{
    public const string Planned = "Planned";
    public const string Started = "Started";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string Skipped = "Skipped";
    public const string Conflict = "Conflict";
}

public static class PlanStatus
{
    public const string Planned = "Planned";
    public const string Completed = "Completed";
    public const string Partial = "Partial";
}

public static class FileStatus
{
    public const string Ok = "Ok";
    public const string Missing = "Missing";
    public const string UnsupportedEntry = "UnsupportedEntry";
    public const string ScanError = "ScanError";
}

public static class HashState
{
    public const string Ok = "Ok";
    public const string Stale = "Stale";
    public const string Unstable = "Unstable";
}

public static class ScanStatus
{
    public const string Started = "Started";
    public const string Completed = "Completed";
    public const string Incomplete = "Incomplete";
    public const string Failed = "Failed";
    public const string Invalidated = "Invalidated";
}

public static class SourceScope
{
    public const string Source = "Source";
    public const string Target = "Target";
}
