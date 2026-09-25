using System.Text.Json;

namespace BackupNormalizer;

public sealed record PlanOp(string Type, string? SourceKind, string? SourceRoot, string? SourcePath,
    string? DestRoot, string? DestPath, long ExpectedSize, string? ExpectedHash);
public sealed record PlanDoc(string PlanId, string CreatedUtc, long EstimatedBytesCopied,
    string? SourceDatabasePath, string? SourceRoot, string? SourcePath,
    string TargetRoot, string TargetPath, List<PlanOpDoc> Operations);
public sealed record PlanOpDoc(int Id, string Type, string? SourceKind, string? SourceRoot,
    string? SourcePath, string? DestinationRoot, string? DestinationPath,
    long ExpectedSize, string? ExpectedHash);

/// <summary>Builds a target-root plan from one selected source root.</summary>
public sealed class Planner
{
    private readonly Database _targetDb;
    private readonly string _algo;

    public Planner(Database targetDb, string? algo = null)
    {
        _targetDb = targetDb;
        _algo = HasherFactory.NormalizeAlgorithm(algo ?? "sha256");
    }

    public sealed record PlanResult(string PlanId, int Keep, int Move, int Copy, int Trash, int Mkdir,
        long BytesToCopy, long BytesAvoided);

    public PlanResult PlanFromRoots(Database sourceDb, string sourceRootId, string targetRootId, string planId)
    {
        var sourceRoot = sourceDb.GetRoot(sourceRootId)
            ?? throw new InvalidOperationException($"unknown source root '{sourceRootId}'");
        var targetRoot = _targetDb.GetRoot(targetRootId)
            ?? throw new InvalidOperationException($"unknown target root '{targetRootId}'");
        if (!targetRoot.Writable) throw new InvalidOperationException($"target root '{targetRootId}' is read-only");
        if (sourceDb.LatestScanStatus(sourceRootId) != "Completed")
            throw new InvalidOperationException($"source root '{sourceRootId}' needs a complete successful scan before planning");
        if (_targetDb.LatestScanStatus(targetRootId) != "Completed")
            throw new InvalidOperationException($"target root '{targetRootId}' needs a complete successful scan before planning");
        if (Paths.RootsOverlap(sourceRoot.Path, targetRoot.Path))
            throw new InvalidOperationException("source and target root paths overlap; automatic plans require disjoint roots");
        if (_targetDb.Context.Plans.Any(plan => plan.Id == planId))
            throw new InvalidOperationException($"plan '{planId}' already exists (plans are immutable)");

        var sourceFiles = Matcher.LoadFromDb(sourceDb, _algo, sourceRootId)
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToList();
        var targetFiles = Matcher.LoadFromDb(_targetDb, _algo, targetRootId)
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToList();
        var desired = sourceFiles.ToDictionary(file => file.RelativePath, StringComparer.Ordinal);
        var current = targetFiles.ToDictionary(file => file.RelativePath, StringComparer.Ordinal);
        var targetByContent = targetFiles.Where(file => file.Hash != null)
            .GroupBy(file => (file.Size, file.Hash!))
            .ToDictionary(group => group.Key, group => group.ToList());
        var movedSources = new HashSet<string>(StringComparer.Ordinal);
        var plannedCopies = new Dictionary<(long Size, string Hash), List<string>>();
        var ops = new List<PlanOp>();
        var mkdirs = new HashSet<string>(StringComparer.Ordinal);
        long bytesToCopy = 0, bytesAvoided = 0;
        int keep = 0, move = 0, copy = 0, trash = 0, mkdir = 0;

        void EnsureMkdir(string rel)
        {
            var dir = rel.Contains('/') ? rel[..rel.LastIndexOf('/')] : null;
            if (dir == null || !mkdirs.Add(dir)) return;
            ops.Add(new PlanOp("MKDIR", null, null, null, targetRootId, dir, 0, null));
            mkdir++;
        }

        // An already-correct target file can supply another desired path, even
        // when that missing path sorts before it.
        foreach (var want in sourceFiles)
        {
            if (want.Hash != null && current.TryGetValue(want.RelativePath, out var existing)
                && existing.Size == want.Size && existing.Hash != null
                && string.Equals(existing.Hash, want.Hash, StringComparison.OrdinalIgnoreCase))
                AddPlannedCopy((want.Size, want.Hash), want.RelativePath);
        }

        foreach (var want in sourceFiles)
        {
            if (want.Hash == null)
                throw new InvalidOperationException($"source file '{want.RelativePath}' is not fully hashed; hash the source root before planning");

            if (current.TryGetValue(want.RelativePath, out var existing))
            {
                if (existing.Size == want.Size && existing.Hash != null &&
                    string.Equals(existing.Hash, want.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    ops.Add(new PlanOp("KEEP", "Target", targetRootId, existing.RelativePath,
                        targetRootId, want.RelativePath, want.Size, want.Hash));
                    keep++;
                }
                else
                {
                    // The executor verifies the destination against the source identity and
                    // reports a conflict if it differs. It never overwrites the target file.
                    ops.Add(new PlanOp("VERIFY", "Target", targetRootId, existing.RelativePath,
                        targetRootId, want.RelativePath, want.Size, want.Hash));
                }
                continue;
            }

            PhysicalFile? moveSource = null;
            if (targetByContent.TryGetValue((want.Size, want.Hash), out var candidates))
                moveSource = candidates.FirstOrDefault(candidate => !desired.ContainsKey(candidate.RelativePath)
                    && !movedSources.Contains(candidate.RelativePath));
            if (moveSource != null)
            {
                EnsureMkdir(want.RelativePath);
                ops.Add(new PlanOp("MOVE", "Target", targetRootId, moveSource.RelativePath,
                    targetRootId, want.RelativePath, want.Size, want.Hash));
                move++;
                bytesAvoided += want.Size;
                current.Remove(moveSource.RelativePath);
                movedSources.Add(moveSource.RelativePath);
                AddPlannedCopy((want.Size, want.Hash), want.RelativePath);
            }
            else if (plannedCopies.TryGetValue((want.Size, want.Hash), out var priorCopies) && priorCopies.Count > 0)
            {
                EnsureMkdir(want.RelativePath);
                ops.Add(new PlanOp("COPY", "Target", targetRootId, priorCopies[0],
                    targetRootId, want.RelativePath, want.Size, want.Hash));
                copy++;
                bytesToCopy += want.Size;
                AddPlannedCopy((want.Size, want.Hash), want.RelativePath);
            }
            else
            {
                EnsureMkdir(want.RelativePath);
                ops.Add(new PlanOp("COPY", "Source", sourceRootId, want.RelativePath,
                    targetRootId, want.RelativePath, want.Size, want.Hash));
                copy++;
                bytesToCopy += want.Size;
                AddPlannedCopy((want.Size, want.Hash), want.RelativePath);
            }
        }

        void AddPlannedCopy((long Size, string Hash) key, string rel)
        {
            if (!plannedCopies.TryGetValue(key, out var paths)) plannedCopies[key] = paths = new List<string>();
            paths.Add(rel);
        }

        // A target extra is moved to recoverable trash only when its verified content
        // will also exist at a desired target path after this plan completes.
        var desiredContent = sourceFiles.Where(file => file.Hash != null)
            .Select(file => (file.Size, file.Hash!)).ToHashSet();
        var targetContentToKeep = ops
            .Where(op => op.Type is "KEEP" or "MOVE" or "COPY")
            .Where(op => op.ExpectedHash != null)
            .Select(op => (op.ExpectedSize, op.ExpectedHash!))
            .ToHashSet();
        foreach (var extra in targetFiles)
        {
            if (desired.ContainsKey(extra.RelativePath) || movedSources.Contains(extra.RelativePath) || extra.Hash == null) continue;
            var key = (extra.Size, extra.Hash);
            if (!desiredContent.Contains(key) || !targetContentToKeep.Contains(key)) continue;
            ops.Add(new PlanOp("TRASH", "Target", targetRootId, extra.RelativePath,
                targetRootId, null, extra.Size, extra.Hash));
            trash++;
        }

        using var transaction = _targetDb.Context.Database.BeginTransaction();
        var plan = new PlanEntity
        {
            Id = planId,
            CreatedUtc = Database.UtcNow(),
            SourceDatabasePath = sourceDb.DbPath,
            SourceRootId = sourceRootId,
            SourceRootPath = Path.GetFullPath(sourceRoot.Path),
            TargetRootId = targetRootId,
            TargetRootPath = Path.GetFullPath(targetRoot.Path),
            Status = "Planned",
            EstimatedBytesCopied = bytesToCopy,
        };
        _targetDb.Context.Plans.Add(plan);
        int seq = 1;
        foreach (var op in ops.OrderBy(OperationOrder))
        {
            _targetDb.Context.PlanOperations.Add(new PlanOperationEntity
            {
                PlanId = planId,
                Sequence = seq++,
                Type = op.Type,
                SourceKind = op.SourceKind,
                SourceRootId = op.SourceRoot,
                SourcePath = op.SourcePath,
                DestinationRootId = op.DestRoot,
                DestinationPath = op.DestPath,
                ExpectedSize = op.ExpectedSize,
                ExpectedHash = op.ExpectedHash,
                Status = "Planned",
            });
        }
        _targetDb.Context.SaveChanges();
        transaction.Commit();
        _targetDb.Context.ChangeTracker.Clear();
        return new PlanResult(planId, keep, move, copy, trash, mkdir, bytesToCopy, bytesAvoided);
    }

    private static int OperationOrder(PlanOp op) => op.Type switch
    {
        "MKDIR" => 0,
        "KEEP" => 1,
        "MOVE" => 2,
        "COPY" => 3,
        "VERIFY" => 4,
        "TRASH" => 5,
        _ => 6,
    };

    public PlanDoc ExportPlan(string planId)
    {
        var plan = _targetDb.Context.Plans.SingleOrDefault(item => item.Id == planId)
            ?? throw new InvalidOperationException($"unknown plan '{planId}'");
        var ops = _targetDb.Context.PlanOperations
            .Where(operation => operation.PlanId == planId)
            .OrderBy(operation => operation.Sequence)
            .Select(operation => new PlanOpDoc(operation.Sequence, operation.Type,
                operation.SourceKind, operation.SourceRootId, operation.SourcePath,
                operation.DestinationRootId, operation.DestinationPath,
                operation.ExpectedSize, operation.ExpectedHash))
            .ToList();
        return new PlanDoc(planId, plan.CreatedUtc, plan.EstimatedBytesCopied,
            plan.SourceDatabasePath, plan.SourceRootId, plan.SourceRootPath,
            plan.TargetRootId, plan.TargetRootPath, ops);
    }

    public static string ToJson(PlanDoc doc) => JsonSerializer.Serialize(doc,
        new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
}
