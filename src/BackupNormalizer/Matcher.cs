namespace BackupNormalizer;

public sealed record PhysicalFile(string RootId, string RelativePath, long Size, string? Hash, string ModifiedUtc, string AbsPath);
public sealed record ContentGroup(long Size, string? Hash, List<PhysicalFile> Copies);

/// <summary>Matcher §11: groups by (size, full-hash). Size-only never definitive.</summary>
public static class Matcher
{
    public static List<ContentGroup> BuildGroups(IEnumerable<PhysicalFile> files)
    {
        return files
            .GroupBy(f => (Size: f.Size, Hash: f.Hash ?? $"nohash:{f.RootId}:{f.RelativePath}"))
            .Select(g => new ContentGroup(g.Key.Size, g.Key.Hash?.StartsWith("nohash:") == true ? null : g.Key.Hash, g.ToList()))
            .ToList();
    }

    public static Dictionary<(long Size, string? Hash), ContentGroup> Index(IEnumerable<PhysicalFile> files)
        => BuildGroups(files).ToDictionary(g => (g.Size, g.Hash));

    public static List<PhysicalFile> LoadFromDb(Database db, string algo, string? rootFilter = null, Dictionary<string, string>? pathOverride = null)
    {
        var out_ = new List<PhysicalFile>();
        var roots = db.ListRoots().ToDictionary(r => r.Id);
        foreach (var f in db.ListFiles(rootFilter))
        {
            if (f.Status != "Ok") continue;
            if (!roots.TryGetValue(f.StorageRootId, out var r)) continue;
            string basePath = r.Path;
            if (pathOverride != null && pathOverride.TryGetValue(f.StorageRootId, out var ov)) basePath = ov;
            var h = db.GetHash(f.Id, algo);
            string? digest = h?.State == "Ok" ? h.Digest : null;
            out_.Add(new PhysicalFile(f.StorageRootId, f.RelativePath, f.Size, digest, f.ModifiedUtc, Paths.CombineRoot(basePath, f.RelativePath)));
        }
        return out_;
    }
}

/// <summary>Copy source cost abstraction (§14).</summary>
public static class CopyCost
{
    public static int RankRole(string role) => role switch
    {
        "Canonical" => 0,
        "Backup" => 1,
        "Archive" => 2,
        "Temporary" => 3,
        _ => 4,
    };

    public static PhysicalFile? PickSource(IEnumerable<PhysicalFile> candidates, Dictionary<string, string> rootRoles)
    {
        return candidates
            .OrderBy(f => rootRoles.TryGetValue(f.RootId, out var r) ? RankRole(r) : 9)
            .ThenBy(f => f.AbsPath.Length) // prefer shorter/local paths
            .FirstOrDefault();
    }
}
