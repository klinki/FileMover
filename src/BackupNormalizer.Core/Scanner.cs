namespace BackupNormalizer;

/// <summary>One enumerated filesystem entry. MFT mode fills metadata directly;
/// recursive mode leaves it empty and the scanner stats each file.</summary>
public sealed record FsEntry(string Path, bool IsDirectory, long Size, DateTime ModifiedUtc,
    DateTime CreatedUtc, bool HasMetadata, bool IsReparse, string? Error);

/// <summary>Scanner §7-8: cheap metadata first, no symlink follow, incremental reuse.</summary>
public sealed class Scanner
{
    private readonly Database _db;
    private readonly IContentHasher _hasher;
    private readonly string _algo;
    private readonly string _mftMode;

    public Scanner(Database db, string? algo = null, string? mftMode = null)
    {
        _db = db;
        _algo = HasherFactory.NormalizeAlgorithm(algo ?? "sha256");
        _hasher = HasherFactory.Create(_algo);
        _mftMode = mftMode ?? "off";
    }

    public (int scanned, int errors) ScanRoot(string rootId)
    {
        var root = _db.GetRoot(rootId) ?? throw new InvalidOperationException($"unknown root '{rootId}'");
        long scanId = _db.BeginScan(rootId);
        int scanned = 0, errors = 0;
        NtfsMftEnumerator.NtfsVolume? mft = null;
        try
        {
            if (!Directory.Exists(root.Path))
                throw new DirectoryNotFoundException($"root path not found: {root.Path}");
            IEnumerable<FsEntry> entries;
            if (NtfsMftEnumerator.TryCreate(root.Path, _mftMode, out var volume, out string? note))
            {
                if (note != null) Log.Info(note);
                mft = volume;
                string volumeRoot = Path.GetPathRoot(Path.GetFullPath(root.Path))!;
                entries = volume!.EnumerateFiles(volumeRoot, root.Path)
                    .Select(f => new FsEntry(f.FullPath, false, f.Size, f.ModifiedUtc, f.CreatedUtc,
                        HasMetadata: true, IsReparse: f.IsReparse, Error: null));
            }
            else
            {
                if (note != null) Log.Info(note);
                entries = EnumerateRecursive(root.Path);
            }
            foreach (var scanEntry in entries)
            {
                if (scanEntry.Error != null)
                {
                    errors++;
                    continue;
                }
                var file = scanEntry.Path!;
                try
                {
                    string rel;
                    rel = Paths.GetRelative(root.Path, file);
                    rel = Paths.NormalizeRelative(rel);
                    bool isReparse;
                    long sizeBefore;
                    string mBefore;
                    string cBefore;
                    string name;
                    if (scanEntry.HasMetadata)
                    {
                        isReparse = scanEntry.IsReparse;
                        sizeBefore = scanEntry.Size;
                        mBefore = scanEntry.ModifiedUtc.ToString("o");
                        cBefore = scanEntry.CreatedUtc.ToString("o");
                        name = Path.GetFileName(file);
                    }
                    else
                    {
                        var fi = new FileInfo(file);
                        // ReparsePoint already filtered, but double-check
                        isReparse = fi.Attributes.HasFlag(FileAttributes.ReparsePoint);
                        sizeBefore = fi.Length;
                        mBefore = fi.LastWriteTimeUtc.ToString("o");
                        cBefore = SafeTime(fi.CreationTimeUtc);
                        name = fi.Name;
                    }
                    if (isReparse)
                    {
                        _db.UpsertFileEntry(new FileEntryRow(0, rootId, rel, name, 0,
                            mBefore, cBefore, null, scanId, FileStatus.UnsupportedEntry, "symlink"));
                        errors++;
                        continue;
                    }
                    // incremental reuse check (§8): same root+path+size+mtime => reuse hash
                    var prev = _db.GetFileEntry(rootId, rel);
                    var entry = new FileEntryRow(0, rootId, rel, name, sizeBefore, mBefore,
                        cBefore, null, scanId, FileStatus.Ok, null);
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
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException)
                {
                    errors++;
                    TryRecordError(rootId, file, root.Path, scanId, FileStatus.ScanError, ex.Message);
                }
            }
            if (errors == 0)
            {
                _db.MarkUnseenFilesMissing(rootId, scanId);
                _db.FinishScan(scanId, ScanStatus.Completed);
            }
            else
            {
                _db.FinishScan(scanId, ScanStatus.Incomplete);
            }
            return (scanned, errors);
        }
        catch
        {
            try { _db.FinishScan(scanId, ScanStatus.Failed); } catch { }
            throw;
        }
        finally
        {
            mft?.Dispose();
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

    private static IEnumerable<FsEntry> EnumerateRecursive(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[]? entries = null;
            string? issue = null;
            try { entries = Directory.GetFileSystemEntries(dir); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { issue = ex.Message; }
            if (issue != null) { yield return new FsEntry(dir, false, 0, default, default, false, false, issue); continue; }
            foreach (var path in entries!)
            {
                FileAttributes? attr = null;
                issue = null;
                try { attr = File.GetAttributes(path); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { issue = ex.Message; }
                if (issue != null)
                {
                    yield return new FsEntry(path, false, 0, default, default, false, false, issue);
                    continue;
                }
                bool isDir = attr!.Value.HasFlag(FileAttributes.Directory);
                bool isReparse = attr.Value.HasFlag(FileAttributes.ReparsePoint);
                if (isReparse)
                {
                    if (!isDir) yield return new FsEntry(path, false, 0, default, default, false, true, null);
                    continue;
                }
                if (isDir) stack.Push(path);
                else yield return new FsEntry(path, false, 0, default, default, false, false, null);
            }
        }
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
            if (f.Status != FileStatus.Ok)
            {
                Interlocked.Increment(ref skipped);
                return;
            }
            lock (lockObj)
            {
                if (!all)
                {
                    var existing = _db.GetHash(f.Id, _algo);
                    if (existing != null && existing.State == HashState.Ok && existing.SizeAtHash == f.Size && existing.ModifiedUtcAtHash == f.ModifiedUtc)
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
                        _db.UpsertHash(new FileHashRow(f.Id, _algo, digest, lenBefore, mBefore, Database.UtcNow(), HashState.Unstable));
                        Interlocked.Increment(ref unstable);
                    }
                    else
                    {
                        _db.UpsertHash(new FileHashRow(f.Id, _algo, digest, lenBefore, mBefore, Database.UtcNow(), HashState.Ok));
                        Interlocked.Increment(ref hashed);
                    }
                }
            }
            catch { Interlocked.Increment(ref skipped); }
        });
        return (hashed, skipped, unstable);
    }
}
