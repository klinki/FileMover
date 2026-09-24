using Microsoft.Data.Sqlite;

namespace BackupNormalizer;

public sealed record StorageRootRow(string Id, string Name, string Path, string Role, bool Writable, string FileSystemId, string CaseSensitivity, string CreatedUtc);
public sealed record ScanRow(long Id, string StorageRootId, string StartedUtc, string? CompletedUtc, string Status);
public sealed record FileEntryRow(long Id, string StorageRootId, string RelativePath, string Name, long Size, string ModifiedUtc, string? CreatedUtc, string? FileIdentity, long LastSeenScanId, string Status, string? Error);
public sealed record FileHashRow(long FileEntryId, string Algorithm, string Digest, long SizeAtHash, string ModifiedUtcAtHash, string CalculatedUtc, string State);

public sealed class Database : IDisposable
{
    private readonly SqliteConnection _conn;
    public string DbPath { get; }

    static Database()
    {
        try { SQLitePCL.Batteries.Init(); } catch { }
    }

    public Database(string dbPath)
    {
        DbPath = Path.GetFullPath(dbPath);
        var dir = Path.GetDirectoryName(DbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _conn = new SqliteConnection($"Data Source={DbPath}");
        _conn.Open();
        try
        {
            using var p = _conn.CreateCommand();
            p.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;";
            p.ExecuteNonQuery();
        }
        catch { }
        Migrate();
    }

    public SqliteConnection Conn => _conn;

    public void Dispose() => _conn.Dispose();

    public static string UtcNow() => DateTime.UtcNow.ToString("o");

    private void Migrate()
    {
        var sql = """
            CREATE TABLE IF NOT EXISTS SchemaVersion(Version INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS StorageRoot(
              Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Path TEXT NOT NULL, Role TEXT NOT NULL DEFAULT 'Unknown',
              Writable INTEGER NOT NULL DEFAULT 1, FileSystemId TEXT NOT NULL DEFAULT 'unknown',
              CaseSensitivity TEXT NOT NULL DEFAULT 'unknown', CreatedUtc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Scan(
              Id INTEGER PRIMARY KEY AUTOINCREMENT, StorageRootId TEXT NOT NULL, StartedUtc TEXT NOT NULL,
              CompletedUtc TEXT, Status TEXT NOT NULL DEFAULT 'Started',
              FOREIGN KEY(StorageRootId) REFERENCES StorageRoot(Id));
            CREATE TABLE IF NOT EXISTS FileEntry(
              Id INTEGER PRIMARY KEY AUTOINCREMENT, StorageRootId TEXT NOT NULL, RelativePath TEXT NOT NULL,
              Name TEXT NOT NULL, Size INTEGER NOT NULL, ModifiedUtc TEXT NOT NULL, CreatedUtc TEXT,
              FileIdentity TEXT, LastSeenScanId INTEGER NOT NULL, Status TEXT NOT NULL DEFAULT 'Ok', Error TEXT,
              UNIQUE(StorageRootId, RelativePath),
              FOREIGN KEY(StorageRootId) REFERENCES StorageRoot(Id));
            CREATE INDEX IF NOT EXISTS IX_FileEntry_Root ON FileEntry(StorageRootId);
            CREATE INDEX IF NOT EXISTS IX_FileEntry_Size ON FileEntry(Size);
            CREATE TABLE IF NOT EXISTS FileHash(
              FileEntryId INTEGER NOT NULL, Algorithm TEXT NOT NULL, Digest TEXT NOT NULL,
              SizeAtHash INTEGER NOT NULL, ModifiedUtcAtHash TEXT NOT NULL, CalculatedUtc TEXT NOT NULL,
              State TEXT NOT NULL DEFAULT 'Ok',
              PRIMARY KEY(FileEntryId, Algorithm),
              FOREIGN KEY(FileEntryId) REFERENCES FileEntry(Id) ON DELETE CASCADE);
            CREATE INDEX IF NOT EXISTS IX_FileHash_Digest ON FileHash(Algorithm, Digest);
            CREATE TABLE IF NOT EXISTS CanonicalEntry(
              Id INTEGER PRIMARY KEY AUTOINCREMENT, RelativePath TEXT NOT NULL,
              Size INTEGER NOT NULL, ExpectedHash TEXT, SourceFileEntryId INTEGER);
            CREATE TABLE IF NOT EXISTS Plan(
              Id TEXT PRIMARY KEY, CreatedUtc TEXT NOT NULL, CanonicalRootId TEXT NOT NULL,
              Status TEXT NOT NULL DEFAULT 'Planned', EstimatedBytesCopied INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS PlanOperation(
              Id INTEGER PRIMARY KEY AUTOINCREMENT, PlanId TEXT NOT NULL, Sequence INTEGER NOT NULL,
              Type TEXT NOT NULL, SourceRootId TEXT, SourcePath TEXT,
              DestinationRootId TEXT, DestinationPath TEXT,
              ExpectedSize INTEGER NOT NULL DEFAULT 0, ExpectedHash TEXT,
              Status TEXT NOT NULL DEFAULT 'Planned', StartedUtc TEXT, CompletedUtc TEXT, Error TEXT,
              FOREIGN KEY(PlanId) REFERENCES Plan(Id) ON DELETE CASCADE);
            CREATE INDEX IF NOT EXISTS IX_PlanOp_Plan ON PlanOperation(PlanId, Sequence);
            CREATE TABLE IF NOT EXISTS ExecutionLog(
              Id INTEGER PRIMARY KEY AUTOINCREMENT, PlanOperationId INTEGER NOT NULL,
              TimestampUtc TEXT NOT NULL, Level TEXT NOT NULL, Message TEXT NOT NULL);
            """;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
        using var v = _conn.CreateCommand();
        v.CommandText = "INSERT INTO SchemaVersion(Version) SELECT 1 WHERE NOT EXISTS(SELECT 1 FROM SchemaVersion)";
        v.ExecuteNonQuery();
    }

    // ---- Roots ----
    public void UpsertRoot(StorageRootRow r)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = """
            INSERT INTO StorageRoot(Id,Name,Path,Role,Writable,FileSystemId,CaseSensitivity,CreatedUtc)
            VALUES($id,$name,$path,$role,$w,$fs,$cs,$cu)
            ON CONFLICT(Id) DO UPDATE SET Name=excluded.Name, Path=excluded.Path, Role=excluded.Role,
              Writable=excluded.Writable, FileSystemId=excluded.FileSystemId, CaseSensitivity=excluded.CaseSensitivity;
            """;
        c.Parameters.AddWithValue("$id", r.Id);
        c.Parameters.AddWithValue("$name", r.Name);
        c.Parameters.AddWithValue("$path", r.Path);
        c.Parameters.AddWithValue("$role", r.Role);
        c.Parameters.AddWithValue("$w", r.Writable ? 1 : 0);
        c.Parameters.AddWithValue("$fs", r.FileSystemId);
        c.Parameters.AddWithValue("$cs", r.CaseSensitivity);
        c.Parameters.AddWithValue("$cu", r.CreatedUtc);
        c.ExecuteNonQuery();
    }

    public List<StorageRootRow> ListRoots()
    {
        var out_ = new List<StorageRootRow>();
        using var c = _conn.CreateCommand();
        c.CommandText = "SELECT Id,Name,Path,Role,Writable,FileSystemId,CaseSensitivity,CreatedUtc FROM StorageRoot ORDER BY Id";
        using var r = c.ExecuteReader();
        while (r.Read())
            out_.Add(new StorageRootRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4) != 0, r.GetString(5), r.GetString(6), r.GetString(7)));
        return out_;
    }

    public StorageRootRow? GetRoot(string id)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = "SELECT Id,Name,Path,Role,Writable,FileSystemId,CaseSensitivity,CreatedUtc FROM StorageRoot WHERE Id=$id";
        c.Parameters.AddWithValue("$id", id);
        using var r = c.ExecuteReader();
        if (!r.Read()) return null;
        return new StorageRootRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4) != 0, r.GetString(5), r.GetString(6), r.GetString(7));
    }

    public long BeginScan(string rootId)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = "INSERT INTO Scan(StorageRootId,StartedUtc,Status) VALUES($r,$t,'Started'); SELECT last_insert_rowid();";
        c.Parameters.AddWithValue("$r", rootId);
        c.Parameters.AddWithValue("$t", UtcNow());
        return (long)(c.ExecuteScalar() ?? 0L);
    }

    public void FinishScan(long scanId, string status)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = "UPDATE Scan SET CompletedUtc=$t, Status=$s WHERE Id=$id";
        c.Parameters.AddWithValue("$t", UtcNow());
        c.Parameters.AddWithValue("$s", status);
        c.Parameters.AddWithValue("$id", scanId);
        c.ExecuteNonQuery();
    }

    public FileEntryRow? GetFileEntry(string rootId, string rel)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = "SELECT Id,StorageRootId,RelativePath,Name,Size,ModifiedUtc,CreatedUtc,FileIdentity,LastSeenScanId,Status,Error FROM FileEntry WHERE StorageRootId=$r AND RelativePath=$p";
        c.Parameters.AddWithValue("$r", rootId);
        c.Parameters.AddWithValue("$p", rel);
        using var r = c.ExecuteReader();
        if (!r.Read()) return null;
        return ReadFileEntry(r);
    }

    public static FileEntryRow ReadFileEntry(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4), r.GetString(5),
        r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7),
        r.GetInt64(8), r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10));

    public long UpsertFileEntry(FileEntryRow e)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = """
            INSERT INTO FileEntry(StorageRootId,RelativePath,Name,Size,ModifiedUtc,CreatedUtc,FileIdentity,LastSeenScanId,Status,Error)
            VALUES($r,$p,$n,$s,$m,$cr,$fi,$scan,$st,$err)
            ON CONFLICT(StorageRootId,RelativePath) DO UPDATE SET
              Name=excluded.Name, Size=excluded.Size, ModifiedUtc=excluded.ModifiedUtc, CreatedUtc=excluded.CreatedUtc,
              FileIdentity=excluded.FileIdentity, LastSeenScanId=excluded.LastSeenScanId, Status=excluded.Status, Error=excluded.Error;
            """;
        c.Parameters.AddWithValue("$r", e.StorageRootId);
        c.Parameters.AddWithValue("$p", e.RelativePath);
        c.Parameters.AddWithValue("$n", e.Name);
        c.Parameters.AddWithValue("$s", e.Size);
        c.Parameters.AddWithValue("$m", e.ModifiedUtc);
        c.Parameters.AddWithValue("$cr", (object?)e.CreatedUtc ?? DBNull.Value);
        c.Parameters.AddWithValue("$fi", (object?)e.FileIdentity ?? DBNull.Value);
        c.Parameters.AddWithValue("$scan", e.LastSeenScanId);
        c.Parameters.AddWithValue("$st", e.Status);
        c.Parameters.AddWithValue("$err", (object?)e.Error ?? DBNull.Value);
        c.ExecuteNonQuery();
        var existing = GetFileEntry(e.StorageRootId, e.RelativePath);
        return existing?.Id ?? 0;
    }

    public List<FileEntryRow> ListFiles(string? rootId = null)
    {
        var out_ = new List<FileEntryRow>();
        using var c = _conn.CreateCommand();
        c.CommandText = rootId == null
            ? "SELECT Id,StorageRootId,RelativePath,Name,Size,ModifiedUtc,CreatedUtc,FileIdentity,LastSeenScanId,Status,Error FROM FileEntry ORDER BY StorageRootId,RelativePath"
            : "SELECT Id,StorageRootId,RelativePath,Name,Size,ModifiedUtc,CreatedUtc,FileIdentity,LastSeenScanId,Status,Error FROM FileEntry WHERE StorageRootId=$r ORDER BY RelativePath";
        if (rootId != null) c.Parameters.AddWithValue("$r", rootId);
        using var r = c.ExecuteReader();
        while (r.Read()) out_.Add(ReadFileEntry(r));
        return out_;
    }

    public FileHashRow? GetHash(long fileEntryId, string algo)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = "SELECT FileEntryId,Algorithm,Digest,SizeAtHash,ModifiedUtcAtHash,CalculatedUtc,State FROM FileHash WHERE FileEntryId=$f AND Algorithm=$a";
        c.Parameters.AddWithValue("$f", fileEntryId);
        c.Parameters.AddWithValue("$a", algo);
        using var r = c.ExecuteReader();
        if (!r.Read()) return null;
        return new FileHashRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetString(4), r.GetString(5), r.GetString(6));
    }

    public void UpsertHash(FileHashRow h)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = """
            INSERT INTO FileHash(FileEntryId,Algorithm,Digest,SizeAtHash,ModifiedUtcAtHash,CalculatedUtc,State)
            VALUES($f,$a,$d,$s,$m,$c,$st)
            ON CONFLICT(FileEntryId,Algorithm) DO UPDATE SET Digest=excluded.Digest, SizeAtHash=excluded.SizeAtHash,
              ModifiedUtcAtHash=excluded.ModifiedUtcAtHash, CalculatedUtc=excluded.CalculatedUtc, State=excluded.State;
            """;
        c.Parameters.AddWithValue("$f", h.FileEntryId);
        c.Parameters.AddWithValue("$a", h.Algorithm);
        c.Parameters.AddWithValue("$d", h.Digest);
        c.Parameters.AddWithValue("$s", h.SizeAtHash);
        c.Parameters.AddWithValue("$m", h.ModifiedUtcAtHash);
        c.Parameters.AddWithValue("$c", h.CalculatedUtc);
        c.Parameters.AddWithValue("$st", h.State);
        c.ExecuteNonQuery();
    }

    public void MarkHashStale(long fileEntryId, string algo)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = "UPDATE FileHash SET State='Stale' WHERE FileEntryId=$f AND Algorithm=$a";
        c.Parameters.AddWithValue("$f", fileEntryId);
        c.Parameters.AddWithValue("$a", algo);
        c.ExecuteNonQuery();
    }

    // ---- Plans ----
    public void InsertPlan(string planId, string canonicalRootId, long estBytes, string status = "Planned")
    {
        using var c = _conn.CreateCommand();
        c.CommandText = "INSERT INTO Plan(Id,CreatedUtc,CanonicalRootId,Status,EstimatedBytesCopied) VALUES($id,$t,$c,$s,$e)";
        c.Parameters.AddWithValue("$id", planId);
        c.Parameters.AddWithValue("$t", UtcNow());
        c.Parameters.AddWithValue("$c", canonicalRootId);
        c.Parameters.AddWithValue("$s", status);
        c.Parameters.AddWithValue("$e", estBytes);
        c.ExecuteNonQuery();
    }

    public void InsertOperation(string planId, int seq, string type, string? srcRoot, string? srcPath, string? dstRoot, string? dstPath, long size, string? hash, string status = "Planned")
    {
        using var c = _conn.CreateCommand();
        c.CommandText = """
            INSERT INTO PlanOperation(PlanId,Sequence,Type,SourceRootId,SourcePath,DestinationRootId,DestinationPath,ExpectedSize,ExpectedHash,Status)
            VALUES($p,$s,$t,$sr,$sp,$dr,$dp,$sz,$h,$st)
            """;
        c.Parameters.AddWithValue("$p", planId);
        c.Parameters.AddWithValue("$s", seq);
        c.Parameters.AddWithValue("$t", type);
        c.Parameters.AddWithValue("$sr", (object?)srcRoot ?? DBNull.Value);
        c.Parameters.AddWithValue("$sp", (object?)srcPath ?? DBNull.Value);
        c.Parameters.AddWithValue("$dr", (object?)dstRoot ?? DBNull.Value);
        c.Parameters.AddWithValue("$dp", (object?)dstPath ?? DBNull.Value);
        c.Parameters.AddWithValue("$sz", size);
        c.Parameters.AddWithValue("$h", (object?)hash ?? DBNull.Value);
        c.Parameters.AddWithValue("$st", status);
        c.ExecuteNonQuery();
    }

    public bool PlanExists(string planId)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = "SELECT 1 FROM Plan WHERE Id=$id";
        c.Parameters.AddWithValue("$id", planId);
        return c.ExecuteScalar() != null;
    }
}
