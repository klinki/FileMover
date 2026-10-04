using System.Globalization;
using System.Text.Json;

namespace BackupNormalizer;

internal static class GroupedFileReportsExport
{
    internal static void Write(
        TextWriter writer,
        LocationChangesReport report,
        string format,
        string filter,
        CancellationToken token
    )
    {
        bool filenames = filter == GroupedFileReports.FilenameDifferences;
        if (filenames && !report.FilenameMatchingEnabled)
            throw new ArgumentException("Enable filename matching to export filename differences.");
        string side = filter == GroupedFileReports.DuplicatesInA ? "A" : "B";
        var families = filenames ? report.FilenameGroups : [];
        var duplicates =
            filenames ? []
            : side == "A" ? report.DuplicatesA
            : report.DuplicatesB;
        var unverified = filenames
            ? []
            : report.UnverifiedLocations.Where(f => f.Location.Side == side).ToArray();
        token.ThrowIfCancellationRequested();
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            var json = JsonSerializer.Serialize(
                new
                {
                    report.Direction,
                    report.A,
                    report.B,
                    report.CreatedUtc,
                    Filter = filter,
                    report.FilenameMatchingEnabled,
                    report.FilenameExtensions,
                    report.ExcludedPaths,
                    report.Summary,
                    report.UnverifiedFiles,
                    FilenameGroups = families,
                    DuplicateGroups = duplicates,
                    UnverifiedLocations = unverified,
                },
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                }
            );
            token.ThrowIfCancellationRequested();
            writer.WriteLine(json);
            return;
        }
        if (!format.Equals("csv", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("File reports support csv or json format.");
        string excludedPaths = JsonSerializer.Serialize(report.ExcludedPaths);
        LocationChangesExport.Row(
            writer,
            "GroupId",
            "Classification",
            "Filename",
            "VersionId",
            "VersionState",
            "Side",
            "RelativePath",
            "Size",
            "Digest",
            "VerificationReason",
            "CopiesA",
            "CopiesB",
            "ExtraCopies",
            "PotentialSavingsBytes",
            "Database",
            "RootId",
            "RootPath",
            "ScannedUtc",
            "Direction",
            "DatabaseA",
            "RootA",
            "RootPathA",
            "ScannedUtcA",
            "DatabaseB",
            "RootB",
            "RootPathB",
            "ScannedUtcB",
            "Filter",
            "FilenameMatchingEnabled",
            "FilenameExtensions",
            "ExcludedPaths"
        );
        int rows = 0;
        void Row(
            string id,
            string classification,
            string? filename,
            string? versionId,
            string? state,
            FileLocation? location,
            string? reason,
            int? copiesA = null,
            int? copiesB = null,
            int? extra = null,
            long? savings = null
        )
        {
            token.ThrowIfCancellationRequested();
            var source =
                location == null ? null
                : location.Side == "A" ? report.A
                : report.B;
            LocationChangesExport.Row(
                writer,
                id,
                classification,
                filename,
                versionId,
                state,
                location?.Side,
                location?.RelativePath,
                location?.Size?.ToString(CultureInfo.InvariantCulture),
                location?.Digest,
                reason,
                copiesA?.ToString(CultureInfo.InvariantCulture),
                copiesB?.ToString(CultureInfo.InvariantCulture),
                extra?.ToString(CultureInfo.InvariantCulture),
                savings?.ToString(CultureInfo.InvariantCulture),
                source?.Input.DatabasePath,
                source?.Input.RootId,
                source?.RootPath,
                source?.ScannedUtc,
                report.Direction,
                report.A.Input.DatabasePath,
                report.A.Input.RootId,
                report.A.RootPath,
                report.A.ScannedUtc,
                report.B.Input.DatabasePath,
                report.B.Input.RootId,
                report.B.RootPath,
                report.B.ScannedUtc,
                filter,
                report.FilenameMatchingEnabled.ToString(),
                string.Join(",", report.FilenameExtensions),
                excludedPaths
            );
            rows++;
        }
        foreach (var family in families)
        foreach (var version in family.Versions)
        {
            int copiesA = version.CopiesA,
                copiesB = version.CopiesB;
            string state = version.State;
            foreach (var location in version.Locations)
                Row(
                    family.Id,
                    family.Classification,
                    family.Filename,
                    version.Id,
                    state,
                    location,
                    version.VerificationReason,
                    copiesA,
                    copiesB
                );
        }
        foreach (var group in duplicates)
        foreach (var location in group.Locations)
            Row(
                group.Id,
                "Verified duplicates",
                null,
                group.Id,
                "Identical content",
                location,
                null,
                side == "A" ? group.Copies : null,
                side == "B" ? group.Copies : null,
                group.ExtraCopies,
                group.PotentialSavingsBytes
            );
        foreach (var file in unverified)
            Row(
                $"unverified:{side}:{file.Location.RelativePath}",
                FileLocationChanges.Unverified,
                null,
                null,
                FileLocationChanges.Unverified,
                file.Location,
                file.Reason
            );
        if (rows == 0)
            Row("", "Report metadata", null, null, "Metadata", null, null);
    }
}
