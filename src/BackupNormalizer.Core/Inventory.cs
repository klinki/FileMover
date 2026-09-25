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
        List<string> Samples);

    public static DiffSummary Diff(string sourceDbPath, string sourceRootId,
        string targetDbPath, string targetRootId, string algo = "sha256", int samples = 20)
    {
        algo = HasherFactory.NormalizeAlgorithm(algo);
        using var sourceDb = new Database(sourceDbPath, readOnly: true);
        using var targetDb = Paths.PathEquals(sourceDb.DbPath, targetDbPath)
            ? null
            : new Database(targetDbPath, readOnly: true);
        var target = targetDb ?? sourceDb;
        if (sourceDb.GetRoot(sourceRootId) == null) throw new InvalidOperationException($"unknown source root '{sourceRootId}'");
        if (target.GetRoot(targetRootId) == null) throw new InvalidOperationException($"unknown target root '{targetRootId}'");

        var sourceFiles = Matcher.LoadFromDb(sourceDb, algo, sourceRootId).ToDictionary(file => file.RelativePath);
        var targetFiles = Matcher.LoadFromDb(target, algo, targetRootId).ToDictionary(file => file.RelativePath);
        int sourceOnly = 0, targetOnly = 0, changed = 0, identical = 0, unverified = 0;
        var sampleLines = new List<string>();
        void Sample(string line) { if (sampleLines.Count < samples) sampleLines.Add(line); }

        foreach (var (path, source) in sourceFiles)
        {
            if (!targetFiles.TryGetValue(path, out var dest))
            {
                sourceOnly++;
                Sample($"source-only: {path}");
                continue;
            }

            if (source.Size == dest.Size && source.Hash != null && dest.Hash != null &&
                string.Equals(source.Hash, dest.Hash, StringComparison.OrdinalIgnoreCase))
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
            if (!sourceFiles.ContainsKey(path))
            {
                targetOnly++;
                Sample($"target-only: {path}");
            }
        }

        return new DiffSummary(sourceOnly, targetOnly, changed, identical, unverified,
            sourceDb.LatestScanStatus(sourceRootId), target.LatestScanStatus(targetRootId), sampleLines);
    }
}
