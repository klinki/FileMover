using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace BackupNormalizer;

public sealed partial class Database
{
    public IReadOnlyList<string> GetExcludedPathRegexes(string rootId)
    {
        var context = Context;

        string? json = context
            .RootScanPolicies.AsNoTracking()
            .Where(p => p.StorageRootId == rootId)
            .Select(p => p.ExcludedPathRegexesJson)
            .FirstOrDefault();
        return json == null
            ? []
            : JsonSerializer.Deserialize(json, CoreJsonContext.Compact.StringArray)
                ?? throw new InvalidOperationException(
                    $"Invalid exclusion policy for root '{rootId}'."
                );
    }

    public PathExclusions GetPathExclusions(StorageRootRow root) =>
        new(GetExcludedPathRegexes(root.Id), root.CaseSensitivity == "insensitive");

    internal void SetExcludedPathRegexes(string rootId, IReadOnlyList<string> patterns)
    {
        var context = Context;
        EnsureWritable();
        using var transaction = context.Database.BeginTransaction();
        context.RootScanPolicies.Where(p => p.StorageRootId == rootId).ExecuteDelete();
        AddAndSave(
            context.RootScanPolicies,
            new RootScanPolicyEntity
            {
                StorageRootId = rootId,
                ExcludedPathRegexesJson = JsonSerializer.Serialize(
                    patterns,
                    CoreJsonContext.Compact.IReadOnlyListString
                ),
            }
        );
        ClearScanCheckpoint(rootId);
        transaction.Commit();
    }
}
