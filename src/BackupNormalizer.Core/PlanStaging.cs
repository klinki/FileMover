using System.Text.Json;

namespace BackupNormalizer;

/// <summary>
/// Plan staging for the Total Commander style UI (drive-local, plan-only).
/// The UI never touches user files: it records intents with preconditions
/// (size + full hash) so the existing <see cref="Executor"/> can run them
/// later, including on another drive via --target-path.
/// </summary>
public static class PlanStaging
{
    public sealed record StagedOp(string Type, string SourceRel, string? DestRel, long ExpectedSize, string? ExpectedHash);

    private static void EnsureUnderBase(string basePath, string absPath)
    {
        try { _ = Paths.GetRelative(basePath, absPath); }
        catch
        {
            throw new InvalidOperationException($"Path '{absPath}' is outside drive-local base '{basePath}'");
        }
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
    public static PlanDoc BuildPlanDoc(string planId, string rootId, string rootPath, IEnumerable<StagedOp> staged)
    {
        var ops = new List<PlanOpDoc>();
        int seq = 1;
        long bytesToCopy = 0;
        // Deterministic order: MKDIR, MOVE, COPY, TRASH, then rest.
        foreach (var s in staged.OrderBy(o => o.Type == "MKDIR" ? 0 : o.Type == "MOVE" ? 1 : o.Type == "COPY" ? 2 : o.Type == "TRASH" ? 3 : 4))
        {
            string? srcRoot = s.Type == "MKDIR" ? null : rootId;
            string? srcPath = s.Type == "MKDIR" ? null : s.SourceRel;
            string? dstRoot = rootId;
            string? dstPath = s.Type switch
            {
                "MKDIR" => s.DestRel ?? s.SourceRel,
                "TRASH" => null,
                _ => s.DestRel,
            };
            if (s.Type == "COPY") bytesToCopy += s.ExpectedSize;
            ops.Add(new PlanOpDoc(seq++, s.Type, srcRoot == null ? null : "Target",
                srcRoot, srcPath, dstRoot, dstPath, s.ExpectedSize, s.ExpectedHash));
        }
        string fullPath = Path.GetFullPath(rootPath);
        return new PlanDoc(planId, Database.UtcNow(), bytesToCopy, null,
            rootId, fullPath, rootId, fullPath, ops);
    }

    public static string ToJson(PlanDoc doc) => Planner.ToJson(doc);

    /// <summary>Write a plan doc into a DB so the existing executor can run it.</summary>
    public static void WriteToDatabase(Database db, PlanDoc doc, string rootId, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(doc.SourceRoot) || string.IsNullOrWhiteSpace(doc.SourcePath)
            || string.IsNullOrWhiteSpace(doc.TargetRoot) || string.IsNullOrWhiteSpace(doc.TargetPath)
            || doc.Operations == null || doc.Operations.Count == 0)
            throw new InvalidOperationException("plan is missing source, target, or operations");
        if (!string.Equals(rootId, doc.TargetRoot, StringComparison.Ordinal))
            throw new InvalidOperationException($"plan target root '{doc.TargetRoot}' does not match database root '{rootId}'");
        foreach (var operation in doc.Operations)
        {
            if (operation.Type is not ("KEEP" or "MKDIR" or "MOVE" or "COPY" or "TRASH" or "VERIFY"))
                throw new InvalidOperationException($"operation {operation.Id} has an unsupported type");
            if (operation.DestinationRoot != null && operation.DestinationRoot != doc.TargetRoot)
                throw new InvalidOperationException($"operation {operation.Id} destination is outside the plan target root");
            if (operation.SourceKind == "Target" && operation.SourceRoot != doc.TargetRoot)
                throw new InvalidOperationException($"operation {operation.Id} target-local source is outside the plan target root");
            if (operation.SourceKind == "Source" && operation.SourceRoot != doc.SourceRoot)
                throw new InvalidOperationException($"operation {operation.Id} source root disagrees with plan metadata");
            if (operation.SourcePath != null)
                _ = Paths.CombineRoot(operation.SourceKind == "Source" ? doc.SourcePath! : rootPath, operation.SourcePath);
            if (operation.DestinationPath != null)
                _ = Paths.CombineRoot(rootPath, operation.DestinationPath);
        }
        var context = db.Context;
        if (context.Plans.Any(plan => plan.Id == doc.PlanId))
            throw new InvalidOperationException($"plan '{doc.PlanId}' already exists (immutable, §18)");
        if (db.GetRoot(rootId) == null)
            db.UpsertRoot(new StorageRootRow(rootId, rootId, Path.GetFullPath(rootPath), true,
                Paths.GetFileSystemId(rootPath), Paths.DetectCaseSensitivity(rootPath), Database.UtcNow()));

        using var transaction = context.Database.BeginTransaction();
        var planEntity = new PlanEntity
        {
            Id = doc.PlanId,
            CreatedUtc = Database.UtcNow(),
            SourceDatabasePath = string.IsNullOrEmpty(doc.SourceDatabasePath) ? db.DbPath : Path.GetFullPath(doc.SourceDatabasePath),
            SourceRootId = doc.SourceRoot ?? rootId,
            SourceRootPath = Path.GetFullPath(doc.SourcePath ?? rootPath),
            TargetRootId = doc.TargetRoot,
            TargetRootPath = Path.GetFullPath(rootPath),
            Status = "Planned",
            EstimatedBytesCopied = doc.EstimatedBytesCopied,
        };
        context.Plans.Add(planEntity);
        var planOperations = new List<PlanOperationEntity>();
        foreach (var o in doc.Operations.OrderBy(o => o.Id))
        {
            var planOperation = new PlanOperationEntity
            {
                PlanId = doc.PlanId,
                Sequence = o.Id,
                Type = o.Type,
                SourceKind = o.SourceKind ?? "Target",
                SourceRootId = o.SourceRoot,
                SourcePath = o.SourcePath,
                DestinationRootId = o.DestinationRoot,
                DestinationPath = o.DestinationPath,
                ExpectedSize = o.ExpectedSize,
                ExpectedHash = o.ExpectedHash,
                Status = "Planned",
            };
            planOperations.Add(planOperation);
            context.PlanOperations.Add(planOperation);
        }
        context.SaveChanges();
        transaction.Commit();
        context.Entry(planEntity).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
        foreach (var operation in planOperations)
            context.Entry(operation).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
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
        if (string.IsNullOrWhiteSpace(doc.PlanId) || doc.Operations == null || doc.Operations.Count == 0
            || string.IsNullOrWhiteSpace(doc.SourceRoot) || string.IsNullOrWhiteSpace(doc.SourcePath)
            || string.IsNullOrWhiteSpace(doc.TargetRoot) || string.IsNullOrWhiteSpace(doc.TargetPath))
            throw new InvalidOperationException($"invalid or legacy plan file (missing plan/root metadata): {jsonPath}");
        foreach (var o in doc.Operations)
        {
            if (o.Type is not ("KEEP" or "MKDIR" or "MOVE" or "COPY" or "TRASH" or "VERIFY"))
                throw new InvalidOperationException($"invalid operation type '{o.Type}' in {jsonPath}");
            if (o.SourceRoot != null && o.SourceKind is not ("Source" or "Target"))
                throw new InvalidOperationException($"invalid source kind in operation {o.Id} in {jsonPath}");
        }
        return doc;
    }
}
