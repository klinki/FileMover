using Microsoft.Data.Sqlite;

namespace BackupNormalizer;

/// <summary>Inventory §27: per-drive DBs, export/import merge, 2-DB diff.</summary>
public static class Inventory
{
    public static void ExportRoot(string dbPath, string rootId, string outputPath)
    {
        if (File.Exists(outputPath)) File.Delete(outputPath);
        // Create new DB with schema then copy rows
        using var dst = new Database(outputPath);
        using var src = new Database(dbPath);
        var root = src.GetRoot(rootId) ?? throw new InvalidOperationException($"unknown root '{rootId}'");
        dst.UpsertRoot(root);
        // Copy scans + file entries + hashes for root
        var files = src.ListFiles(rootId);
        var idMap = new Dictionary<long, long>();
        foreach (var f in files)
        {
            long newId = dst.UpsertFileEntry(f with { Id = 0, LastSeenScanId = 1 });
            idMap[f.Id] = newId;
            foreach (var algo in new[] { "sha256", "blake3" })
            {
                var h = src.GetHash(f.Id, algo);
                if (h != null) dst.UpsertHash(h with { FileEntryId = newId });
            }
        }
        // Ensure at least one scan row exists for FK sanity (LastSeenScanId may dangle; acceptable for inventory cache)
        Log.Info($"exported root '{rootId}' ({files.Count} files) to {outputPath}");
    }

    public static void ImportFile(string dbPath, string inputPath)
    {
        using var dst = new Database(dbPath);
        using var src = new Database(inputPath);
        foreach (var r in src.ListRoots())
        {
            if (dst.GetRoot(r.Id) == null)
                dst.UpsertRoot(r);
            else
                Log.Warn($"root '{r.Id}' already exists in target; merging file entries");
            foreach (var f in src.ListFiles(r.Id))
            {
                long newId = dst.UpsertFileEntry(f with { Id = 0 });
                foreach (var algo in new[] { "sha256", "blake3" })
                {
                    var h = src.GetHash(f.Id, algo);
                    if (h != null) dst.UpsertHash(h with { FileEntryId = newId });
                }
            }
        }
        Log.Info($"imported {inputPath} into {dbPath}");
    }

    public sealed record DiffSummary(int OnlyInOld, int OnlyInNew, int Changed, int Identical, List<string> Samples);

    public static DiffSummary Diff(string oldDbPath, string newDbPath, string algo = "sha256", int samples = 20)
    {
        algo = HasherFactory.NormalizeAlgorithm(algo);
        using var a = new Database(oldDbPath);
        using var b = new Database(newDbPath);
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
}
