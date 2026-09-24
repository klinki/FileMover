using Microsoft.Data.Sqlite;

namespace BackupNormalizer;

/// <summary>Executor §20-24: pre-validation, crash-safe copy, journaling, resume, conflicts.</summary>
public sealed class Executor
{
    private readonly Database _db;
    private readonly string _algo;
    private readonly string _trashName;
    private readonly IContentHasher _hasher;

    public Executor(Database db, string? algo = null, string trashName = ".backup-normalizer-trash")
    {
        _db = db;
        _algo = HasherFactory.NormalizeAlgorithm(algo ?? "sha256");
        _trashName = trashName;
        _hasher = HasherFactory.Create(_algo);
    }

    public sealed record ExecSummary(int Completed, int Failed, int Skipped, int Conflicts);

    public ExecSummary Execute(string planId, Dictionary<string, string>? mapRoot = null, bool resume = false, bool stopOnError = false)
    {
        var roots = _db.ListRoots().ToDictionary(r => r.Id);
        string ResolvePath(string rootId, string rel)
        {
            string basePath;
            if (mapRoot != null && mapRoot.TryGetValue(rootId, out var ov)) basePath = ov;
            else if (roots.TryGetValue(rootId, out var r)) basePath = r.Path;
            else throw new InvalidOperationException($"unknown root '{rootId}' (hint: use --map-root {rootId}=<path>)");
            return Paths.CombineRoot(basePath, rel);
        }
        string TrashDirFor(string rootId)
        {
            string basePath;
            if (mapRoot != null && mapRoot.TryGetValue(rootId, out var ov)) basePath = ov;
            else basePath = roots[rootId].Path;
            return Path.Combine(basePath, _trashName, planId);
        }

        var ops = LoadOps(planId);
        int done = 0, failed = 0, skipped = 0, conflicts = 0;
        foreach (var op in ops)
        {
            if (resume && op.Status == "Completed") { done++; continue; }
            if (op.Status == "Completed" && !resume) { done++; continue; }
            if (op.Type is "KEEP")
            {
                Mark(op.Id, "Completed");
                Journal(op.Id, "INFO", $"KEEP {op.DestRoot}:{op.DestPath}");
                done++;
                continue;
            }
            if (op.Type is "MKDIR")
            {
                try
                {
                    SetStarted(op.Id);
                    var dir = ResolvePath(op.DestRoot!, op.DestPath!);
                    Directory.CreateDirectory(dir);
                    Mark(op.Id, "Completed");
                    Journal(op.Id, "INFO", $"MKDIR {op.DestRoot}:{op.DestPath}");
                    done++;
                }
                catch (Exception ex) { Mark(op.Id, "Failed", ex.Message); Journal(op.Id, "ERROR", ex.Message); failed++; if (stopOnError) break; }
                continue;
            }
            try
            {
                SetStarted(op.Id);
                switch (op.Type)
                {
                    case "MOVE": DoMove(op, ResolvePath); break;
                    case "COPY": DoCopy(op, ResolvePath); break;
                    case "TRASH": DoTrash(op, ResolvePath, TrashDirFor); break;
                    case "VERIFY": DoVerify(op, ResolvePath); break;
                    default: throw new InvalidOperationException($"unknown op {op.Type}");
                }
                Mark(op.Id, "Completed");
                Journal(op.Id, "INFO", $"{op.Type} ok {op.SourceRoot}:{op.SourcePath} -> {op.DestRoot}:{op.DestPath} size={op.ExpectedSize} hash={op.ExpectedHash}");
                done++;
            }
            catch (ConflictException ex)
            {
                Mark(op.Id, "Conflict", ex.Message);
                Journal(op.Id, "WARN", $"CONFLICT {ex.Message}");
                conflicts++;
                if (stopOnError) break;
            }
            catch (Exception ex)
            {
                Mark(op.Id, "Failed", ex.Message);
                Journal(op.Id, "ERROR", $"{op.Type} failed: {ex.Message}");
                failed++;
                if (stopOnError) break;
            }
        }
        // Update plan status
        using var c = _db.Conn.CreateCommand();
        c.CommandText = "UPDATE Plan SET Status=$s WHERE Id=$id";
        c.Parameters.AddWithValue("$s", failed == 0 && conflicts == 0 ? "Completed" : "Partial");
        c.Parameters.AddWithValue("$id", planId);
        c.ExecuteNonQuery();
        return new ExecSummary(done, failed, skipped, conflicts);
    }

    private sealed class ConflictException : Exception { public ConflictException(string m) : base(m) { } }
    private sealed record OpRow(long Id, string Type, string? SourceRoot, string? SourcePath, string? DestRoot, string? DestPath, long ExpectedSize, string? ExpectedHash, string Status);

    private List<OpRow> LoadOps(string planId)
    {
        var out_ = new List<OpRow>();
        using var c = _db.Conn.CreateCommand();
        c.CommandText = "SELECT Id,Type,SourceRootId,SourcePath,DestinationRootId,DestinationPath,ExpectedSize,ExpectedHash,Status FROM PlanOperation WHERE PlanId=$p ORDER BY Sequence";
        c.Parameters.AddWithValue("$p", planId);
        using var r = c.ExecuteReader();
        while (r.Read())
            out_.Add(new OpRow(r.GetInt64(0), r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
                r.GetInt64(6), r.IsDBNull(7) ? null : r.GetString(7), r.GetString(8)));
        if (out_.Count == 0) throw new InvalidOperationException($"unknown or empty plan '{planId}'");
        return out_;
    }

    private void SetStarted(long opId)
    {
        using var c = _db.Conn.CreateCommand();
        c.CommandText = "UPDATE PlanOperation SET Status='Started', StartedUtc=$t WHERE Id=$id";
        c.Parameters.AddWithValue("$t", Database.UtcNow());
        c.Parameters.AddWithValue("$id", opId);
        c.ExecuteNonQuery();
    }

    private void Mark(long opId, string status, string? err = null)
    {
        using var c = _db.Conn.CreateCommand();
        c.CommandText = "UPDATE PlanOperation SET Status=$s, CompletedUtc=$t, Error=$e WHERE Id=$id";
        c.Parameters.AddWithValue("$s", status);
        c.Parameters.AddWithValue("$t", Database.UtcNow());
        c.Parameters.AddWithValue("$e", (object?)err ?? DBNull.Value);
        c.Parameters.AddWithValue("$id", opId);
        c.ExecuteNonQuery();
    }

    private void Journal(long opId, string level, string msg)
    {
        using var c = _db.Conn.CreateCommand();
        c.CommandText = "INSERT INTO ExecutionLog(PlanOperationId,TimestampUtc,Level,Message) VALUES($o,$t,$l,$m)";
        c.Parameters.AddWithValue("$o", opId);
        c.Parameters.AddWithValue("$t", Database.UtcNow());
        c.Parameters.AddWithValue("$l", level);
        c.Parameters.AddWithValue("$m", msg);
        c.ExecuteNonQuery();
        Log.Info(msg, new { opId, level });
    }

    private void DoVerify(OpRow op, Func<string, string, string> resolve)
    {
        if (op.DestRoot == null || op.DestPath == null) throw new ConflictException("VERIFY missing destination");
        var dst = resolve(op.DestRoot, op.DestPath);
        if (!File.Exists(dst)) throw new ConflictException($"destination missing: {dst}");
        var fi = new FileInfo(dst);
        if (op.ExpectedSize != 0 && fi.Length != op.ExpectedSize)
            throw new ConflictException($"destination size mismatch at {dst}: expected {op.ExpectedSize}, got {fi.Length}");
        if (op.ExpectedHash != null)
        {
            string actual = _hasher.HashFile(dst, fi.Length);
            if (!string.Equals(actual, op.ExpectedHash, StringComparison.OrdinalIgnoreCase))
                throw new ConflictException($"destination hash mismatch at {dst}");
        }
    }

    private void DoMove(OpRow op, Func<string, string, string> resolve)
    {
        var src = resolve(op.SourceRoot!, op.SourcePath!);
        var dst = resolve(op.DestRoot!, op.DestPath!);
        if (!File.Exists(src)) throw new ConflictException($"source disappeared: {src}");
        var sfi = new FileInfo(src);
        if (op.ExpectedSize != 0 && sfi.Length != op.ExpectedSize)
            throw new ConflictException($"source changed size: {src}");
        if (File.Exists(dst))
        {
            var dfi = new FileInfo(dst);
            if (dfi.Length == sfi.Length)
            {
                string dh = _hasher.HashFile(dst, dfi.Length);
                if (op.ExpectedHash != null && string.Equals(dh, op.ExpectedHash, StringComparison.OrdinalIgnoreCase))
                    return; // identical -> treat as KEEP (§20)
            }
            throw new ConflictException($"destination exists with different content: {dst}");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        // Optional hash check on source before destructive move
        if (op.ExpectedHash != null)
        {
            string sh = _hasher.HashFile(src, sfi.Length);
            if (!string.Equals(sh, op.ExpectedHash, StringComparison.OrdinalIgnoreCase))
                throw new ConflictException($"source content changed (hash mismatch): {src}");
        }
        File.Move(src, dst);
        // §23 verification
        if (!File.Exists(dst) || File.Exists(src))
            throw new IOException($"move verification failed: {src} -> {dst}");
        if (op.ExpectedHash != null)
        {
            var dfi = new FileInfo(dst);
            string dh = _hasher.HashFile(dst, dfi.Length);
            if (!string.Equals(dh, op.ExpectedHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"moved file hash mismatch: {dst}");
        }
    }

    private void DoCopy(OpRow op, Func<string, string, string> resolve)
    {
        // Source may live in canonical DB snapshot (offline) -> fail safely as conflict, not data loss
        string src;
        try { src = resolve(op.SourceRoot!, op.SourcePath!); }
        catch { throw new ConflictException($"copy source root '{op.SourceRoot}' not mapped (use --map-root). Need {op.SourceRoot}:{op.SourcePath}"); }
        var dst = resolve(op.DestRoot!, op.DestPath!);
        if (!File.Exists(src)) throw new ConflictException($"copy source missing: {src} (stale plan fails safely, §32.8)");
        var sfi = new FileInfo(src);
        if (op.ExpectedSize != 0 && sfi.Length != op.ExpectedSize)
            throw new ConflictException($"copy source changed: {src}");
        if (File.Exists(dst))
        {
            var dfi = new FileInfo(dst);
            if (dfi.Length == op.ExpectedSize && op.ExpectedHash != null)
            {
                string dh = _hasher.HashFile(dst, dfi.Length);
                if (string.Equals(dh, op.ExpectedHash, StringComparison.OrdinalIgnoreCase)) return; // -> KEEP
            }
            throw new ConflictException($"destination exists with different content: {dst} (refusing overwrite, Invariant 6)");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        // Crash-safe: copy -> tmp -> flush -> verify -> rename (§21)
        string tmp = Path.Combine(Path.GetDirectoryName(dst)!,
            $".{Path.GetFileName(dst)}.backup-normalizer.{op.Id}.tmp");
        const int Buf = 4 * 1024 * 1024;
        using (var ins = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, Buf, FileOptions.SequentialScan))
        using (var outs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, Buf, FileOptions.SequentialScan))
        {
            ins.CopyTo(outs, Buf);
            outs.Flush(true);
        }
        var tfi = new FileInfo(tmp);
        if (op.ExpectedSize != 0 && tfi.Length != op.ExpectedSize)
        {
            try { File.Delete(tmp); } catch { }
            throw new IOException($"copied size mismatch: {tmp}");
        }
        if (op.ExpectedHash != null)
        {
            string dh = _hasher.HashFile(tmp, tfi.Length);
            if (!string.Equals(dh, op.ExpectedHash, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(tmp); } catch { }
                throw new IOException($"copied hash mismatch: {tmp}");
            }
        }
        File.Move(tmp, dst);
    }

    private void DoTrash(OpRow op, Func<string, string, string> resolve, Func<string, string> trashDirFor)
    {
        var src = resolve(op.SourceRoot!, op.SourcePath!);
        if (!File.Exists(src)) throw new ConflictException($"trash source missing (already gone?): {src}");
        // Invariant 1/5: refuse unless another valid copy exists — best-effort check via DB content group
        // MVP: verify hash matches expected before trashing; full cross-check done at plan time.
        var sfi = new FileInfo(src);
        if (op.ExpectedHash != null)
        {
            string sh = _hasher.HashFile(src, sfi.Length);
            if (!string.Equals(sh, op.ExpectedHash, StringComparison.OrdinalIgnoreCase))
                throw new ConflictException($"refusing trash: source changed: {src}");
        }
        string trashDir = trashDirFor(op.SourceRoot!);
        string rel = op.SourcePath!;
        string dst = Path.Combine(trashDir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        if (File.Exists(dst)) dst += "." + Guid.NewGuid().ToString("N");
        try { File.Move(src, dst); }
        catch (IOException)
        {
            // Cross-filesystem fallback: copy+verify+delete
            File.Copy(src, dst, false);
            string dh = _hasher.HashFile(dst, new FileInfo(dst).Length);
            if (op.ExpectedHash != null && !string.Equals(dh, op.ExpectedHash, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(dst); } catch { }
                throw new IOException($"trash copy verification failed: {src}");
            }
            File.Delete(src);
        }
    }
}
