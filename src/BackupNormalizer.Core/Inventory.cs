using Microsoft.EntityFrameworkCore;

namespace BackupNormalizer;

/// <summary>Inventory §27: per-drive DBs, export/import merge, 2-DB diff.</summary>
public static class Inventory
{
    public static void ExportRoot(string dbPath, string rootId, string outputPath)
    {
        var sourcePath = Path.GetFullPath(dbPath);
        var destinationPath = Path.GetFullPath(outputPath);
        if (PathEquals(sourcePath, destinationPath))
            throw new InvalidOperationException("The inventory output must be a different file from its source database.");

        using var src = new Database(sourcePath, readOnly: true);
        var root = src.GetRoot(rootId) ?? throw new InvalidOperationException($"unknown root '{rootId}'");
        var files = src.ListFiles(rootId);
        var hashes = (from hash in src.Context.FileHashes.AsNoTracking()
                      join file in src.Context.FileEntries.AsNoTracking() on hash.FileEntryId equals file.Id
                      where file.StorageRootId == rootId
                      select hash).ToList();
        var hashesByFileId = hashes.GroupBy(h => h.FileEntryId).ToDictionary(g => g.Key, g => g.ToList());

        if (File.Exists(destinationPath)) File.Delete(destinationPath);
        using var dst = new Database(destinationPath);
        using var transaction = dst.Context.Database.BeginTransaction();
        dst.UpsertRoot(root);
        foreach (var file in files)
        {
            // Inventory files are standalone cache rows; their scan id is not a foreign key.
            var newId = dst.UpsertFileEntry(file with { Id = 0, LastSeenScanId = 1 });
            if (!hashesByFileId.TryGetValue(file.Id, out var fileHashes)) continue;
            foreach (var hash in fileHashes)
                dst.UpsertHash(new FileHashRow(newId, hash.Algorithm, hash.Digest, hash.SizeAtHash,
                    hash.ModifiedUtcAtHash, hash.CalculatedUtc, hash.State));
        }
        transaction.Commit();
        Log.Info($"exported root '{rootId}' ({files.Count} files) to {destinationPath}");
    }

    public static void ImportFile(string dbPath, string inputPath)
    {
        var destinationPath = Path.GetFullPath(dbPath);
        var sourcePath = Path.GetFullPath(inputPath);
        if (PathEquals(destinationPath, sourcePath))
            throw new InvalidOperationException("The inventory source must be a different file from the target database.");

        using var src = new Database(sourcePath, readOnly: true);
        using var dst = new Database(destinationPath);
        using var transaction = dst.Context.Database.BeginTransaction();
        foreach (var root in src.ListRoots())
        {
            if (dst.GetRoot(root.Id) == null)
                dst.UpsertRoot(root);
            else
                Log.Warn($"root '{root.Id}' already exists in target; merging file entries");

            var files = src.ListFiles(root.Id);
            var hashes = (from hash in src.Context.FileHashes.AsNoTracking()
                          join file in src.Context.FileEntries.AsNoTracking() on hash.FileEntryId equals file.Id
                          where file.StorageRootId == root.Id
                          select hash).ToList();
            var hashesByFileId = hashes.GroupBy(h => h.FileEntryId).ToDictionary(g => g.Key, g => g.ToList());

            foreach (var file in files)
            {
                var newId = dst.UpsertFileEntry(file with { Id = 0 });
                if (!hashesByFileId.TryGetValue(file.Id, out var fileHashes)) continue;
                foreach (var hash in fileHashes)
                    dst.UpsertHash(new FileHashRow(newId, hash.Algorithm, hash.Digest, hash.SizeAtHash,
                        hash.ModifiedUtcAtHash, hash.CalculatedUtc, hash.State));
            }
        }
        transaction.Commit();
        Log.Info($"imported {sourcePath} into {destinationPath}");
    }

    public sealed record DiffSummary(int OnlyInOld, int OnlyInNew, int Changed, int Identical, List<string> Samples);

    public static DiffSummary Diff(string oldDbPath, string newDbPath, string algo = "sha256", int samples = 20)
    {
        algo = HasherFactory.NormalizeAlgorithm(algo);
        using var a = new Database(oldDbPath, readOnly: true);
        using var b = new Database(newDbPath, readOnly: true);
        var fa = Matcher.LoadFromDb(a, algo).GroupBy(f => f.RelativePath).ToDictionary(g => g.Key, g => g.First());
        var fb = Matcher.LoadFromDb(b, algo).GroupBy(f => f.RelativePath).ToDictionary(g => g.Key, g => g.First());
        int onlyOld = 0, onlyNew = 0, changed = 0, same = 0;
        var sampleLines = new List<string>();
        foreach (var kv in fa)
        {
            if (!fb.TryGetValue(kv.Key, out var n)) { onlyOld++; if (sampleLines.Count < samples) sampleLines.Add($"- {kv.Key}"); }
            else if (kv.Value.Size == n.Size && kv.Value.Hash != null && n.Hash != null && kv.Value.Hash == n.Hash) same++;
            else { changed++; if (sampleLines.Count < samples) sampleLines.Add($"~ {kv.Key} ({kv.Value.Size}->{n.Size})"); }
        }
        foreach (var kv in fb)
            if (!fa.ContainsKey(kv.Key)) { onlyNew++; if (sampleLines.Count < samples) sampleLines.Add($"+ {kv.Key}"); }
        return new DiffSummary(onlyOld, onlyNew, changed, same, sampleLines);
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
