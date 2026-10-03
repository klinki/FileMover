using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BackupNormalizer;

namespace BackupNormalizer.Tests;

[Collection("Console")]
public sealed class BackupCoverageTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        AppContext.BaseDirectory,
        "bn-coverage-" + Guid.NewGuid().ToString("N")
    );
    private const string Modified = "2026-01-01T00:00:00Z";

    public BackupCoverageTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, true);

    private string DbPath(string name) => Path.Combine(_directory, name + ".db");

    private static string Digest(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private void Seed(
        string database,
        string root,
        string status,
        params (string Path, string Content, bool HasHash)[] files
    )
    {
        using var db = Database.OpenWritable(DbPath(database), pooling: false);
        db.UpsertRoot(
            new StorageRootRow(
                root,
                root,
                Path.Combine(_directory, "offline", root),
                false,
                database,
                "sensitive",
                Modified
            )
        );
        long scan = db.BeginScan(root);
        foreach (var file in files)
        {
            long id = db.UpsertFileEntry(
                new FileEntryRow(
                    0,
                    root,
                    file.Path,
                    Path.GetFileName(file.Path),
                    file.Content.Length,
                    Modified,
                    null,
                    null,
                    scan,
                    FileStatus.Ok,
                    null
                )
            );
            if (file.HasHash)
                db.UpsertHash(
                    new FileHashRow(
                        id,
                        "sha256",
                        Digest(file.Content),
                        file.Content.Length,
                        Modified,
                        Modified,
                        HashState.Ok
                    )
                );
        }
        db.FinishScan(scan, status);
    }

    [Fact]
    public void Counts_Content_By_Distinct_Device_And_Reads_Offline_Snapshots_Without_Writing()
    {
        Seed(
            "nas",
            "photos",
            ScanStatus.Completed,
            ("a.jpg", "same", true),
            ("b.jpg", "same", true),
            ("unique.jpg", "unique", true)
        );
        Seed("nas", "archive", ScanStatus.Completed, ("copy.jpg", "same", true));
        Seed("disk", "photos", ScanStatus.Completed, ("renamed.jpg", "same", true));
        byte[] before = File.ReadAllBytes(DbPath("nas"));
        var report = BackupCoverage.Analyze([
            new(DbPath("nas"), "photos", "NAS"),
            new(DbPath("nas"), "archive", "nas"),
            new(DbPath("disk"), "photos", "External"),
        ]);
        Assert.Equal(2, report.Devices.Count);
        Assert.Equal(1, report.SingleDeviceContent);
        Assert.Equal(1, report.ContentOnEveryDevice);
        var shared = Assert.Single(report.Content, content => content.Digest == Digest("same"));
        Assert.Equal(2, shared.DeviceCount);
        Assert.Equal(4, shared.Locations.Count);
        Assert.All(report.Sources, source => Assert.False(source.LocallyAvailable));
        Assert.Equal(before, File.ReadAllBytes(DbPath("nas")));
    }

    [Fact]
    public void Missing_Stale_And_Incomplete_Hashes_Do_Not_Claim_Coverage()
    {
        Seed(
            "fresh",
            "r",
            ScanStatus.Completed,
            ("fresh", "content", true),
            ("nohash", "content", false),
            ("stale", "content", true)
        );
        Seed("incomplete", "r", ScanStatus.Incomplete, ("other", "content", true));
        using (var db = Database.OpenWritable(DbPath("fresh"), pooling: false))
        {
            var entry = db.GetFileEntry("r", "stale")!;
            db.UpsertFileEntry(entry with { ModifiedUtc = "2026-01-02T00:00:00Z" });
        }
        var report = BackupCoverage.Analyze([
            new(DbPath("fresh"), "r", "One"),
            new(DbPath("incomplete"), "r", "Two"),
        ]);
        Assert.Equal(1, Assert.Single(report.Content).DeviceCount);
        Assert.Equal(3, report.Unverified.Count);
        Assert.Equal(0, report.ContentOnEveryDevice);
        Assert.Contains(report.Unverified, file => file.Reason.Contains("complete latest scan"));
    }

    [Fact]
    public void Links_Missing_Entries_And_Stored_Exclusions_Are_Skipped()
    {
        Seed(
            "skip",
            "r",
            ScanStatus.Completed,
            ("keep", "content", true),
            ("missing", "other", true),
            ("exclude/file", "other", true),
            ("link", "other", true),
            ("link/child", "other", true)
        );
        using (var db = Database.OpenWritable(DbPath("skip"), pooling: false))
        {
            db.UpsertFileEntry(
                db.GetFileEntry("r", "missing")! with
                {
                    Status = FileStatus.Missing,
                }
            );
            db.UpsertFileEntry(
                db.GetFileEntry("r", "link")! with
                {
                    EntryKind = EntryKind.DirectoryLink,
                }
            );
            db.SetExcludedPathRegexes("r", ["^exclude/"]);
        }
        var report = BackupCoverage.Analyze([new(DbPath("skip"), "r", "NAS")]);
        Assert.Equal("keep", Assert.Single(Assert.Single(report.Content).Locations).RelativePath);
        Assert.Equal(4, Assert.Single(report.Sources).SkippedEntries);
    }

    [Fact]
    public void Same_Root_Cannot_Be_Assigned_To_Two_Devices_And_Repeated_Inputs_Are_Deduplicated()
    {
        Seed("same", "r", ScanStatus.Completed, ("file", "data", true));
        var input = new CoverageInput(DbPath("same"), "r", "Drive");
        Assert.Throws<ArgumentException>(() =>
            BackupCoverage.Analyze([input, input with { DeviceId = "Other" }])
        );
        var report = BackupCoverage.Analyze([input, input]);
        Assert.Single(report.Sources);
        Assert.Single(Assert.Single(report.Content).Locations);
        Assert.Throws<ArgumentException>(() =>
            BackupCoverage.Analyze([input with { DeviceId = " " }])
        );
    }

    [Fact]
    public void Cli_Coverage_Reports_Multiple_Devices_As_Json()
    {
        Seed("one", "r", ScanStatus.Completed, ("a", "same", true));
        Seed("two", "r", ScanStatus.Completed, ("b", "same", true));
        var original = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Assert.Equal(
                0,
                Cli.Run([
                    "coverage",
                    "--inventory",
                    "NAS=" + DbPath("one"),
                    "--inventory",
                    "External=" + DbPath("two"),
                    "--json",
                ])
            );
        }
        finally
        {
            Console.SetOut(original);
        }
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(2, json.RootElement.GetProperty("devices").GetArrayLength());
        Assert.Equal(
            2,
            json.RootElement.GetProperty("content")[0].GetProperty("deviceCount").GetInt32()
        );
    }
}
