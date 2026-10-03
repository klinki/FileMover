using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace BackupNormalizer;

public sealed partial class Database
{
    public IReadOnlyList<string> GetExcludedPathRegexes(string rootId)
    {
        if (_readOnly && !HasColumn("RootScanPolicy", "ExcludedPathRegexesJson")) return [];
        string? json = Context.RootScanPolicies.AsNoTracking().Where(p => p.StorageRootId == rootId)
            .Select(p => p.ExcludedPathRegexesJson).FirstOrDefault();
        return json == null ? [] : JsonSerializer.Deserialize<string[]>(json)
            ?? throw new InvalidOperationException($"Invalid exclusion policy for root '{rootId}'.");
    }

    public PathExclusions GetPathExclusions(StorageRootRow root) =>
        new(GetExcludedPathRegexes(root.Id), root.CaseSensitivity == "insensitive");

    internal void SetExcludedPathRegexes(string rootId, IReadOnlyList<string> patterns)
    {
        EnsureWritable();
        using var transaction = Context.Database.BeginTransaction();
        Context.RootScanPolicies.Where(p => p.StorageRootId == rootId).ExecuteDelete();
        AddAndSave(Context.RootScanPolicies, new RootScanPolicyEntity
        {
            StorageRootId = rootId, ExcludedPathRegexesJson = JsonSerializer.Serialize(patterns)
        });
        ClearScanCheckpoint(rootId);
        transaction.Commit();
    }
}
