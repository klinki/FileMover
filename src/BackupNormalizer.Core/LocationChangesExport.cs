using System.Globalization;
using System.Text;
using System.Text.Json;

namespace BackupNormalizer;

public static class LocationChangesExport
{
    public static void Write(
        TextWriter writer,
        LocationChangesReport report,
        string format,
        string filter = "Quick differences",
        CancellationToken cancellationToken = default
    )
    {
        filter = FileLocationChanges.NormalizeFilter(filter);
        if (GroupedFileReports.IsGroupedView(filter))
        {
            GroupedFileReportsExport.Write(writer, report, format, filter, cancellationToken);
            return;
        }
        var groups = FileLocationChanges.Filter(report, filter, cancellationToken);
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            string json = JsonSerializer.Serialize(
                new
                {
                    report.Direction,
                    report.A,
                    report.B,
                    report.CreatedUtc,
                    Filter = filter,
                    report.Summary,
                    report.UnverifiedFiles,
                    report.FilenameMatchingEnabled,
                    report.FilenameExtensions,
                    report.ExcludedPaths,
                    Groups = groups,
                },
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                }
            );
            cancellationToken.ThrowIfCancellationRequested();
            writer.WriteLine(json);
            return;
        }
        if (!format.Equals("csv", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Location reports support csv or json format.");
        string excludedPaths = JsonSerializer.Serialize(report.ExcludedPaths);
        Row(
            writer,
            "GroupId",
            "Classification",
            "Side",
            "Database",
            "RootId",
            "RelativePath",
            "LocationState",
            "Size",
            "Digest",
            "VerificationReason",
            "BeforePath",
            "AfterPath",
            "Direction",
            "RootPath",
            "ScannedUtc",
            "DatabaseA",
            "RootA",
            "RootPathA",
            "ScannedUtcA",
            "DatabaseB",
            "RootB",
            "RootPathB",
            "ScannedUtcB",
            "Filter",
            "ExcludedPaths"
        );
        if (groups.Length == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Row(
                writer,
                "",
                "Report metadata",
                "",
                "",
                "",
                "",
                "Metadata",
                "",
                "",
                "",
                "",
                "",
                report.Direction,
                "",
                "",
                report.A.Input.DatabasePath,
                report.A.Input.RootId,
                report.A.RootPath,
                report.A.ScannedUtc,
                report.B.Input.DatabasePath,
                report.B.Input.RootId,
                report.B.RootPath,
                report.B.ScannedUtc,
                filter,
                excludedPaths
            );
        }
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var location in group.Locations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = location.Side == "A" ? report.A : report.B;
                Row(
                    writer,
                    group.Id,
                    group.Classification,
                    location.Side,
                    source.Input.DatabasePath,
                    source.Input.RootId,
                    location.RelativePath,
                    location.State,
                    (location.Size ?? group.Size).ToString(CultureInfo.InvariantCulture),
                    location.Digest ?? group.Digest,
                    group.VerificationReason,
                    group.BeforePath,
                    group.AfterPath,
                    report.Direction,
                    source.RootPath,
                    source.ScannedUtc,
                    report.A.Input.DatabasePath,
                    report.A.Input.RootId,
                    report.A.RootPath,
                    report.A.ScannedUtc,
                    report.B.Input.DatabasePath,
                    report.B.Input.RootId,
                    report.B.RootPath,
                    report.B.ScannedUtc,
                    filter,
                    excludedPaths
                );
            }
        }
    }

    public static void Save(
        string path,
        LocationChangesReport report,
        string format,
        string filter = "Quick differences",
        CancellationToken cancellationToken = default
    )
    {
        path = Path.GetFullPath(path);
        foreach (var source in new[] { report.A, report.B })
        foreach (string suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            if (Paths.PathEquals(path, source.Input.DatabasePath + suffix))
                throw new ArgumentException(
                    "The report destination cannot be an input database or one of its SQLite companions."
                );
        }
        cancellationToken.ThrowIfCancellationRequested();
        string temporary = Path.Combine(
            Path.GetDirectoryName(path)!,
            ".bn-location-report-" + Guid.NewGuid().ToString("N") + ".tmp"
        );
        try
        {
            using (
                var stream = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None
                )
            )
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                Write(writer, report, format, filter, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    internal static void Row(TextWriter writer, params string?[] values) =>
        writer.WriteLine(
            string.Join(
                ",",
                values.Select(value =>
                {
                    value ??= "";
                    return value.IndexOfAny([',', '"', '\r', '\n']) >= 0
                        ? "\"" + value.Replace("\"", "\"\"") + "\""
                        : value;
                })
            )
        );
}
