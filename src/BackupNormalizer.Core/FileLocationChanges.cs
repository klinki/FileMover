using Microsoft.EntityFrameworkCore;

namespace BackupNormalizer;

public sealed record LocationChangeInput(string DatabasePath, string RootId);

public sealed record LocationChangeSource(
    LocationChangeInput Input,
    string RootName,
    string RootPath,
    string CaseSensitivity,
    string ScanStatus,
    string? ScannedUtc,
    int Files,
    int UsableHashes,
    int SkippedEntries
);

public sealed record FileLocation(string Side, string RelativePath, string State);

public sealed record LocationChangeGroup(
    string Id,
    string Classification,
    long Size,
    string? Digest,
    IReadOnlyList<FileLocation> Locations,
    IReadOnlyList<string> SharedPaths,
    IReadOnlyList<string> RemovedPaths,
    IReadOnlyList<string> AddedPaths,
    string? BeforePath,
    string? AfterPath,
    string? VerificationReason
);

public sealed record LocationChangesReport(
    LocationChangeSource A,
    LocationChangeSource B,
    string CreatedUtc,
    IReadOnlyDictionary<string, int> Summary,
    int UnverifiedFiles,
    IReadOnlyList<LocationChangeGroup> Groups
)
{
    public string Direction => "A → B";
}

public static class FileLocationChanges
{
    public const string Unchanged = "Unchanged";
    public const string Moved = "Moved";
    public const string Copied = "Copied";
    public const string RemovedCopies = "Removed copies";
    public const string Ambiguous = "Ambiguous";
    public const string OnlyInA = "Only in A";
    public const string OnlyInB = "Only in B";
    public const string Unverified = "Unverified";
    public static IReadOnlyList<string> Filters { get; } =
    [
        "Location changes",
        "All differences",
        Moved,
        Copied,
        RemovedCopies,
        Ambiguous,
        OnlyInA,
        OnlyInB,
        Unverified,
        Unchanged,
        "All results",
    ];

    public static LocationChangesReport Analyze(
        LocationChangeInput a,
        LocationChangeInput b,
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        a = a with { DatabasePath = Path.GetFullPath(a.DatabasePath) };
        b = b with { DatabasePath = Path.GetFullPath(b.DatabasePath) };
        progress?.Report("Loading inventory roots...");
        using var aDb = Database.OpenReadOnly(a.DatabasePath, pooling: false);
        using var bDb = Paths.PathEquals(a.DatabasePath, b.DatabasePath)
            ? null
            : Database.OpenReadOnly(b.DatabasePath, pooling: false);
        var targetDb = bDb ?? aDb;
        using var aTransaction = aDb.Context.Database.BeginTransaction();
        using var bTransaction = bDb?.Context.Database.BeginTransaction();
        var aRoot = RequireRoot(aDb, a, "A");
        var bRoot = RequireRoot(targetDb, b, "B");
        var aExclusions = aDb.GetPathExclusions(aRoot);
        var bExclusions = targetDb.GetPathExclusions(bRoot);
        bool Excluded(string path) => aExclusions.IsExcluded(path) || bExclusions.IsExcluded(path);
        var groups = new Dictionary<(long Size, string Digest), List<FileLocation>>();
        var unknown = new List<(long Size, FileLocation Location, string Reason)>();
        var aSource = Load(
            aDb,
            a,
            aRoot,
            "A",
            Excluded,
            groups,
            unknown,
            cancellationToken,
            progress
        );
        var bSource = Load(
            targetDb,
            b,
            bRoot,
            "B",
            Excluded,
            groups,
            unknown,
            cancellationToken,
            progress
        );
        var comparer =
            aRoot.CaseSensitivity == "insensitive" && bRoot.CaseSensitivity == "insensitive"
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
        var unknownSizes = unknown.Select(file => file.Size).ToHashSet();
        var results = new List<LocationChangeGroup>();
        progress?.Report("Matching recorded locations...");
        foreach (
            var group in groups
                .OrderBy(g => g.Key.Size)
                .ThenBy(g => g.Key.Digest, StringComparer.Ordinal)
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = group
                .Value.Where(p => p.Side == "A")
                .Select(p => p.RelativePath)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var after = group
                .Value.Where(p => p.Side == "B")
                .Select(p => p.RelativePath)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var beforeSet = before.ToHashSet(comparer);
            var afterSet = after.ToHashSet(comparer);
            var shared = before.Where(afterSet.Contains).ToArray();
            var removed = before.Where(p => !afterSet.Contains(p)).ToArray();
            var added = after.Where(p => !beforeSet.Contains(p)).ToArray();
            string classification =
                before.Length == 0 ? OnlyInB
                : after.Length == 0 ? OnlyInA
                : removed.Length == 0 && added.Length == 0 ? Unchanged
                : before.Length == 1 && after.Length == 1 ? Moved
                : shared.Length > 0 && removed.Length == 0 ? Copied
                : shared.Length > 0 && added.Length == 0 ? RemovedCopies
                : Ambiguous;
            string? reason = null;
            if (unknownSizes.Contains(group.Key.Size))
            {
                classification = Unverified;
                reason =
                    "Same-size unverified entries could contain additional copies of this content.";
            }
            var locations = before
                .Select(p => new FileLocation(
                    "A",
                    p,
                    afterSet.Contains(p) ? "Retained" : "Removed"
                ))
                .Concat(
                    after.Select(p => new FileLocation(
                        "B",
                        p,
                        beforeSet.Contains(p) ? "Retained" : "Added"
                    ))
                )
                .ToArray();
            results.Add(
                new LocationChangeGroup(
                    $"sha256:{group.Key.Size}:{group.Key.Digest}",
                    classification,
                    group.Key.Size,
                    group.Key.Digest,
                    locations,
                    shared,
                    removed,
                    added,
                    classification == Moved ? before[0] : null,
                    classification == Moved ? after[0] : null,
                    reason
                )
            );
        }
        foreach (
            var file in unknown
                .OrderBy(f => f.Location.Side, StringComparer.Ordinal)
                .ThenBy(f => f.Location.RelativePath, StringComparer.Ordinal)
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(
                new LocationChangeGroup(
                    $"unverified:{file.Location.Side}:{file.Location.RelativePath}",
                    Unverified,
                    file.Size,
                    null,
                    [file.Location],
                    [],
                    [],
                    [],
                    null,
                    null,
                    file.Reason
                )
            );
        }
        var summary = results
            .GroupBy(g => g.Classification)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report("Report ready.");
        return new LocationChangesReport(
            aSource,
            bSource,
            Database.UtcNow(),
            summary,
            unknown.Count,
            results
        );
    }

    public static string NormalizeFilter(string filter)
    {
        string key = filter.Trim().ToLowerInvariant().Replace(' ', '-');
        if (key == "changes")
            key = "location-changes";
        if (key == "all")
            key = "all-results";
        return Filters.FirstOrDefault(f => f.ToLowerInvariant().Replace(' ', '-') == key)
            ?? throw new ArgumentException(
                $"Unknown location-change filter '{filter}'. Use {string.Join(", ", Filters)}."
            );
    }

    public static LocationChangeGroup[] Filter(
        LocationChangesReport report,
        string filter,
        CancellationToken cancellationToken = default
    )
    {
        filter = NormalizeFilter(filter);
        var result = new List<LocationChangeGroup>();
        foreach (var group in report.Groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool include = filter switch
            {
                "Location changes" => group.Classification
                    is Moved
                        or Copied
                        or RemovedCopies
                        or Ambiguous,
                "All differences" => group.Classification != Unchanged,
                "All results" => true,
                _ => group.Classification == filter,
            };
            if (include)
                result.Add(group);
        }
        return result.ToArray();
    }

    private static StorageRootRow RequireRoot(Database db, LocationChangeInput input, string side)
    {
        var root =
            db.GetRoot(input.RootId)
            ?? throw new InvalidOperationException(
                $"Unknown root '{input.RootId}' in inventory {side} ({input.DatabasePath})."
            );
        if (db.LatestScanStatus(input.RootId) != ScanStatus.Completed)
            throw new InvalidOperationException(
                $"Inventory {side}, root '{input.RootId}', needs a complete successful scan before location analysis."
            );
        return root;
    }

    private static LocationChangeSource Load(
        Database db,
        LocationChangeInput input,
        StorageRootRow root,
        string side,
        Func<string, bool> excluded,
        Dictionary<(long Size, string Digest), List<FileLocation>> groups,
        List<(long Size, FileLocation Location, string Reason)> unknown,
        CancellationToken token,
        IProgress<string>? progress
    )
    {
        token.ThrowIfCancellationRequested();
        progress?.Report($"Reading inventory {side}...");
        var files = db.ListFilesWithHashes(input.RootId, "sha256");
        var rootComparer =
            root.CaseSensitivity == "insensitive"
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
        var links = files
            .Where(f => f.Status != FileStatus.Missing && f.EntryKind != EntryKind.File)
            .Select(f => Paths.NormalizeRelative(f.RelativePath))
            .ToHashSet(rootComparer);
        int count = 0,
            hashed = 0,
            skipped = 0;
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            string path = Paths.NormalizeRelative(file.RelativePath);
            if (
                file.Status == FileStatus.Missing
                || file.EntryKind != EntryKind.File
                || excluded(path)
                || Paths.FindRecordedLink(path, links) != null
            )
            {
                skipped++;
                continue;
            }
            count++;
            if (count % 1000 == 0)
                progress?.Report($"Inventory {side}: {count:N0} files read...");
            if (
                file.Status != FileStatus.Ok
                || file.Digest is not { Length: 64 }
                || !file.Digest.All(Uri.IsHexDigit)
            )
            {
                unknown.Add(
                    (
                        file.Size,
                        new FileLocation(side, path, Unverified),
                        file.Status != FileStatus.Ok
                            ? file.Error ?? "Entry has a scan error."
                            : "No usable current full SHA-256 hash."
                    )
                );
                continue;
            }
            hashed++;
            var key = (file.Size, file.Digest.ToLowerInvariant());
            if (!groups.TryGetValue(key, out var locations))
                groups[key] = locations = [];
            locations.Add(new FileLocation(side, path, ""));
        }
        return new LocationChangeSource(
            input,
            root.Name,
            root.Path,
            root.CaseSensitivity,
            ScanStatus.Completed,
            db.GetScanDetails(input.RootId)?.Scan.CompletedUtc,
            count,
            hashed,
            skipped
        );
    }
}
