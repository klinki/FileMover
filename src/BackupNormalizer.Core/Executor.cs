using Microsoft.EntityFrameworkCore;

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

        // Invariant 1 at execute time (not just plan time): a TRASH must leave
        // behind at least one other verified copy. The planner proves this for
        // the planned disk, but a remapped replay (--map-root) needs re-proof.
        bool HasSurvivingCopy(OpRow op)
        {
            if (op.ExpectedHash == null) return false;
            // (a) Same-plan completed COPY/MOVE: hash-verified when marked Completed.
            var completedCopies = _db.Context.PlanOperations
                .Where(candidate => candidate.PlanId == planId
                    && candidate.Status == "Completed"
                    && candidate.ExpectedHash == op.ExpectedHash
                    && (candidate.Type == "COPY" || candidate.Type == "MOVE"))
                .Select(candidate => new { candidate.DestinationRootId, candidate.DestinationPath, candidate.ExpectedSize })
                .ToList();
            foreach (var candidate in completedCopies)
            {
                string? dr = candidate.DestinationRootId;
                string? dp = candidate.DestinationPath;
                if (dr == null || dp == null) continue;
                if (dr == op.SourceRoot && dp == op.SourcePath) continue; // must be ANOTHER copy
                string abs;
                try { abs = ResolvePath(dr, dp); } catch { continue; } // offline snapshot root
                if (File.Exists(abs) && new FileInfo(abs).Length == candidate.ExpectedSize) return true;
            }
            // (b) DB-known copies: existence + size + fresh hash verification.
            // The SQL NOT expression returned no rows for a null source root/path.
            if (op.SourceRoot == null || op.SourcePath == null) return false;
            var candidates = _db.Context.FileHashes
                .Join(_db.Context.FileEntries, hash => hash.FileEntryId, entry => entry.Id,
                    (hash, entry) => new { hash, entry })
                .Where(pair => pair.hash.Digest == op.ExpectedHash
                    && pair.hash.State == "Ok"
                    && pair.entry.Status == "Ok"
                    && !(pair.entry.StorageRootId == op.SourceRoot && pair.entry.RelativePath == op.SourcePath))
                .Select(pair => new { pair.entry.StorageRootId, pair.entry.RelativePath, pair.entry.Size })
                .ToList();
            foreach (var candidate in candidates)
            {
                var cr = candidate.StorageRootId;
                var cp = candidate.RelativePath;
                var csz = candidate.Size;
                string abs;
                try { abs = ResolvePath(cr, cp); } catch { continue; }
                if (!File.Exists(abs)) continue;
                var fi = new FileInfo(abs);
                if (fi.Length != csz || (op.ExpectedSize != 0 && fi.Length != op.ExpectedSize)) continue;
                string digest;
                try { digest = _hasher.HashFile(abs, fi.Length); } catch { continue; }
                if (string.Equals(digest, op.ExpectedHash, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        var ops = LoadOps(planId);
        int done = 0, failed = 0, skipped = 0, conflicts = 0;
        int pos = 0, total = ops.Count;
        foreach (var op in ops)
        {
            pos++;
            string tag = $"[{pos}/{total}]";
            if (resume && op.Status == "Completed") { done++; continue; }
            if (op.Status == "Completed" && !resume) { done++; continue; }
            if (op.Type is "KEEP")
            {
                Mark(op.Id, "Completed");
                Journal(op.Id, "INFO", $"{tag} KEEP {op.DestRoot}:{op.DestPath}");
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
                    Journal(op.Id, "INFO", $"{tag} MKDIR {op.DestRoot}:{op.DestPath}");
                    done++;
                }
                catch (Exception ex) { Mark(op.Id, "Failed", ex.Message); Journal(op.Id, "ERROR", $"{tag} {ex.Message}"); failed++; if (stopOnError) break; }
                continue;
            }
            try
            {
                SetStarted(op.Id);
                switch (op.Type)
                {
                    case "MOVE": DoMove(op, ResolvePath); break;
                    case "COPY": DoCopy(op, ResolvePath); break;
                    case "TRASH": DoTrash(op, ResolvePath, TrashDirFor, HasSurvivingCopy); break;
                    case "VERIFY": DoVerify(op, ResolvePath); break;
                    default: throw new InvalidOperationException($"unknown op {op.Type}");
                }
                Mark(op.Id, "Completed");
                Journal(op.Id, "INFO", $"{tag} {op.Type} ok {op.SourceRoot}:{op.SourcePath} -> {op.DestRoot}:{op.DestPath} size={op.ExpectedSize} hash={op.ExpectedHash}");
                done++;
            }
            catch (ConflictException ex)
            {
                Mark(op.Id, "Conflict", ex.Message);
                Journal(op.Id, "WARN", $"{tag} CONFLICT {ex.Message}");
                conflicts++;
                if (stopOnError) break;
            }
            catch (Exception ex)
            {
                Mark(op.Id, "Failed", ex.Message);
                Journal(op.Id, "ERROR", $"{tag} {op.Type} failed: {ex.Message}");
                failed++;
                if (stopOnError) break;
            }
        }
        // Update plan status
        string planStatus = failed == 0 && conflicts == 0 ? "Completed" : "Partial";
        _db.Context.Plans
            .Where(plan => plan.Id == planId)
            .ExecuteUpdate(setters => setters.SetProperty(plan => plan.Status, planStatus));
        var trackedPlan = _db.Context.Plans.Local.FirstOrDefault(plan => plan.Id == planId);
        if (trackedPlan != null)
            _db.Context.Entry(trackedPlan).State = EntityState.Detached;
        return new ExecSummary(done, failed, skipped, conflicts);
    }

    private sealed class ConflictException : Exception { public ConflictException(string m) : base(m) { } }
    private sealed record OpRow(long Id, string Type, string? SourceRoot, string? SourcePath, string? DestRoot, string? DestPath, long ExpectedSize, string? ExpectedHash, string Status);

    private List<OpRow> LoadOps(string planId)
    {
        var out_ = _db.Context.PlanOperations
            .Where(operation => operation.PlanId == planId)
            .OrderBy(operation => operation.Sequence)
            .Select(operation => new OpRow(operation.Id, operation.Type,
                operation.SourceRootId, operation.SourcePath,
                operation.DestinationRootId, operation.DestinationPath,
                operation.ExpectedSize, operation.ExpectedHash, operation.Status))
            .ToList();
        if (out_.Count == 0) throw new InvalidOperationException($"unknown or empty plan '{planId}'");
        return out_;
    }

    private void SetStarted(long opId)
    {
        string startedUtc = Database.UtcNow();
        _db.Context.PlanOperations
            .Where(operation => operation.Id == opId)
            .ExecuteUpdate(setters => setters
                .SetProperty(operation => operation.Status, "Started")
                .SetProperty(operation => operation.StartedUtc, startedUtc));
    }

    private void Mark(long opId, string status, string? err = null)
    {
        string completedUtc = Database.UtcNow();
        _db.Context.PlanOperations
            .Where(operation => operation.Id == opId)
            .ExecuteUpdate(setters => setters
                .SetProperty(operation => operation.Status, status)
                .SetProperty(operation => operation.CompletedUtc, completedUtc)
                .SetProperty(operation => operation.Error, err));
    }

    private void Journal(long opId, string level, string msg)
    {
        var logEntry = new ExecutionLogEntity
        {
            PlanOperationId = opId,
            TimestampUtc = Database.UtcNow(),
            Level = level,
            Message = msg,
        };
        _db.Context.ExecutionLogs.Add(logEntry);
        _db.Context.SaveChanges();
        _db.Context.Entry(logEntry).State = EntityState.Detached;
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

    private void DoTrash(OpRow op, Func<string, string, string> resolve, Func<string, string> trashDirFor, Func<OpRow, bool> hasSurvivor)
    {
        var src = resolve(op.SourceRoot!, op.SourcePath!);
        if (!File.Exists(src)) throw new ConflictException($"trash source missing (already gone?): {src}");
        var sfi = new FileInfo(src);
        if (op.ExpectedSize != 0 && sfi.Length != op.ExpectedSize)
            throw new ConflictException($"trash source changed size: {src}");
        if (op.ExpectedHash == null)
            throw new ConflictException($"refusing trash without content identity: {src}");
        string sh = _hasher.HashFile(src, sfi.Length);
        if (!string.Equals(sh, op.ExpectedHash, StringComparison.OrdinalIgnoreCase))
            throw new ConflictException($"refusing trash: source changed: {src}");
        // Invariant 1 re-proven here (planner proof doesn't transfer across --map-root replay).
        if (!hasSurvivor(op))
            throw new ConflictException($"refusing trash: no surviving verified copy of content: {src}");
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
