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
        foreach (var f in db.ListFilesWithHashes(rootFilter, algo))
        {
            if (f.Status != FileStatus.Ok) continue;
            if (!roots.TryGetValue(f.StorageRootId, out var r)) continue;
            string basePath = r.Path;
            if (pathOverride != null && pathOverride.TryGetValue(f.StorageRootId, out var ov)) basePath = ov;
            out_.Add(new PhysicalFile(f.StorageRootId, f.RelativePath, f.Size, f.Digest, f.ModifiedUtc, Paths.CombineRoot(basePath, f.RelativePath)));
        }
        return out_;
    }
}
