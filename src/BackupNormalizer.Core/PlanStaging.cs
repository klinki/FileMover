using System.Text.Json;

namespace BackupNormalizer;

/// <summary>
/// Plan staging for the Total Commander style UI (drive-local, plan-only).
/// The UI never touches user files: it records intents with preconditions
/// (size + full hash) so the existing <see cref="Executor"/> can run them
/// later, including on another drive via --map-root.
/// </summary>
public static class PlanStaging
{
    public sealed record StagedOp(string Type, string SourceRel, string? DestRel, long ExpectedSize, string? ExpectedHash);

    private static void EnsureUnderBase(string basePath, string absPath)
    {
        string baseFull = Path.GetFullPath(basePath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(absPath);
        bool under = full.StartsWith(baseFull, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), baseFull.TrimEnd(Path.DirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        if (!under)
            throw new InvalidOperationException($"Path '{absPath}' is outside drive-local base '{basePath}'");
    }

    private static (long Size, string Hash) IdentityOf(string absFile)
    {
        var fi = new FileInfo(absFile);
        if (!fi.Exists) throw new FileNotFoundException($"staged source disappeared: {absFile}");
        string hash = HasherFactory.Create(null).HashFile(absFile, fi.Length);
        return (fi.Length, hash);
    }

    private static IEnumerable<string> FilesUnder(string absPath)
    {
        if (File.Exists(absPath)) { yield return absPath; yield break; }
        if (!Directory.Exists(absPath)) throw new DirectoryNotFoundException($"path not found: {absPath}");
        foreach (var f in Scanner.EnumerateFilesSafe(absPath))
            yield return f;
    }

    public static List<StagedOp> StageMkdir(string basePath, string absNewDir)
    {
        EnsureUnderBase(basePath, absNewDir);
        string rel = Paths.NormalizeRelative(Paths.GetRelative(basePath, absNewDir));
        return [new StagedOp("MKDIR", "", rel, 0, null)];
    }

    public static List<StagedOp> StageTrash(string basePath, string absTarget)
    {
        EnsureUnderBase(basePath, absTarget);
        var ops = new List<StagedOp>();
        foreach (var f in FilesUnder(absTarget))
        {
            var (size, hash) = IdentityOf(f);
            string rel = Paths.NormalizeRelative(Paths.GetRelative(basePath, f));
            ops.Add(new StagedOp("TRASH", rel, null, size, hash));
        }
        return ops;
    }

    public static List<StagedOp> StageCopy(string basePath, string absSource, string absDestDir)
    {
        return StageCopyOrMove(basePath, absSource, absDestDir, "COPY");
    }

    public static List<StagedOp> StageMove(string basePath, string absSource, string absDestDir)
    {
        return StageCopyOrMove(basePath, absSource, absDestDir, "MOVE");
    }

    private static List<StagedOp> StageCopyOrMove(string basePath, string absSource, string absDestDir, string type)
    {
        EnsureUnderBase(basePath, absSource);
        EnsureUnderBase(basePath, absDestDir);
        var ops = new List<StagedOp>();
        var mkdirs = new HashSet<string>();
        void EnsureMkdirRel(string dirRel)
        {
            if (string.IsNullOrEmpty(dirRel) || !mkdirs.Add(dirRel)) return;
            ops.Add(new StagedOp("MKDIR", "", dirRel, 0, null));
        }
        if (File.Exists(absSource))
        {
            string destAbs = Path.Combine(absDestDir, Path.GetFileName(absSource));
            EnsureUnderBase(basePath, destAbs);
            string srcRel = Paths.NormalizeRelative(Paths.GetRelative(basePath, absSource));
            string dstRel = Paths.NormalizeRelative(Paths.GetRelative(basePath, destAbs));
            string? dir = dstRel.Contains('/') ? dstRel[..dstRel.LastIndexOf('/')] : null;
            if (dir != null) EnsureMkdirRel(dir);
            var (size, hash) = IdentityOf(absSource);
            ops.Add(new StagedOp(type, srcRel, dstRel, size, hash));
            return ops;
        }
        if (!Directory.Exists(absSource)) throw new DirectoryNotFoundException($"source not found: {absSource}");
        string srcBaseName = new DirectoryInfo(absSource).Name;
        foreach (var f in FilesUnder(absSource))
        {
            string fileRelToSrc = Paths.NormalizeRelative(Paths.GetRelative(absSource, f));
            string destAbs = Path.Combine(Path.Combine(absDestDir, srcBaseName), fileRelToSrc.Replace('/', Path.DirectorySeparatorChar));
            EnsureUnderBase(basePath, destAbs);
            string srcRel = Paths.NormalizeRelative(Paths.GetRelative(basePath, f));
            string dstRel = Paths.NormalizeRelative(Paths.GetRelative(basePath, destAbs));
            string? dir = dstRel.Contains('/') ? dstRel[..dstRel.LastIndexOf('/')] : null;
            if (dir != null)
            {
                // add ancestor chain so executor can MKDIR in order
                var parts = dir.Split('/');
                for (int i = 1; i <= parts.Length; i++)
                    EnsureMkdirRel(string.Join('/', parts[..i]));
            }
            var (size, hash) = IdentityOf(f);
            ops.Add(new StagedOp(type, srcRel, dstRel, size, hash));
        }
        return ops;
    }

    /// <summary>Build an executor-compatible <see cref="PlanDoc"/> from staged ops.</summary>
    public static PlanDoc BuildPlanDoc(string planId, string rootId, IEnumerable<StagedOp> staged)
    {
        var ops = new List<PlanOpDoc>();
        int seq = 1;
        long bytesToCopy = 0;
        // Deterministic order: MKDIR, MOVE, COPY, TRASH, then rest.
        foreach (var s in staged.OrderBy(o => o.Type == "MKDIR" ? 0 : o.Type == "MOVE" ? 1 : o.Type == "COPY" ? 2 : o.Type == "TRASH" ? 3 : 4))
        {
            string? srcRoot = s.Type == "MKDIR" ? null : rootId;
            string? srcPath = s.Type == "MKDIR" ? null : s.SourceRel;
            string? dstRoot = s.Type == "TRASH" ? rootId : rootId;
            string? dstPath = s.Type switch
            {
                "MKDIR" => s.DestRel ?? s.SourceRel,
                "TRASH" => null,
                _ => s.DestRel,
            };
            if (s.Type == "COPY") bytesToCopy += s.ExpectedSize;
            ops.Add(new PlanOpDoc(seq++, s.Type, srcRoot, srcPath, dstRoot, dstPath, s.ExpectedSize, s.ExpectedHash));
        }
        return new PlanDoc(planId, Database.UtcNow(), bytesToCopy, ops);
    }

    public static string ToJson(PlanDoc doc) => Planner.ToJson(doc);

    /// <summary>Write a plan doc into a DB so the existing executor can run it.</summary>
    public static void WriteToDatabase(Database db, PlanDoc doc, string rootId, string rootPath, string role = "Backup")
    {
        if (db.PlanExists(doc.PlanId))
            throw new InvalidOperationException($"plan '{doc.PlanId}' already exists (immutable, §18)");
        if (db.GetRoot(rootId) == null)
            db.UpsertRoot(new StorageRootRow(rootId, rootId, Path.GetFullPath(rootPath), role, true,
                Paths.GetFileSystemId(rootPath), Paths.DetectCaseSensitivity(rootPath), Database.UtcNow()));
        db.InsertPlan(doc.PlanId, rootId, doc.EstimatedBytesCopied);
        foreach (var o in doc.Operations.OrderBy(o => o.Id))
            db.InsertOperation(doc.PlanId, o.Id, o.Type, o.SourceRoot, o.SourcePath,
                o.DestinationRoot, o.DestinationPath, o.ExpectedSize, o.ExpectedHash);
    }

    /// <summary>Import a plan JSON file into a DB (CLI `plan import` + UI "Write to .db" helper).</summary>
    public static PlanDoc ImportJson(string jsonPath)
    {
        string json = File.ReadAllText(jsonPath);
        var doc = JsonSerializer.Deserialize<PlanDoc>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        }) ?? throw new InvalidOperationException($"invalid plan file: {jsonPath}");
        if (string.IsNullOrWhiteSpace(doc.PlanId) || doc.Operations.Count == 0)
            throw new InvalidOperationException($"invalid plan file (missing planId/operations): {jsonPath}");
        foreach (var o in doc.Operations)
        {
            if (o.Type is not ("KEEP" or "MKDIR" or "MOVE" or "COPY" or "TRASH" or "DELETE" or "VERIFY"))
                throw new InvalidOperationException($"invalid operation type '{o.Type}' in {jsonPath}");
        }
        return doc;
    }
}
