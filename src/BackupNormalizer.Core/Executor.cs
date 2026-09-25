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

    public ExecSummary Execute(string planId, string? sourcePathOverride = null, string? targetPathOverride = null,
        bool resume = false, bool stopOnError = false)
    {
        var plan = _db.GetPlan(planId)
            ?? throw new InvalidOperationException($"unknown plan '{planId}'");
        string sourceBasePath = Path.GetFullPath(sourcePathOverride ?? plan.SourceRootPath);
        string targetBasePath = Path.GetFullPath(targetPathOverride ?? plan.TargetRootPath);
        string ResolvePath(string sourceKind, string rootId, string rel)
        {
            string basePath = sourceKind switch
            {
                SourceScope.Source when rootId == plan.SourceRootId => sourceBasePath,
                SourceScope.Target when rootId == plan.TargetRootId => targetBasePath,
                _ => throw new InvalidOperationException($"root '{rootId}' is not part of plan '{planId}' as {sourceKind}")
            };
            return Paths.CombineRoot(basePath, rel);
        }
        string TrashDirFor(string rootId) => rootId == plan.TargetRootId
            ? Path.Combine(targetBasePath, _trashName, planId)
            : throw new InvalidOperationException($"trash root '{rootId}' is not the plan target");

        // Invariant 1 at execute time (not just plan time): a TRASH must leave
        // behind at least one other verified copy. The planner proves this for
        // the planned disk, but a replay with a different target path needs re-proof.
        bool HasSurvivingCopy(Database.PlanOperationRow op)
        {
            if (op.ExpectedHash == null || op.SourceKind != SourceScope.Target) return false;
            bool IsVerifiedCopy(string? root, string? rel, long size)
            {
                if (root != plan.TargetRootId || rel == null || (root == op.SourceRoot && rel == op.SourcePath)) return false;
                string abs;
                try { abs = ResolvePath(SourceScope.Target, root, rel); } catch { return false; }
                if (!File.Exists(abs)) return false;
                var file = new FileInfo(abs);
                if (file.Length != size || (op.ExpectedSize != 0 && file.Length != op.ExpectedSize)) return false;
                try { return string.Equals(_hasher.HashFile(abs, file.Length), op.ExpectedHash, StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            }

            // Completed target operations are the most reliable candidates because
            // they were created or checked by this plan.
            foreach (var candidate in _db.ListCompletedCopies(planId, op.ExpectedHash))
                if (IsVerifiedCopy(candidate.RootId, candidate.RelativePath, candidate.Size)) return true;

            // A separately scanned target copy can also keep the content safe.
            if (op.SourceRoot == null || op.SourcePath == null) return false;
            foreach (var candidate in _db.ListContentCopies(plan.TargetRootId, op.SourceRoot, op.SourcePath, op.ExpectedHash))
                if (IsVerifiedCopy(candidate.RootId, candidate.RelativePath, candidate.Size)) return true;
            return false;
        }
        var ops = _db.ListPlanOperations(planId);
        if (ops.Count == 0) throw new InvalidOperationException($"unknown or empty plan '{planId}'");
        if (ops.Any(op => op.SourceKind == SourceScope.Source) && Paths.RootsOverlap(sourceBasePath, targetBasePath))
            throw new InvalidOperationException("source and target paths overlap; execution requires disjoint roots");
        int done = 0, failed = 0, skipped = 0, conflicts = 0;
        int pos = 0, total = ops.Count;
        foreach (var op in ops)
        {
            pos++;
            string tag = $"[{pos}/{total}]";
            if (resume && op.Status == OpStatus.Completed) { done++; continue; }
            if (op.Status == OpStatus.Completed && !resume) { done++; continue; }
            if (op.Type is OpType.Mkdir)
            {
                try
                {
                    SetStarted(op.Id);
                    var dir = ResolvePath(SourceScope.Target, op.DestRoot!, op.DestPath!);
                    Directory.CreateDirectory(dir);
                    Mark(op.Id, OpStatus.Completed);
                    Journal(op.Id, "INFO", $"{tag} MKDIR {op.DestRoot}:{op.DestPath}");
                    done++;
                }
                catch (Exception ex) { Mark(op.Id, OpStatus.Failed, ex.Message); Journal(op.Id, "ERROR", $"{tag} {ex.Message}"); failed++; if (stopOnError) break; }
                continue;
            }
            try
            {
                SetStarted(op.Id);
                switch (op.Type)
                {
                    case OpType.Keep: DoVerify(op, ResolvePath); break;
                    case OpType.Move: DoMove(op, ResolvePath); break;
                    case OpType.Copy: DoCopy(op, ResolvePath); break;
                    case OpType.Trash: DoTrash(op, ResolvePath, TrashDirFor, HasSurvivingCopy); break;
                    case OpType.Verify: DoVerify(op, ResolvePath); break;
                    default: throw new InvalidOperationException($"unknown op {op.Type}");
                }
                Mark(op.Id, OpStatus.Completed);
                Journal(op.Id, "INFO", $"{tag} {op.Type} ok {op.SourceRoot}:{op.SourcePath} -> {op.DestRoot}:{op.DestPath} size={op.ExpectedSize} hash={op.ExpectedHash}");
                done++;
            }
            catch (ConflictException ex)
            {
                Mark(op.Id, OpStatus.Conflict, ex.Message);
                Journal(op.Id, "WARN", $"{tag} CONFLICT {ex.Message}");
                conflicts++;
                if (stopOnError) break;
            }
            catch (Exception ex)
            {
                Mark(op.Id, OpStatus.Failed, ex.Message);
                Journal(op.Id, "ERROR", $"{tag} {op.Type} failed: {ex.Message}");
                failed++;
                if (stopOnError) break;
            }
        }
        // Update plan status
        _db.UpdatePlanStatus(planId, failed == 0 && conflicts == 0 ? PlanStatus.Completed : PlanStatus.Partial);
        return new ExecSummary(done, failed, skipped, conflicts);
    }

    private sealed class ConflictException : Exception { public ConflictException(string m) : base(m) { } }

    private void SetStarted(long opId) => _db.MarkOperationStarted(opId, Database.UtcNow());

    private void Mark(long opId, string status, string? err = null) =>
        _db.MarkOperation(opId, status, err, Database.UtcNow());

    private void Journal(long opId, string level, string msg)
    {
        _db.AddExecutionLog(opId, level, msg, Database.UtcNow());
        Log.Info(msg, new { opId, level });
    }

    private void DoVerify(Database.PlanOperationRow op, Func<string, string, string, string> resolve)
    {
        if (op.DestRoot == null || op.DestPath == null) throw new ConflictException("VERIFY missing destination");
        var dst = resolve(SourceScope.Target, op.DestRoot, op.DestPath);
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

    private void DoMove(Database.PlanOperationRow op, Func<string, string, string, string> resolve)
    {
        if (op.SourceKind != SourceScope.Target) throw new ConflictException("MOVE source must be inside the target root");
        var src = resolve(SourceScope.Target, op.SourceRoot!, op.SourcePath!);
        var dst = resolve(SourceScope.Target, op.DestRoot!, op.DestPath!);
        if (!File.Exists(src))
        {
            // A crash can occur after File.Move but before the operation is journaled.
            if (File.Exists(dst) && op.ExpectedHash != null)
            {
                var landed = new FileInfo(dst);
                if (landed.Length == op.ExpectedSize && string.Equals(
                    _hasher.HashFile(dst, landed.Length), op.ExpectedHash, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            throw new ConflictException($"source disappeared: {src}");
        }
        var sfi = new FileInfo(src);
        if (op.ExpectedSize != 0 && sfi.Length != op.ExpectedSize)
            throw new ConflictException($"source changed size: {src}");
        if (File.Exists(dst))
            throw new ConflictException($"destination already exists while move source remains: {dst}");
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

    private void DoCopy(Database.PlanOperationRow op, Func<string, string, string, string> resolve)
    {
        if (op.SourceKind is not (SourceScope.Source or SourceScope.Target)) throw new ConflictException("COPY has no valid source scope");
        string src;
        try { src = resolve(op.SourceKind, op.SourceRoot!, op.SourcePath!); }
        catch { throw new ConflictException($"copy source '{op.SourceKind}:{op.SourceRoot}:{op.SourcePath}' is not available"); }
        var dst = resolve(SourceScope.Target, op.DestRoot!, op.DestPath!);
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

    private void DoTrash(Database.PlanOperationRow op, Func<string, string, string, string> resolve,
        Func<string, string> trashDirFor, Func<Database.PlanOperationRow, bool> hasSurvivor)
    {
        if (op.SourceKind != SourceScope.Target) throw new ConflictException("TRASH source must be inside the target root");
        var src = resolve(SourceScope.Target, op.SourceRoot!, op.SourcePath!);
        if (!File.Exists(src)) throw new ConflictException($"trash source missing (already gone?): {src}");
        var sfi = new FileInfo(src);
        if (op.ExpectedSize != 0 && sfi.Length != op.ExpectedSize)
            throw new ConflictException($"trash source changed size: {src}");
        if (op.ExpectedHash == null)
            throw new ConflictException($"refusing trash without content identity: {src}");
        string sh = _hasher.HashFile(src, sfi.Length);
        if (!string.Equals(sh, op.ExpectedHash, StringComparison.OrdinalIgnoreCase))
            throw new ConflictException($"refusing trash: source changed: {src}");
        // Invariant 1 re-proven here (planner proof doesn't transfer across remapped paths).
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
