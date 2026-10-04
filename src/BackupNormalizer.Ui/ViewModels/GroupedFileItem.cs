using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace BackupNormalizer.Ui.ViewModels;

public sealed record GroupedFileItem(
    string Label,
    string Details,
    IReadOnlyList<GroupedFileItem> Children
)
{
    public static GroupedFileItem[] Build(
        LocationChangesReport report,
        string filter,
        CancellationToken token
    )
    {
        var result = new List<GroupedFileItem>();
        if (filter == GroupedFileReports.FilenameDifferences)
        {
            foreach (var family in report.FilenameGroups)
            {
                token.ThrowIfCancellationRequested();
                var versions = new List<GroupedFileItem>();
                foreach (var version in family.Versions)
                {
                    token.ThrowIfCancellationRequested();
                    string label =
                        $"{version.State} | {version.Size:N0} bytes | copies A/B: {version.CopiesA}/{version.CopiesB}"
                        + $" | SHA-256: {version.Digest?[..12] ?? "unavailable"}";
                    string details =
                        label
                        + $"\nSHA-256: {version.Digest ?? "unavailable"}\n{version.VerificationReason}";
                    var sides = new[] { "A", "B" }
                        .Select(side =>
                        {
                            var paths = Paths(version.Locations.Where(l => l.Side == side), token);
                            return new GroupedFileItem(
                                $"Inventory {side}: {paths.Length:N0} files",
                                details,
                                paths
                            );
                        })
                        .ToArray();
                    versions.Add(new GroupedFileItem(label, details, sides));
                }
                string summary =
                    $"{family.Filename} | Filename-based candidate | verified versions A/B: {family.VerifiedVersionsA}/{family.VerifiedVersionsB}"
                    + $" | files A/B: {family.CopiesA}/{family.CopiesB} | unverified: {family.UnverifiedFiles}";
                result.Add(new GroupedFileItem(summary, summary, versions));
            }
        }
        else
        {
            string side = filter == GroupedFileReports.DuplicatesInA ? "A" : "B";
            var groups = side == "A" ? report.DuplicatesA : report.DuplicatesB;
            foreach (var group in groups)
            {
                token.ThrowIfCancellationRequested();
                string label =
                    $"{group.Size:N0} bytes | {group.Copies:N0} identical copies | {group.ExtraCopies:N0} extra"
                    + $" | potential saving: {group.PotentialSavingsBytes:N0} bytes | SHA-256: {group.Digest[..12]}";
                result.Add(
                    new GroupedFileItem(
                        label,
                        $"Inventory {side} only\n{label}\nSHA-256: {group.Digest}\nLogical bytes if one copy is kept.",
                        Paths(group.Locations, token)
                    )
                );
            }
            var unknown = new List<GroupedFileItem>();
            foreach (var file in report.UnverifiedLocations.Where(f => f.Location.Side == side))
            {
                token.ThrowIfCancellationRequested();
                unknown.Add(
                    new GroupedFileItem(
                        file.Location.RelativePath,
                        $"{side}: {file.Location.RelativePath}\n{file.Location.Size:N0} bytes\nUnverified: {file.Reason}",
                        []
                    )
                );
            }
            if (unknown.Count > 0)
                result.Add(
                    new GroupedFileItem(
                        $"Unverified files: {unknown.Count:N0}",
                        "These files are not confirmed duplicates and do not contribute to potential savings.",
                        unknown
                    )
                );
        }
        return result.ToArray();
    }

    private static GroupedFileItem[] Paths(
        IEnumerable<FileLocation> locations,
        CancellationToken token
    )
    {
        var paths = new List<GroupedFileItem>();
        foreach (var location in locations)
        {
            token.ThrowIfCancellationRequested();
            paths.Add(
                new GroupedFileItem(
                    location.RelativePath,
                    $"{location.Side}: {location.RelativePath}\n{location.Size:N0} bytes\nSHA-256: {location.Digest ?? "unavailable"}",
                    []
                )
            );
        }
        return paths.ToArray();
    }
}
