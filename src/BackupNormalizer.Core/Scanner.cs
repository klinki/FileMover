namespace BackupNormalizer;

/// <summary>One enumerated filesystem entry. MFT mode fills metadata directly;
/// recursive mode leaves it empty and the scanner stats each file.</summary>
public sealed record FsEntry(string Path, bool IsDirectory, long Size, DateTime ModifiedUtc,
    DateTime CreatedUtc, bool HasMetadata, bool IsReparse, string? Error);

/// <summary>Live scan progress snapshot. Totals are unknown upfront (recursive
/// walk), so consumers estimate against a baseline (e.g. previous scan count).</summary>
public sealed record ScanProgress(string RootId, int Scanned, int Errors, string CurrentPath, TimeSpan Elapsed,
    bool Incremental = false);

/// <summary>A filesystem scan failure, separate from non-fatal link metadata notes.</summary>
public sealed record ScanError(string RootId, string Path, string Message);

/// <summary>Hashing progress, including read chunks before a file completes.</summary>
public sealed record HashProgress(int TotalFiles, int Hashed, int Skipped, int Unstable,
    long BytesRead, string CurrentPath, TimeSpan Elapsed)
{
    public int Processed => Hashed + Skipped + Unstable;
}

/// <summary>Scanner §7-8: cheap metadata first, no symlink follow, incremental reuse.</summary>
public sealed class Scanner
{
    private readonly Database _db;
    private readonly IContentHasher _hasher;
    private readonly string _algo;
    private readonly string _mftMode;
    private readonly string _usnMode;
    private readonly HashSet<string> _databaseFiles;
    private readonly Func<string, IEnumerable<FsEntry>>? _enumerate;
    private readonly Func<string, IUsnJournal?> _openJournal;
    private readonly IReadOnlyList<string>? _excludedPathRegexes;

    public bool LastScanWasIncremental { get; private set; }
    public string? LastScanFallbackReason { get; private set; }

    public Scanner(Database db, string? algo = null, string? mftMode = null, string? usnMode = null,
        IReadOnlyList<string>? excludedPathRegexes = null)
    {
        _db = db;
        _algo = HasherFactory.NormalizeAlgorithm(algo ?? "sha256");
        _hasher = HasherFactory.Create(_algo);
        _mftMode = mftMode ?? "off";
        _usnMode = (usnMode ?? "auto").ToLowerInvariant();
        if (_usnMode is not ("auto" or "off")) throw new ArgumentException("--usn must be auto or off.", nameof(usnMode));
        _openJournal = NtfsUsnJournal.TryOpen;
        _excludedPathRegexes = excludedPathRegexes == null ? null : new PathExclusions(excludedPathRegexes).Patterns;
        _databaseFiles = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
        {
            db.DbPath, db.DbPath + "-wal", db.DbPath + "-shm", db.DbPath + "-journal"
        };
    }

    internal Scanner(Database db, Func<string, IEnumerable<FsEntry>> enumerate,
        Func<string, IUsnJournal?>? openJournal = null, IReadOnlyList<string>? excludedPathRegexes = null)
        : this(db, excludedPathRegexes: excludedPathRegexes)
    {
        _enumerate = enumerate;
        _openJournal = openJournal ?? (_ => null);
    }

    private void RetireDatabaseEntries(StorageRootRow root)
    {
        string prefix = Path.GetFullPath(root.Path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var relativePaths = _databaseFiles.Where(path => path.StartsWith(prefix, comparison))
            .Select(path => Paths.GetRelative(root.Path, path)).ToArray();
        _db.RetireDatabasePaths(root.Id, relativePaths);
    }

    public (int scanned, int errors) ScanRoot(string rootId, IProgress<ScanProgress>? progress = null,
        Action<ScanError>? onError = null, bool full = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastScanWasIncremental = false;
        LastScanFallbackReason = null;
        var root = _db.GetRoot(rootId) ?? throw new InvalidOperationException($"unknown root '{rootId}'");
        var storedExclusions = _db.GetPathExclusions(root);
        var exclusions = _excludedPathRegexes == null ? storedExclusions
            : new PathExclusions(_excludedPathRegexes, root.CaseSensitivity == "insensitive");
        bool scopeChanged = !storedExclusions.Patterns.SequenceEqual(exclusions.Patterns);
        if (scopeChanged) full = true;
        var previousScan = _db.LatestScan(rootId);
        long scanId = _db.BeginScan(rootId);
        int scanned = 0, errors = 0;
        string scanMode = "NotStarted";
        var diagnostics = new List<ScanError>();
        var startedAt = DateTime.UtcNow;
        string currentDir = root.Path;
        void Report()
        {
            try { progress?.Report(new ScanProgress(rootId, scanned, errors, currentDir, DateTime.UtcNow - startedAt, LastScanWasIncremental)); }
            catch { }
        }
        void ReportError(string path, string message)
        {
            errors++;
            var diagnostic = new ScanError(rootId, path, message);
            diagnostics.Add(diagnostic);
            try { onError?.Invoke(diagnostic); }
            catch { } // Diagnostic observers must not change scan results.
        }
        NtfsMftEnumerator.NtfsVolume? mft = null;
        IUsnJournal? journal = null;
        UsnState? baseline = null;
        UsnChanges? changes = null;
        Database.DatabaseTransaction? scanTransaction = null;
        var missing = new List<string>();
        try
        {
            if (scopeChanged) _db.SetExcludedPathRegexes(rootId, exclusions.Patterns);
            if (!Directory.Exists(root.Path))
                throw new DirectoryNotFoundException($"root path not found: {root.Path}");
            RetireDatabaseEntries(root);
            var checkpoint = _db.GetScanCheckpoint(rootId);
            if (_usnMode != "off")
            {
                try
                {
                    journal = _openJournal(root.Path);
                    if (journal != null)
                    {
                        baseline = journal.Query(); // Capture before full enumeration, never after it.
                        if (checkpoint != null && UsnReplay.IsValid(checkpoint, previousScan, root.Path, baseline))
                            changes = UsnReplay.Read(journal, root.Path, checkpoint.NextUsn, baseline, _databaseFiles);
                        else if (checkpoint != null)
                            throw new IOException("The USN checkpoint, root identity, or retained journal history is no longer valid.");
                        else LastScanFallbackReason = "No USN checkpoint; establishing a full-scan baseline.";
                    }
                    else if (checkpoint != null) throw new IOException("The USN journal is unavailable or inaccessible.");
                    else LastScanFallbackReason = "USN journal unavailable; using a full scan.";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    LastScanFallbackReason = ex.Message;
                    if (checkpoint != null) _db.MarkRootHashesStale(rootId);
                    _db.ClearScanCheckpoint(rootId);
                    changes = null;
                    // The pre-scan snapshot can establish a new baseline after fallback.
                }
            }
            else _db.ClearScanCheckpoint(rootId);
            IEnumerable<FsEntry> entries;
            if (changes != null && !full)
            {
                try
                {
                    entries = PrepareIncrementalEntries(root.Path, changes, missing, exclusions);
                    LastScanWasIncremental = true;
                    scanMode = "USN";
                    LastScanFallbackReason = null;
                }
                catch (IOException ex)
                {
                    LastScanFallbackReason = ex.Message;
                    _db.MarkRootHashesStale(rootId);
                    _db.ClearScanCheckpoint(rootId);
                    changes = null;
                    missing.Clear();
                    entries = FullEntries();
                }
            }
            else entries = FullEntries();
            IEnumerable<FsEntry> FullEntries()
            {
                scanMode = "Recursive";
                if (_enumerate != null) return _enumerate(root.Path);
                if (NtfsMftEnumerator.TryCreate(root.Path, _mftMode, out var volume, out string? note))
                {
                    if (note != null) Log.Info(note);
                    mft = volume;
                    scanMode = "MFT";
                    string volumeRoot = Path.GetPathRoot(Path.GetFullPath(root.Path))!;
                    return volume!.EnumerateFiles(volumeRoot, root.Path)
                        .Select(f => new FsEntry(f.FullPath, f.IsDirectory, f.Size, f.ModifiedUtc, f.CreatedUtc,
                            HasMetadata: true, IsReparse: f.IsReparse, Error: null));
                }
                if (note != null) Log.Info(note);
                return EnumerateRecursive(root.Path, exclusions);
            }
            if (LastScanWasIncremental) scanTransaction = _db.BeginTransaction();
            if (changes != null)
            {
                foreach (var (relative, invalidate) in changes.Paths)
                    if (invalidate && !exclusions.IsExcluded(relative)) _db.MarkPathHashesStale(rootId, relative);
            }
            foreach (var scanEntry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = scanEntry.Path!;
                try
                {
                    if (_databaseFiles.Contains(Path.GetFullPath(file))) continue;
                    if (exclusions.Patterns.Count > 0 && !Paths.PathEquals(root.Path, file)
                        && exclusions.IsExcluded(Paths.GetRelative(root.Path, file))) continue;
                    if (scanEntry.Error != null)
                    {
                        ReportError(file, scanEntry.Error);
                        continue;
                    }
                    currentDir = Path.GetDirectoryName(file) ?? root.Path;
                    string rel;
                    rel = Paths.GetRelative(root.Path, file);
                    rel = Paths.NormalizeRelative(rel);
                    if (LastScanWasIncremental && Paths.FindLink(root.Path, rel) is { } parentLink
                        && !Paths.PathEquals(parentLink, file))
                        throw new IOException("The entry acquired a linked parent while refreshing its metadata.");
                    // Inspect the link before any length/content access, including dangling links.
                    if (scanEntry.IsReparse)
                    {
                        _db.UpsertFileEntry(ReadLink(rootId, rel, scanId, scanEntry));
                        scanned++;
                        if (scanned % 64 == 0) Report();
                        continue;
                    }
                    long sizeBefore;
                    string mBefore;
                    string? cBefore;
                    string name;
                    if (scanEntry.HasMetadata)
                    {
                        sizeBefore = scanEntry.Size;
                        mBefore = scanEntry.ModifiedUtc.ToString("o");
                        cBefore = scanEntry.CreatedUtc.ToString("o");
                        name = Path.GetFileName(file);
                    }
                    else
                    {
                        var fi = new FileInfo(file);
                        // A regular entry may have become a link since enumeration.
                        if (fi.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            _db.UpsertFileEntry(ReadLink(rootId, rel, scanId,
                                scanEntry with { IsDirectory = fi.Attributes.HasFlag(FileAttributes.Directory) }));
                            scanned++;
                            if (scanned % 64 == 0) Report();
                            continue;
                        }
                        sizeBefore = fi.Length;
                        mBefore = fi.LastWriteTimeUtc.ToString("o");
                        cBefore = SafeTime(fi.CreationTimeUtc);
                        name = fi.Name;
                    }
                    // incremental reuse check (§8): same root+path+size+mtime => reuse hash
                    var prev = _db.GetFileEntry(rootId, rel);
                    var entry = new FileEntryRow(0, rootId, rel, name, sizeBefore, mBefore,
                        cBefore, null, scanId, FileStatus.Ok, null);
                    long id = _db.UpsertFileEntry(entry);
                    // If metadata changed vs previous hash, mark stale (§8)
                    if (prev != null)
                    {
                        if (prev.Status != FileStatus.Ok || scopeChanged && storedExclusions.IsExcluded(rel))
                            _db.MarkFileHashesStale(id);
                        var h = _db.GetHash(id, _algo);
                        if (h != null && (h.SizeAtHash != sizeBefore || h.ModifiedUtcAtHash != mBefore))
                            _db.MarkHashStale(id, _algo);
                    }
                    scanned++;
                    if (scanned % 64 == 0) Report();
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException)
                {
                    ReportError(file, ex.Message);
                    TryRecordError(rootId, file, root.Path, scanId, FileStatus.ScanError, ex.Message);
                }
            }
            Report();
            cancellationToken.ThrowIfCancellationRequested();
            if (errors == 0)
            {
                ScanCheckpointRow? nextCheckpoint = null;
                if (journal != null && baseline != null)
                {
                    long nextUsn = changes?.NextUsn ?? baseline.NextUsn;
                    nextCheckpoint = new ScanCheckpointRow(rootId, root.Path, baseline.VolumeIdentity,
                        baseline.RootIdentity, baseline.JournalId, nextUsn, scanId);
                    try
                    {
                        var completed = new ScanRow(scanId, rootId, "", null, ScanStatus.Completed);
                        if (!UsnReplay.IsValid(nextCheckpoint, completed, root.Path, journal.Query()))
                            throw new IOException("The USN journal changed while scanning.");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        nextCheckpoint = null;
                        if (LastScanWasIncremental)
                        {
                            scanTransaction!.Rollback();
                            scanTransaction.Dispose();
                            scanTransaction = null;
                            _db.MarkRootHashesStale(rootId);
                            _db.ClearScanCheckpoint(rootId);
                            _db.FinishScan(scanId, ScanStatus.Incomplete);
                            // One bounded retry: full scans never recursively retry journal failures.
                            var result = ScanRoot(rootId, progress, onError, full: true, cancellationToken: cancellationToken);
                            LastScanFallbackReason = ex.Message;
                            return result;
                        }
                        LastScanFallbackReason = ex.Message;
                        _db.MarkRootHashesStale(rootId);
                    }
                }
                scanTransaction ??= _db.BeginTransaction();
                if (LastScanWasIncremental)
                {
                    foreach (string relative in missing)
                    {
                        _db.MarkPathHashesStale(rootId, relative);
                        _db.MarkPathMissing(rootId, relative);
                    }
                    scanned += missing.Count;
                    Report();
                }
                else _db.MarkUnseenFilesMissing(rootId, scanId);
                _db.FinishScan(scanId, ScanStatus.Completed);
                if (nextCheckpoint != null) _db.SaveScanCheckpoint(nextCheckpoint);
                else _db.ClearScanCheckpoint(rootId);
                cancellationToken.ThrowIfCancellationRequested();
                scanTransaction.Commit();
            }
            else
            {
                scanTransaction?.Rollback();
                scanTransaction?.Dispose();
                scanTransaction = null;
                if (LastScanWasIncremental) _db.MarkRootHashesStale(rootId);
                _db.ClearScanCheckpoint(rootId);
                _db.FinishScan(scanId, ScanStatus.Incomplete);
            }
            return (scanned, errors);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            scanTransaction?.Rollback();
            scanTransaction?.Dispose();
            scanTransaction = null;
            _db.ClearScanCheckpoint(rootId);
            if (LastScanWasIncremental) _db.MarkRootHashesStale(rootId);
            LastScanFallbackReason = "Scan canceled. Rescan to establish completeness.";
            _db.FinishScan(scanId, ScanStatus.Incomplete);
            throw;
        }
        catch (Exception ex)
        {
            scanTransaction?.Rollback();
            scanTransaction?.Dispose();
            scanTransaction = null;
            ReportError(root.Path, ex.Message);
            try
            {
                _db.ClearScanCheckpoint(rootId);
                if (LastScanWasIncremental) _db.MarkRootHashesStale(rootId);
            }
            catch { }
            try { _db.FinishScan(scanId, ScanStatus.Failed); } catch { }
            if (mft != null)
                throw new IOException($"MFT scan failed: {ex.Message} Retry with --mft off.", ex);
            throw;
        }
        finally
        {
            scanTransaction?.Dispose();
            try { _db.SaveScanDiagnostics(scanId, scanMode, scanned, LastScanFallbackReason, diagnostics); }
            catch (Exception ex) { Log.Error($"Cannot save scan #{scanId} diagnostics: {ex.Message}"); }
            journal?.Dispose();
            mft?.Dispose();
        }
    }

    private static List<FsEntry> PrepareIncrementalEntries(string root, UsnChanges changes, List<string> missing,
        PathExclusions exclusions)
    {
        var entries = new List<FsEntry>();
        foreach (string relative in changes.Paths.Keys)
        {
            if (exclusions.IsExcluded(relative)) continue;
            string path = Paths.CombineRoot(root, relative);
            try
            {
                if (Paths.FindLink(root, relative) is { } link && !Paths.PathEquals(link, path))
                    throw new IOException("A changed entry lies beneath a link; a full scan is required.");
                var attributes = File.GetAttributes(path);
                bool directory = attributes.HasFlag(FileAttributes.Directory);
                bool reparse = attributes.HasFlag(FileAttributes.ReparsePoint);
                if (directory && !reparse) throw new IOException("A changed file became a directory; a full scan is required.");
                entries.Add(new FsEntry(path, directory, 0, default, default, false, reparse, null));
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                missing.Add(relative);
            }
            catch (UnauthorizedAccessException ex)
            {
                entries.Add(new FsEntry(path, false, 0, default, default, false, false, ex.Message));
            }
        }
        return entries;
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

    private static FileEntryRow ReadLink(string rootId, string rel, long scanId, FsEntry entry)
    {
        FileSystemInfo info = entry.IsDirectory ? new DirectoryInfo(entry.Path) : new FileInfo(entry.Path);
        string? target = null, targetPath = null, note = null, created = null;
        string modified = entry.HasMetadata ? entry.ModifiedUtc.ToString("o") : Database.UtcNow();
        string kind = EntryKind.ReparsePoint;
        try
        {
            target = info.LinkTarget;
            if (target != null)
            {
                kind = entry.IsDirectory ? EntryKind.DirectoryLink : EntryKind.FileLink;
                var immediate = info.ResolveLinkTarget(false);
                targetPath = immediate?.FullName;
                if (immediate != null && !immediate.Exists) note = "Target is missing or unavailable.";
            }
            else note = "Reparse target is unavailable or unsupported.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
            or System.Security.SecurityException)
        {
            note = ex.Message;
        }
        try
        {
            if (entry.HasMetadata) created = entry.CreatedUtc.ToString("o");
            else
            {
                if (info.LastWriteTimeUtc.Year > 1601) modified = SafeTime(info.LastWriteTimeUtc);
                if (info.CreationTimeUtc.Year > 1601) created = SafeTime(info.CreationTimeUtc);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            note = note == null ? ex.Message : note + " " + ex.Message;
        }
        return new FileEntryRow(0, rootId, rel, Path.GetFileName(entry.Path), 0, modified, created,
            null, scanId, FileStatus.Ok, null, kind, target, targetPath, note);
    }

    internal static IEnumerable<FsEntry> EnumerateRecursive(string root, PathExclusions? exclusions = null)
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
                if (exclusions?.Patterns.Count > 0 && exclusions.IsExcluded(Paths.GetRelative(root, path))) continue;
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
                    yield return new FsEntry(path, isDir, 0, default, default, false, true, null);
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
                    // Content enumeration omits both file and directory links.
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
    public (int hashed, int skipped, int unstable) HashNeeded(string? rootId = null, bool all = false,
        int parallelism = 2, IProgress<HashProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var root in _db.ListRoots().Where(r => rootId == null || r.Id == rootId))
            RetireDatabaseEntries(root);
        var files = _db.ListFiles(rootId);
        var roots = _db.ListRoots().ToDictionary(r => r.Id);
        var exclusions = roots.Values.ToDictionary(r => r.Id, r => new PathExclusions(
            _db.GetExcludedPathRegexes(r.Id).Concat(_excludedPathRegexes ?? []), r.CaseSensitivity == "insensitive"));
        int hashed = 0, skipped = 0, unstable = 0;
        long bytesRead = 0;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var opts = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism), CancellationToken = cancellationToken };
        // HDD/NAS: default conservative (§29). Parallel file reads only; DB writes serialized via lock.
        var lockObj = new object();
        // Call under the same lock as counters so parallel workers deliver ordered snapshots.
        void Report(string path)
        {
            try { progress?.Report(new HashProgress(files.Count, hashed, skipped, unstable, bytesRead, path, timer.Elapsed)); }
            catch { } // A progress observer must not turn a successful hash into a skipped file.
        }
        void Complete(string path, string? state = null)
        {
            lock (lockObj)
            {
                if (state == HashState.Ok) hashed++;
                else if (state == HashState.Unstable) unstable++;
                else skipped++;
                Report(path);
            }
        }
        Report("");
        Parallel.ForEach(files, opts, f =>
        {
            if (f.Status != FileStatus.Ok || f.EntryKind != EntryKind.File)
            {
                Complete(f.RelativePath);
                return;
            }
            if (!roots.TryGetValue(f.StorageRootId, out var root)) { Complete(f.RelativePath); return; }
            if (exclusions[root.Id].IsExcluded(f.RelativePath)) { Complete(f.RelativePath); return; }
            var abs = Paths.CombineRoot(root.Path, f.RelativePath);
            string mBefore;
            long lenBefore;
            try
            {
                if (Paths.FindLink(root.Path, f.RelativePath) != null)
                {
                    lock (lockObj) _db.MarkFileHashesStale(f.Id);
                    Complete(abs);
                    return;
                }
                lock (lockObj)
                {
                    if (!all)
                    {
                        var existing = _db.GetHash(f.Id, _algo);
                        if (existing != null && existing.State == HashState.Ok && existing.SizeAtHash == f.Size && existing.ModifiedUtcAtHash == f.ModifiedUtc)
                        {
                            Complete(f.RelativePath);
                            return;
                        }
                    }
                }
                var fi = new FileInfo(abs);
                lenBefore = fi.Length; mBefore = fi.LastWriteTimeUtc.ToString("o");
            }
            catch { Complete(abs); return; }
            string digest;
            Action<long> onBytesRead = count =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (progress == null) return;
                lock (lockObj)
                {
                    bytesRead += count;
                    Report(abs);
                }
            };
            try { digest = _hasher.HashFile(abs, lenBefore, onBytesRead); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { Complete(abs); return; }
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (Paths.FindLink(root.Path, f.RelativePath) != null)
                {
                    lock (lockObj) _db.MarkFileHashesStale(f.Id);
                    Complete(abs);
                    return;
                }
                var fi2 = new FileInfo(abs);
                string mAfter = fi2.LastWriteTimeUtc.ToString("o");
                long lenAfter = fi2.Length;
                lock (lockObj)
                {
                    if (lenBefore != lenAfter || mBefore != mAfter)
                    {
                        // §7.4 unstable
                        _db.UpsertHash(new FileHashRow(f.Id, _algo, digest, lenBefore, mBefore, Database.UtcNow(), HashState.Unstable));
                        Complete(abs, HashState.Unstable);
                    }
                    else
                    {
                        _db.UpsertHash(new FileHashRow(f.Id, _algo, digest, lenBefore, mBefore, Database.UtcNow(), HashState.Ok));
                        Complete(abs, HashState.Ok);
                    }
                }
            }
            catch { Complete(abs); }
        });
        return (hashed, skipped, unstable);
    }
}
