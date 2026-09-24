namespace BackupNormalizer;

/// <summary>Scanner §7-8: cheap metadata first, no symlink follow, incremental reuse.</summary>
public sealed class Scanner
{
    private readonly Database _db;
    private readonly IContentHasher _hasher;
    private readonly string _algo;

    public Scanner(Database db, string? algo = null)
    {
        _db = db;
        _algo = HasherFactory.NormalizeAlgorithm(algo ?? "sha256");
        _hasher = HasherFactory.Create(_algo);
    }

    public (int scanned, int errors) ScanRoot(string rootId)
    {
        var root = _db.GetRoot(rootId) ?? throw new InvalidOperationException($"unknown root '{rootId}'");
        if (!Directory.Exists(root.Path))
            throw new DirectoryNotFoundException($"root path not found: {root.Path}");
        long scanId = _db.BeginScan(rootId);
        int scanned = 0, errors = 0;
        try
        {
            foreach (var file in EnumerateFilesSafe(root.Path))
            {
                try
                {
                    string rel;
                    try { rel = Paths.GetRelative(root.Path, file); }
                    catch { continue; }
                    rel = Paths.NormalizeRelative(rel);
                    var fi = new FileInfo(file);
                    // ReparsePoint already filtered, but double-check
                    if (fi.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        _db.UpsertFileEntry(new FileEntryRow(0, rootId, rel, fi.Name, 0,
                            fi.LastWriteTimeUtc.ToString("o"), SafeTime(fi.CreationTimeUtc), null, scanId, "UnsupportedEntry", "symlink"));
                        errors++;
                        continue;
                    }
                    long sizeBefore = fi.Length;
                    string mBefore = fi.LastWriteTimeUtc.ToString("o");
                    // incremental reuse check (§8): same root+path+size+mtime => reuse hash
                    var prev = _db.GetFileEntry(rootId, rel);
                    var entry = new FileEntryRow(0, rootId, rel, fi.Name, sizeBefore, mBefore,
                        SafeTime(fi.CreationTimeUtc), null, scanId, "Ok", null);
                    long id = _db.UpsertFileEntry(entry);
                    // If metadata changed vs previous hash, mark stale (§8)
                    if (prev != null)
                    {
                        var h = _db.GetHash(id, _algo);
                        if (h != null && (h.SizeAtHash != sizeBefore || h.ModifiedUtcAtHash != mBefore))
                            _db.MarkHashStale(id, _algo);
                    }
                    scanned++;
                }
                catch (UnauthorizedAccessException ex)
                {
                    errors++;
                    TryRecordError(rootId, file, root.Path, scanId, "AccessDenied", ex.Message);
                }
                catch (FileNotFoundException ex)
                {
                    errors++;
                    TryRecordError(rootId, file, root.Path, scanId, "NotFound", ex.Message);
                }
                catch (IOException ex)
                {
                    errors++;
                    TryRecordError(rootId, file, root.Path, scanId, "IoError", ex.Message);
                }
            }
            _db.FinishScan(scanId, "Completed");
            return (scanned, errors);
        }
        catch
        {
            try { _db.FinishScan(scanId, "Failed"); } catch { }
            throw;
        }
    }

    private void TryRecordError(string rootId, string full, string rootPath, long scanId, string status, string msg)
    {
        try
        {
            var rel = Paths.NormalizeRelative(Paths.GetRelative(rootPath, full));
            _db.UpsertFileEntry(new FileEntryRow(0, rootId, rel, Path.GetFileName(full), 0,
                Database.UtcNow(), null, null, scanId, status, msg));
        }
        catch { }
    }

    public static IEnumerable<string> EnumerateFilesSafe(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(dir); }
            catch { continue; }
            foreach (var e in entries)
            {
                FileAttributes attr;
                try { attr = File.GetAttributes(e); }
                catch { continue; }
                if (attr.HasFlag(FileAttributes.ReparsePoint))
                {
                    // §7.3: do not follow; if it's a file-link yield it so caller records UnsupportedEntry,
                    // if dir-link skip recursion.
                    if (!attr.HasFlag(FileAttributes.Directory))
                        yield return e;
                    continue;
                }
                if (attr.HasFlag(FileAttributes.Directory))
                    stack.Push(e);
                else
                    yield return e;
            }
        }
    }

    private static string SafeTime(DateTime dt)
    {
        try { return dt.ToUniversalTime().ToString("o"); } catch { return Database.UtcNow(); }
    }

    /// <summary>Demand-driven hashing (§9): hash only needed/ambiguous or all.</summary>
    public (int hashed, int skipped, int unstable) HashNeeded(string? rootId = null, bool all = false, int parallelism = 2)
    {
        var files = _db.ListFiles(rootId);
        var roots = _db.ListRoots().ToDictionary(r => r.Id);
        int hashed = 0, skipped = 0, unstable = 0;
        var opts = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism) };
        // HDD/NAS: default conservative (§29). Parallel file reads only; DB writes serialized via lock.
        var lockObj = new object();
        Parallel.ForEach(files, opts, f =>
        {
            if (f.Status != "Ok")
            {
                Interlocked.Increment(ref skipped);
                return;
            }
            lock (lockObj)
            {
                if (!all)
                {
                    var existing = _db.GetHash(f.Id, _algo);
                    if (existing != null && existing.State == "Ok" && existing.SizeAtHash == f.Size && existing.ModifiedUtcAtHash == f.ModifiedUtc)
                    {
                        Interlocked.Increment(ref skipped);
                        return;
                    }
                }
            }
            if (!roots.TryGetValue(f.StorageRootId, out var root)) { Interlocked.Increment(ref skipped); return; }
            var abs = Paths.CombineRoot(root.Path, f.RelativePath);
            string mBefore;
            long lenBefore;
            try
            {
                var fi = new FileInfo(abs);
                lenBefore = fi.Length; mBefore = fi.LastWriteTimeUtc.ToString("o");
            }
            catch { Interlocked.Increment(ref skipped); return; }
            string digest;
            try { digest = _hasher.HashFile(abs, lenBefore); }
            catch { Interlocked.Increment(ref skipped); return; }
            try
            {
                var fi2 = new FileInfo(abs);
                string mAfter = fi2.LastWriteTimeUtc.ToString("o");
                long lenAfter = fi2.Length;
                lock (lockObj)
                {
                    if (lenBefore != lenAfter || mBefore != mAfter)
                    {
                        // §7.4 unstable
                        _db.UpsertHash(new FileHashRow(f.Id, _algo, digest, lenBefore, mBefore, Database.UtcNow(), "Unstable"));
                        Interlocked.Increment(ref unstable);
                    }
                    else
                    {
                        _db.UpsertHash(new FileHashRow(f.Id, _algo, digest, lenBefore, mBefore, Database.UtcNow(), "Ok"));
                        Interlocked.Increment(ref hashed);
                    }
                }
            }
            catch { Interlocked.Increment(ref skipped); }
        });
        return (hashed, skipped, unstable);
    }
}
