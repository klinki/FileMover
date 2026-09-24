using System.Text.Json;

namespace BackupNormalizer;

public sealed record PlanOp(string Type, string? SourceRoot, string? SourcePath, string? DestRoot, string? DestPath, long ExpectedSize, string? ExpectedHash);
public sealed record PlanDoc(string PlanId, string CreatedUtc, long EstimatedBytesCopied, List<PlanOpDoc> Operations);
public sealed record PlanOpDoc(int Id, string Type, string? SourceRoot, string? SourcePath, string? DestinationRoot, string? DestinationPath, long ExpectedSize, string? ExpectedHash);

/// <summary>
/// Planner §12-16: KEEP > same-FS MOVE > COPY, TRASH only with surviving copy,
/// never delete on size/name alone (Invariants 1-3).
/// Supports single-DB canonical-root mode + two-DB snapshot-as-canonical mode
/// + root remap for plan migration.
/// </summary>
public sealed class Planner
{
    private readonly Database _db;
    private readonly string _algo;
    public Planner(Database db, string? algo = null)
    {
        _db = db;
        _algo = HasherFactory.NormalizeAlgorithm(algo ?? "sha256");
    }

    public sealed record PlanResult(string PlanId, int Keep, int Move, int Copy, int Trash, int Mkdir, long BytesToCopy, long BytesAvoided);

    public PlanResult PlanSingleDb(string canonicalRootId, string planId)
    {
        var roots = _db.ListRoots().ToDictionary(r => r.Id);
        if (!roots.ContainsKey(canonicalRootId)) throw new InvalidOperationException($"unknown canonical root '{canonicalRootId}'");
        var all = Matcher.LoadFromDb(_db, _algo);
        var canonicalFiles = all.Where(f => f.RootId == canonicalRootId).ToDictionary(f => f.RelativePath);
        return BuildPlan(planId, canonicalRootId, canonicalFiles, all, roots);
    }

    /// <summary>
    /// Two-DB mode (user extension): canonicalDb holds desired layout (T0 snapshot),
    /// targetDb (this _db) holds current state (T1). Canonical relative paths come
    /// from canonicalDb (optionally filtered to one root); content groups span both
    /// DBs for source selection, but same-FS MOVE is only planned within targetDb.
    /// </summary>
    public PlanResult PlanFromSnapshot(Database canonicalDb, string? canonicalRootFilter, string targetRootId, string planId, Dictionary<string, string>? canonicalRoles = null)
    {
        var targetRoots = _db.ListRoots().ToDictionary(r => r.Id);
        if (!targetRoots.ContainsKey(targetRootId)) throw new InvalidOperationException($"unknown target root '{targetRootId}'");
        var canonRoots = canonicalDb.ListRoots().ToDictionary(r => r.Id);
        var canonFiles = Matcher.LoadFromDb(canonicalDb, _algo, canonicalRootFilter);
        // Desired layout: relative path -> representative (size, hash). If multiple roots in canonical DB,
        // first occurrence wins; duplicates across canonical roots are treated as same desired path needing one copy.
        var desired = new Dictionary<string, PhysicalFile>();
        foreach (var f in canonFiles.OrderBy(f => f.RootId).ThenBy(f => f.RelativePath))
            desired.TryAdd(f.RelativePath, f);
        var currentAll = Matcher.LoadFromDb(_db, _algo);
        // For source selection include canonical copies too (they carry expected hashes).
        var combined = new List<PhysicalFile>(currentAll);
        combined.AddRange(canonFiles);
        var roles = targetRoots.ToDictionary(kv => kv.Key, kv => kv.Value.Role);
        foreach (var kv in canonRoots) roles.TryAdd("canon:" + kv.Key, kv.Value.Role);
        // Tag canonical files with distinct root prefix to avoid same-FS move confusion; planner below
        // only allows MOVE when source is in targetDb on same filesystem.
        var taggedCanon = canonFiles.Select(f => f with { RootId = "canon:" + f.RootId }).ToList();
        var combinedTagged = new List<PhysicalFile>(currentAll);
        combinedTagged.AddRange(taggedCanon);
        return BuildPlanWithSources(planId, targetRootId, desired, currentAll, combinedTagged, targetRoots, allowMoveOnlyWithin: true);
    }

    private PlanResult BuildPlan(string planId, string canonicalRootId, Dictionary<string, PhysicalFile> desired, List<PhysicalFile> all, Dictionary<string, StorageRootRow> roots)
        => BuildPlanWithSources(planId, canonicalRootId, desired, all, all, roots, allowMoveOnlyWithin: false);

    private PlanResult BuildPlanWithSources(string planId, string destRootId, Dictionary<string, PhysicalFile> desired,
        List<PhysicalFile> currentInTarget, List<PhysicalFile> sourceUniverse,
        Dictionary<string, StorageRootRow> targetRoots, bool allowMoveOnlyWithin)
    {
        var context = _db.Context;
        if (context.Plans.Any(p => p.Id == planId)) throw new InvalidOperationException($"plan '{planId}' already exists (immutable after approval, §18)");
        var byContent = sourceUniverse
            .Where(f => f.Hash != null)
            .GroupBy(f => (f.Size, f.Hash!))
            .ToDictionary(g => g.Key, g => g.ToList());
        var currentByPath = currentInTarget.Where(f => f.RootId == destRootId).ToDictionary(f => f.RelativePath);
        var ops = new List<PlanOp>();
        long bytesToCopy = 0, bytesAvoided = 0;
        int keep = 0, move = 0, copy = 0, trash = 0, mkdir = 0;
        var mkdirs = new HashSet<string>();
        var destFs = targetRoots[destRootId].FileSystemId;

        void EnsureMkdir(string rel)
        {
            var dir = rel.Contains('/') ? rel[..rel.LastIndexOf('/')] : null;
            if (dir == null || !mkdirs.Add(dir)) return;
            ops.Add(new PlanOp("MKDIR", null, null, destRootId, dir, 0, null));
            mkdir++;
        }

        // 1. Ensure each desired path exists with identical content
        var movedSources = new HashSet<string>();
        foreach (var (rel, want) in desired.OrderBy(kv => kv.Key))
        {
            if (currentByPath.TryGetValue(rel, out var cur))
            {
                if (cur.Size == want.Size && cur.Hash != null && want.Hash != null && cur.Hash == want.Hash)
                {
                    ops.Add(new PlanOp("KEEP", cur.RootId, cur.RelativePath, destRootId, rel, cur.Size, cur.Hash));
                    keep++;
                }
                else if (cur.Hash == null || want.Hash == null)
                {
                    // Ambiguous without full hash: need COPY/VERIFY later; plan COPY if sizes differ else VERIFY
                    if (cur.Size != want.Size)
                    {
                        var src = PickSourceFor(want, sourceUniverse, targetRoots);
                        if (src != null) { ops.Add(new PlanOp("COPY", src.RootId, src.RelativePath, destRootId, rel, want.Size, want.Hash)); copy++; bytesToCopy += want.Size; }
                    }
                    else
                    {
                        ops.Add(new PlanOp("VERIFY", cur.RootId, cur.RelativePath, destRootId, rel, cur.Size, cur.Hash ?? want.Hash));
                    }
                }
                else
                {
                    // Destination exists with different bytes -> conflict (§33): never auto-overwrite (Invariant 6)
                    ops.Add(new PlanOp("VERIFY", cur.RootId, cur.RelativePath, destRootId, rel, cur.Size, cur.Hash));
                }
                continue;
            }
            // Missing at desired path: find identical content
            List<PhysicalFile>? group = null;
            if (want.Hash != null && byContent.TryGetValue((want.Size, want.Hash), out var g)) group = g;
            // Prefer same-filesystem MOVE from elsewhere in target DB (§13)
            PhysicalFile? moveSrc = null;
            if (group != null)
            {
                foreach (var cand in group)
                {
                    if (cand.RelativePath == rel) continue;
                    if (!targetRoots.ContainsKey(cand.RootId)) continue; // canonical-tagged copies can't MOVE
                    if (currentByPath.ContainsKey(cand.RelativePath) && cand.RootId == destRootId)
                    {
                        // candidate exists in target; check same FS via stored FileSystemId
                        if (targetRoots[cand.RootId].FileSystemId == destFs)
                        { moveSrc = cand; break; }
                    }
                    else if (cand.RootId == destRootId)
                    { moveSrc = cand; break; }
                }
                // Also consider same-root different-path files even without hash group? No: need hash (Invariant 2/3).
            }
            if (moveSrc != null)
            {
                EnsureMkdir(rel);
                ops.Add(new PlanOp("MOVE", moveSrc.RootId, moveSrc.RelativePath, destRootId, rel, want.Size, want.Hash));
                move++;
                bytesAvoided += want.Size;
                // Remove moveSrc from currentByPath tracking so it isn't later trashed incorrectly.
                // The source path ceases to exist after MOVE — it must be excluded from TRASH phase.
                currentByPath.Remove(moveSrc.RelativePath);
                movedSources.Add($"{moveSrc.RootId}\0{moveSrc.RelativePath}");
            }
            else if (group != null && group.Count > 0)
            {
                var src = PickSourceFor(want, group, targetRoots);
                if (src != null)
                {
                    EnsureMkdir(rel);
                    ops.Add(new PlanOp("COPY", src.RootId, src.RelativePath, destRootId, rel, want.Size, want.Hash));
                    copy++; bytesToCopy += want.Size;
                }
            }
            else
            {
                // No known copy: record VERIFY placeholder so report shows missing content (can't fabricate bytes)
                ops.Add(new PlanOp("VERIFY", null, null, destRootId, rel, want.Size, want.Hash));
            }
        }

        // 2. Duplicates: TRASH extras that have a surviving identical copy (Invariant 1, §15-16)
        var desiredPaths = new HashSet<string>(desired.Keys);
        // Rebuild post-plan occupancy: desired paths will exist; only trash non-desired paths with dup content.
        // Sources consumed by MOVE no longer exist at their old path — skip them.
        foreach (var cur in currentInTarget.Where(f => f.RootId == destRootId).ToList())
        {
            if (desiredPaths.Contains(cur.RelativePath)) continue;
            if (movedSources.Contains($"{cur.RootId}\0{cur.RelativePath}")) continue;
            if (cur.Hash == null) continue; // never delete without full hash (§15)
            var key = (cur.Size, cur.Hash);
            if (!byContent.TryGetValue(key, out var grp)) continue;
            // Surviving copy: desired path with same content OR another non-trashed copy
            bool survives = desired.Any(kv => kv.Value.Hash == cur.Hash && kv.Value.Size == cur.Size)
                || grp.Count >= 2;
            if (survives)
            {
                ops.Add(new PlanOp("TRASH", cur.RootId, cur.RelativePath, cur.RootId, null, cur.Size, cur.Hash));
                trash++;
            }
        }

        using var transaction = context.Database.BeginTransaction();
        var planEntity = new PlanEntity
        {
            Id = planId,
            CreatedUtc = Database.UtcNow(),
            CanonicalRootId = destRootId,
            Status = "Planned",
            EstimatedBytesCopied = bytesToCopy,
        };
        context.Plans.Add(planEntity);
        var planOperations = new List<PlanOperationEntity>();
        int seq = 1;
        foreach (var op in ops.OrderBy(o => o.Type == "MKDIR" ? 0 : o.Type == "KEEP" ? 1 : o.Type == "MOVE" ? 2 : o.Type == "COPY" ? 3 : o.Type == "TRASH" ? 4 : 5))
        {
            var planOperation = new PlanOperationEntity
            {
                PlanId = planId,
                Sequence = seq++,
                Type = op.Type,
                SourceRootId = op.SourceRoot,
                SourcePath = op.SourcePath,
                DestinationRootId = op.DestRoot,
                DestinationPath = op.DestPath,
                ExpectedSize = op.ExpectedSize,
                ExpectedHash = op.ExpectedHash,
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
        return new PlanResult(planId, keep, move, copy, trash, mkdir, bytesToCopy, bytesAvoided);
    }

    private static PhysicalFile? PickSourceFor(PhysicalFile want, List<PhysicalFile> group, Dictionary<string, StorageRootRow> targetRoots)
    {
        var roles = targetRoots.ToDictionary(kv => kv.Key, kv => kv.Value.Role);
        var cands = group.Where(f => f.Hash == want.Hash && f.Size == want.Size).ToList();
        if (cands.Count == 0) return null;
        return CopyCost.PickSource(cands, roles)
            ?? cands.First();
    }

    public PlanDoc ExportPlan(string planId)
    {
        var plan = _db.Context.Plans.SingleOrDefault(p => p.Id == planId)
            ?? throw new InvalidOperationException($"unknown plan '{planId}'");
        var ops = _db.Context.PlanOperations
            .Where(o => o.PlanId == planId)
            .OrderBy(o => o.Sequence)
            .Select(o => new PlanOpDoc(o.Sequence, o.Type, o.SourceRootId, o.SourcePath,
                o.DestinationRootId, o.DestinationPath, o.ExpectedSize, o.ExpectedHash))
            .ToList();
        return new PlanDoc(planId, plan.CreatedUtc, plan.EstimatedBytesCopied, ops);
    }

    public static string ToJson(PlanDoc doc)
        => JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
}
