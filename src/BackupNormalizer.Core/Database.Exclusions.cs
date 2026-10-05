using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace BackupNormalizer;

public sealed partial class Database
{
    public IReadOnlyList<string> GetExcludedPathRegexes(string rootId)
    {
        var queryRootId = rootId;
        var context = Context;

        string? json = context
            .RootScanPolicies.AsNoTracking()
            .Where(p => p.StorageRootId == queryRootId)
            .Select(p => p.ExcludedPathRegexesJson)
            .FirstOrDefault();
        return json == null
            ? []
            : JsonSerializer.Deserialize(json, CoreJsonContext.Compact.StringArray)
                ?? throw new InvalidOperationException(
                    $"Invalid exclusion policy for root '{queryRootId}'."
                );
    }

    public PathExclusions GetPathExclusions(StorageRootRow root) =>
        new(GetExcludedPathRegexes(root.Id), root.CaseSensitivity == "insensitive");

    internal void SetExcludedPathRegexes(string rootId, IReadOnlyList<string> patterns)
    {
        var queryRootId = rootId;
        var queryPatterns = patterns;
        var context = Context;
        EnsureWritable();
        using var transaction = context.Database.BeginTransaction();
        context.RootScanPolicies.Where(p => p.StorageRootId == queryRootId).ExecuteDelete();
        AddAndSave(
            context.RootScanPolicies,
            new RootScanPolicyEntity
            {
                StorageRootId = queryRootId,
                ExcludedPathRegexesJson = JsonSerializer.Serialize(
                    queryPatterns,
                    CoreJsonContext.Compact.IReadOnlyListString
                ),
            }
        );
        ClearScanCheckpoint(queryRootId);
        transaction.Commit();
    }
}
