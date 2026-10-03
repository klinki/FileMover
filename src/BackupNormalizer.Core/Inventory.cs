namespace BackupNormalizer;

/// <summary>Root-scoped source-to-target inventory comparison.</summary>
public static class Inventory
{
    public sealed record DiffSummary(
        int SourceOnly,
        int TargetOnly,
        int Changed,
        int Identical,
        int Unverified,
        string? SourceScanStatus,
        string? TargetScanStatus,
        List<string> Samples,
        int SkippedLinks = 0,
        int LinkConflicts = 0
    );

    public static DiffSummary Diff(
        string sourceDbPath,
        string sourceRootId,
        string targetDbPath,
        string targetRootId,
        string algo = "sha256",
        int samples = 20
    )
    {
        algo = HasherFactory.NormalizeAlgorithm(algo);
        using var sourceDb = new Database(sourceDbPath, readOnly: true);
        using var targetDb = Paths.PathEquals(sourceDb.DbPath, targetDbPath)
            ? null
            : new Database(targetDbPath, readOnly: true);
        var target = targetDb ?? sourceDb;
        if (sourceDb.GetRoot(sourceRootId) == null)
        {
            throw new InvalidOperationException($"unknown source root '{sourceRootId}'");
        }

        if (target.GetRoot(targetRootId) == null)
        {
            throw new InvalidOperationException($"unknown target root '{targetRootId}'");
        }

        var sourceExclusions = sourceDb.GetPathExclusions(sourceDb.GetRoot(sourceRootId)!);
        var targetExclusions = target.GetPathExclusions(target.GetRoot(targetRootId)!);
        bool Excluded(string path) =>
            sourceExclusions.IsExcluded(path) || targetExclusions.IsExcluded(path);

        var sourceLinks = sourceDb
            .ListFiles(sourceRootId)
            .Where(f =>
                f.Status != FileStatus.Missing
                && f.EntryKind != EntryKind.File
                && !Excluded(f.RelativePath)
            )
            .ToList();
        var targetLinks = target
            .ListFiles(targetRootId)
            .Where(f =>
                f.Status != FileStatus.Missing
                && f.EntryKind != EntryKind.File
                && !Excluded(f.RelativePath)
            )
            .ToList();
        var comparer =
            sourceDb.GetRoot(sourceRootId)!.CaseSensitivity == "insensitive"
            && target.GetRoot(targetRootId)!.CaseSensitivity == "insensitive"
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
        var sourceLinkPaths = sourceLinks.Select(f => f.RelativePath).ToHashSet(comparer);
        var targetLinkPaths = targetLinks.Select(f => f.RelativePath).ToHashSet(comparer);
        var sourceFiles = Matcher
            .LoadFromDb(sourceDb, algo, sourceRootId)
            .Where(f =>
                !Excluded(f.RelativePath)
                && Paths.FindRecordedLink(f.RelativePath, sourceLinkPaths) == null
            )
            .ToDictionary(file => file.RelativePath);
        var targetFiles = Matcher
            .LoadFromDb(target, algo, targetRootId)
            .Where(f =>
                !Excluded(f.RelativePath)
                && Paths.FindRecordedLink(f.RelativePath, targetLinkPaths) == null
            )
            .ToDictionary(file => file.RelativePath);
        int sourceOnly = 0,
            targetOnly = 0,
            changed = 0,
            identical = 0,
            unverified = 0;
        int linkConflicts = 0;
        var sampleLines = new List<string>();
        void Sample(string line)
        {
            if (sampleLines.Count < samples)
            {
                sampleLines.Add(line);
            }
        }

        foreach (var (path, source) in sourceFiles)
        {
            if (Paths.FindRecordedLink(path, targetLinkPaths) != null)
            {
                linkConflicts++;
                Sample($"type-conflict: {path} is blocked by a target link");
                continue;
            }
            if (!targetFiles.TryGetValue(path, out var dest))
            {
                sourceOnly++;
                Sample($"source-only: {path}");
                continue;
            }

            if (
                source.Size == dest.Size
                && source.Hash != null
                && dest.Hash != null
                && string.Equals(source.Hash, dest.Hash, StringComparison.OrdinalIgnoreCase)
            )
            {
                identical++;
            }
            else if (source.Size == dest.Size && (source.Hash == null || dest.Hash == null))
            {
                unverified++;
                Sample($"unverified: {path}");
            }
            else
            {
                changed++;
                Sample($"changed: {path} ({source.Size}->{dest.Size})");
            }
        }

        foreach (var (path, _) in targetFiles)
        {
            if (Paths.FindRecordedLink(path, sourceLinkPaths) != null)
            {
                linkConflicts++;
                Sample($"type-conflict: {path} is excluded by a source link");
                continue;
            }
            if (!sourceFiles.ContainsKey(path))
            {
                targetOnly++;
                Sample($"target-only: {path}");
            }
        }

        return new DiffSummary(
            sourceOnly,
            targetOnly,
            changed,
            identical,
            unverified,
            sourceDb.LatestScanStatus(sourceRootId),
            target.LatestScanStatus(targetRootId),
            sampleLines,
            sourceLinks.Count + targetLinks.Count,
            linkConflicts
        );
    }
}
