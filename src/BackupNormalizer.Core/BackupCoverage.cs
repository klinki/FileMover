using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace BackupNormalizer;

public sealed record CoverageInput(string DatabasePath, string RootId, string DeviceId);

public sealed record CoverageLocation(
    string DeviceId,
    string DatabasePath,
    string RootId,
    string RelativePath
);

public sealed record CoverageSource(
    CoverageInput Input,
    string RootPath,
    string? ScanStatus,
    string? ScannedUtc,
    bool LocallyAvailable,
    int UnverifiedFiles,
    int SkippedEntries
);

public sealed record CoverageContent(
    long Size,
    string Digest,
    IReadOnlyList<string> Devices,
    IReadOnlyList<CoverageLocation> Locations
)
{
    public int DeviceCount => Devices.Count;
}

public sealed record UnverifiedCoverageFile(CoverageLocation Location, long Size, string Reason);

public sealed record CoverageReport(
    IReadOnlyList<CoverageSource> Sources,
    IReadOnlyList<string> Devices,
    IReadOnlyList<CoverageContent> Content,
    IReadOnlyList<UnverifiedCoverageFile> Unverified
)
{
    public int SingleDeviceContent => Content.Count(item => item.DeviceCount == 1);
    public int ContentOnEveryDevice => Content.Count(item => item.DeviceCount == Devices.Count);
}

/// <summary>Historical content coverage. Device labels are selected by the user.</summary>
public static class BackupCoverage
{
    public static CoverageReport Analyze(
        IEnumerable<CoverageInput> inputs,
        CancellationToken cancellationToken = default
    )
    {
        var selections = inputs
            .Select(input =>
                input with
                {
                    DatabasePath = Path.GetFullPath(input.DatabasePath),
                    DeviceId = input.DeviceId.Trim(),
                }
            )
            .ToArray();
        if (
            selections.Length == 0
            || selections.Any(input =>
                string.IsNullOrWhiteSpace(input.DeviceId) || string.IsNullOrWhiteSpace(input.RootId)
            )
        )
        {
            throw new ArgumentException(
                "Select inventory roots and assign a device label to each."
            );
        }

        var sources = new List<CoverageSource>();
        var unverified = new List<UnverifiedCoverageFile>();
        var groups = new Dictionary<(long Size, string Digest), List<CoverageLocation>>();
        var selectedRoots = new HashSet<string>(StringComparer.Ordinal);
        foreach (
            var databaseGroup in selections.GroupBy(
                input => input.DatabasePath,
                OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal
            )
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var db = Database.OpenReadOnly(databaseGroup.Key, pooling: false);
            using var transaction = db.Context.Database.BeginTransaction();
            foreach (var selection in databaseGroup)
            {
                var input = selection with { DatabasePath = databaseGroup.Key };
                cancellationToken.ThrowIfCancellationRequested();
                string selectionKey = $"{input.DatabasePath}\0{input.RootId}";
                if (!selectedRoots.Add(selectionKey))
                {
                    var previous = sources.First(source =>
                        Paths.PathEquals(source.Input.DatabasePath, input.DatabasePath)
                        && source.Input.RootId == input.RootId
                    );
                    if (
                        !StringComparer.OrdinalIgnoreCase.Equals(
                            previous.Input.DeviceId,
                            input.DeviceId
                        )
                    )
                    {
                        throw new ArgumentException(
                            "The same inventory root cannot represent two devices."
                        );
                    }
                    continue;
                }

                var root =
                    db.GetRoot(input.RootId)
                    ?? throw new InvalidOperationException(
                        $"Unknown root '{input.RootId}' in {input.DatabasePath}."
                    );
                var scan = db.GetScanDetails(input.RootId)?.Scan;
                bool complete = scan?.Status == ScanStatus.Completed;
                var entries = db.ListFilesWithHashes(input.RootId, "sha256");
                var comparer =
                    root.CaseSensitivity == "insensitive"
                        ? StringComparer.OrdinalIgnoreCase
                        : StringComparer.Ordinal;
                var links = entries
                    .Where(file =>
                        file.Status != FileStatus.Missing && file.EntryKind != EntryKind.File
                    )
                    .Select(file => Paths.NormalizeRelative(file.RelativePath))
                    .ToHashSet(comparer);
                var exclusions = db.GetPathExclusions(root);
                int unknown = 0,
                    skipped = 0;
                foreach (var file in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (
                        file.Status == FileStatus.Missing
                        || file.EntryKind != EntryKind.File
                        || exclusions.IsExcluded(file.RelativePath)
                        || Paths.FindRecordedLink(file.RelativePath, links) != null
                    )
                    {
                        skipped++;
                        continue;
                    }

                    var location = new CoverageLocation(
                        input.DeviceId,
                        input.DatabasePath,
                        input.RootId,
                        file.RelativePath
                    );
                    if (
                        !complete
                        || file.Status != FileStatus.Ok
                        || file.Digest is not { Length: 64 }
                        || !file.Digest.All(Uri.IsHexDigit)
                    )
                    {
                        unknown++;
                        unverified.Add(
                            new UnverifiedCoverageFile(
                                location,
                                file.Size,
                                !complete ? "Inventory has no complete latest scan."
                                    : file.Status != FileStatus.Ok
                                        ? file.Error ?? "Entry has a scan error."
                                    : "No usable full SHA-256 hash."
                            )
                        );
                        continue;
                    }

                    var key = (file.Size, file.Digest.ToLowerInvariant());
                    if (!groups.TryGetValue(key, out var locations))
                    {
                        groups[key] = locations = [];
                    }
                    locations.Add(location);
                }

                sources.Add(
                    new CoverageSource(
                        input,
                        root.Path,
                        scan?.Status,
                        scan?.CompletedUtc,
                        Path.IsPathFullyQualified(root.Path) && Directory.Exists(root.Path),
                        unknown,
                        skipped
                    )
                );
            }
        }

        var devices = sources
            .Select(source => source.Input.DeviceId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var content = groups
            .Select(group => new CoverageContent(
                group.Key.Size,
                group.Key.Digest,
                group
                    .Value.Select(location => location.DeviceId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                group.Value.ToArray()
            ))
            .OrderBy(group => group.DeviceCount)
            .ThenBy(group => group.Locations[0].RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new CoverageReport(sources, devices, content, unverified);
    }

    public static string ScanAge(string? scannedUtc)
    {
        if (
            !DateTimeOffset.TryParse(
                scannedUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var date
            )
        )
        {
            return "Scan age unknown";
        }
        var age = DateTimeOffset.UtcNow - date;
        return age < TimeSpan.Zero ? "Scan time is in the future"
            : age.TotalDays >= 1 ? $"{(int)age.TotalDays} days ago"
            : "Less than one day ago";
    }
}
