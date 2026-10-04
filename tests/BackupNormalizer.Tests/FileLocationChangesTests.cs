using System.Text.Json;
using BackupNormalizer;
using Microsoft.VisualBasic.FileIO;

namespace BackupNormalizer.Tests;

[Collection("Console")]
public sealed class FileLocationChangesTests : IDisposable
{
    private readonly LocationChangesFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Theory]
    [InlineData("old/photo.jpg", "new/photo.jpg", FileLocationChanges.Moved)]
    [InlineData("photo.jpg", "photo.jpg|backup/photo.jpg", FileLocationChanges.Copied)]
    [InlineData(
        "one/photo.jpg|two/photo.jpg",
        "one/photo.jpg|two/photo.jpg|three/photo.jpg",
        FileLocationChanges.Copied
    )]
    [InlineData(
        "old/photo.jpg|kept/photo.jpg",
        "new/photo.jpg|kept/photo.jpg",
        FileLocationChanges.Ambiguous
    )]
    [InlineData(
        "one/photo.jpg|two/photo.jpg",
        "three/photo.jpg|four/photo.jpg",
        FileLocationChanges.Ambiguous
    )]
    [InlineData("photo.jpg|backup/photo.jpg", "photo.jpg", FileLocationChanges.RemovedCopies)]
    [InlineData("one|two", "three", FileLocationChanges.Ambiguous)]
    [InlineData("photo.jpg", "photo.jpg", FileLocationChanges.Unchanged)]
    [InlineData("photo.jpg", "", FileLocationChanges.OnlyInA)]
    [InlineData("", "photo.jpg", FileLocationChanges.OnlyInB)]
    public void Classifies_Complete_Location_Sets(string a, string b, string expected)
    {
        var before = a.Split('|', StringSplitOptions.RemoveEmptyEntries);
        var after = b.Split('|', StringSplitOptions.RemoveEmptyEntries);
        _fixture.Seed("a", before);
        _fixture.Seed("b", after);
        var report = _fixture.Analyze();
        var group = Assert.Single(report.Groups);
        Assert.Equal(expected, group.Classification);
        Assert.Equal(
            before.Order(StringComparer.Ordinal),
            group.Locations.Where(l => l.Side == "A").Select(l => l.RelativePath)
        );
        Assert.Equal(
            after.Order(StringComparer.Ordinal),
            group.Locations.Where(l => l.Side == "B").Select(l => l.RelativePath)
        );
        if (expected == FileLocationChanges.Moved)
        {
            Assert.Equal(a, group.BeforePath);
            Assert.Equal(b, group.AfterPath);
        }
        else
        {
            Assert.Null(group.BeforePath);
            Assert.Null(group.AfterPath);
        }
        Assert.Equal(1, report.Summary[expected]);
        Assert.Equal("/offline/same-root", report.A.RootPath);
        Assert.Equal("/offline/same-root", report.B.RootPath);
    }

    [Fact]
    public void Swapping_Direction_Reverses_Moves_And_Copy_Classifications()
    {
        _fixture.Seed("a", ["old"]);
        _fixture.Seed("b", ["new"]);
        var reversed = Assert.Single(_fixture.Analyze("b", "a").Groups);
        Assert.Equal("new", reversed.BeforePath);
        Assert.Equal("old", reversed.AfterPath);
        _fixture.Seed("a", ["retained"], root: "copies", content: "copied");
        _fixture.Seed("b", ["retained", "added"], root: "copies", content: "copied");
        Assert.Equal(
            FileLocationChanges.Copied,
            Assert.Single(_fixture.Analyze(rootA: "copies", rootB: "copies").Groups).Classification
        );
        Assert.Equal(
            FileLocationChanges.RemovedCopies,
            Assert.Single(_fixture.Analyze("b", "a", "copies", "copies").Groups).Classification
        );
    }

    [Theory]
    [InlineData("insensitive", "insensitive", FileLocationChanges.Unchanged)]
    [InlineData("insensitive", "sensitive", FileLocationChanges.Moved)]
    [InlineData("sensitive", "insensitive", FileLocationChanges.Moved)]
    public void Case_Rules_And_Cross_Platform_Separators_Are_Respected(
        string aCase,
        string bCase,
        string classification
    )
    {
        _fixture.Seed("a", ["Folder\\Photo.jpg"], caseSensitivity: aCase);
        _fixture.Seed("b", ["folder/photo.jpg"], caseSensitivity: bCase);
        var group = Assert.Single(_fixture.Analyze().Groups);
        Assert.Equal(classification, group.Classification);
        Assert.Equal("Folder/Photo.jpg", group.Locations[0].RelativePath);
    }

    [Fact]
    public void Changed_Content_Is_One_Sided_And_Selected_Roots_Are_Isolated_Even_In_One_Database()
    {
        _fixture.Seed("a", ["photo.jpg"], content: "left");
        _fixture.Seed("a", ["photo.jpg"], root: "other", content: "rght");
        var report = _fixture.Analyze("a", "a", "r", "other");
        Assert.Equal(2, report.Groups.Count);
        Assert.Contains(report.Groups, g => g.Classification == FileLocationChanges.OnlyInA);
        Assert.Contains(report.Groups, g => g.Classification == FileLocationChanges.OnlyInB);
        Assert.Empty(FileLocationChanges.Filter(report, "changes"));
    }

    [Theory]
    [InlineData(ScanStatus.Incomplete)]
    [InlineData(ScanStatus.Started)]
    public void Incomplete_Scans_Block_Analysis(string status)
    {
        _fixture.Seed("a", ["old"]);
        _fixture.Seed("b", ["new"], status: status);
        var error = Assert.Throws<InvalidOperationException>(() => _fixture.Analyze());
        Assert.Contains("Inventory B", error.Message);
        Assert.Contains("complete successful scan", error.Message);
    }

    [Fact]
    public void Missing_Stale_And_Invalid_Hashes_Cannot_Establish_Uniqueness()
    {
        _fixture.Seed("a", ["old"]);
        string b = _fixture.Seed("b", ["new", "stale", "invalid"]);
        using (var db = Database.OpenWritable(b, pooling: false))
        {
            db.UpsertFileEntry(
                db.GetFileEntry("r", "stale")! with
                {
                    ModifiedUtc = "2026-01-02T00:00:00Z",
                }
            );
            var invalid = db.GetFileEntry("r", "invalid")!;
            db.UpsertHash(
                new FileHashRow(
                    invalid.Id,
                    "sha256",
                    new string('z', 64),
                    4,
                    LocationChangesFixture.Modified,
                    LocationChangesFixture.Modified,
                    HashState.Ok
                )
            );
        }
        var report = _fixture.Analyze();
        Assert.Equal(2, report.UnverifiedFiles);
        Assert.All(
            report.Groups,
            g => Assert.Equal(FileLocationChanges.Unverified, g.Classification)
        );
        var known = Assert.Single(report.Groups, g => g.Digest != null);
        Assert.Equal(2, known.Locations.Count);
        Assert.Null(known.BeforePath);
        Assert.Contains("Same-size", known.VerificationReason);
        Assert.Empty(FileLocationChanges.Filter(report, "changes"));
        _fixture.Seed("a", ["missinghash"], root: "nohash", hashed: false);
        _fixture.Seed("b", [], root: "nohash");
        Assert.Equal(
            FileLocationChanges.Unverified,
            Assert.Single(_fixture.Analyze(rootA: "nohash", rootB: "nohash").Groups).Classification
        );
    }

    [Fact]
    public void Links_Descendants_Missing_And_Union_Of_Exclusions_Are_Skipped_Without_Modifying_Databases()
    {
        string a = _fixture.Seed(
            "a",
            ["old", "excludedA/file", "excludedB/file", "missing", "link", "link/child"]
        );
        string b = _fixture.Seed("b", ["new", "excludedA/file", "excludedB/file"]);
        using (var db = Database.OpenWritable(a, pooling: false))
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
            db.SetExcludedPathRegexes("r", ["^excludedA/"]);
        }
        using (var db = Database.OpenWritable(b, pooling: false))
            db.SetExcludedPathRegexes("r", ["^excludedB/"]);
        var beforeA = File.ReadAllBytes(a);
        var beforeB = File.ReadAllBytes(b);
        var report = _fixture.Analyze();
        Assert.Equal(FileLocationChanges.Moved, Assert.Single(report.Groups).Classification);
        Assert.Equal(5, report.A.SkippedEntries);
        Assert.Equal(2, report.B.SkippedEntries);
        Assert.Equal(beforeA, File.ReadAllBytes(a));
        Assert.Equal(beforeB, File.ReadAllBytes(b));
    }

    [Fact]
    public void Json_And_Csv_Preserve_Direction_Metadata_Filter_And_All_Ambiguous_Locations()
    {
        _fixture.Seed("a", ["old,\"name\"\n.txt", "retained"]);
        _fixture.Seed("b", ["new", "retained"]);
        var report = _fixture.Analyze();
        using var jsonOutput = new StringWriter();
        LocationChangesExport.Write(jsonOutput, report, "json", "ambiguous");
        using var json = JsonDocument.Parse(jsonOutput.ToString());
        Assert.Equal("A → B", json.RootElement.GetProperty("direction").GetString());
        Assert.Equal("Ambiguous", json.RootElement.GetProperty("filter").GetString());
        Assert.Equal(
            _fixture.PathFor("a"),
            json.RootElement.GetProperty("a")
                .GetProperty("input")
                .GetProperty("databasePath")
                .GetString()
        );
        Assert.Equal(
            4,
            json.RootElement.GetProperty("groups")[0].GetProperty("locations").GetArrayLength()
        );
        using var csvOutput = new StringWriter();
        LocationChangesExport.Write(csvOutput, report, "csv");
        using var parser = new TextFieldParser(new StringReader(csvOutput.ToString()))
        {
            HasFieldsEnclosedInQuotes = true,
        };
        parser.SetDelimiters(",");
        var header = parser.ReadFields()!;
        var rows = new List<string[]>();
        while (!parser.EndOfData)
            rows.Add(parser.ReadFields()!);
        Assert.Equal(4, rows.Count);
        Assert.All(rows, row => Assert.Equal(header.Length, row.Length));
        Assert.Contains(rows, row => row[5] == "old,\"name\"\n.txt");
        Assert.All(rows, row => Assert.Equal("A → B", row[12]));
        Assert.Single(rows.Select(row => row[0]).Distinct());
        Assert.All(
            rows,
            row =>
            {
                Assert.Equal("", row[10]);
                Assert.Equal("", row[11]);
            }
        );
    }

    [Fact]
    public void Exports_Protect_Both_Databases_And_Companions_And_Cancel_Without_Replacing_An_Existing_Report()
    {
        string a = _fixture.Seed("a", ["old"]);
        string b = _fixture.Seed("b", ["new"]);
        var report = _fixture.Analyze();
        foreach (string db in new[] { a, b })
        foreach (string suffix in new[] { "", "-wal", "-shm", "-journal" })
            Assert.Throws<ArgumentException>(() =>
                LocationChangesExport.Save(db + suffix, report, "json")
            );
        string destination = Path.Combine(_fixture.DirectoryPath, "report.json");
        File.WriteAllText(destination, "previous");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            LocationChangesExport.Save(
                destination,
                report,
                "json",
                cancellationToken: cancelled.Token
            )
        );
        Assert.Equal("previous", File.ReadAllText(destination));
        Assert.Throws<ArgumentException>(() =>
            LocationChangesExport.Save(destination, report, "invalid")
        );
        Assert.Equal("previous", File.ReadAllText(destination));
        Assert.Empty(Directory.EnumerateFiles(_fixture.DirectoryPath, ".bn-location-report-*"));
        LocationChangesExport.Save(destination, report, "json");
        using var json = JsonDocument.Parse(File.ReadAllText(destination));
        Assert.Equal(
            "Moved",
            json.RootElement.GetProperty("groups")[0].GetProperty("classification").GetString()
        );
    }

    [Fact]
    public void Analysis_Is_Deterministic_Cancellable_And_Rejects_Unknown_Roots_And_Filters()
    {
        _fixture.Seed("a", ["z", "a"]);
        _fixture.Seed("b", ["y", "b"]);
        Assert.Equal(
            _fixture.Analyze().Groups[0].Locations,
            _fixture.Analyze().Groups[0].Locations
        );
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            FileLocationChanges.Analyze(
                new(_fixture.PathFor("a"), "r"),
                new(_fixture.PathFor("b"), "r"),
                cancelled.Token
            )
        );
        Assert.Throws<InvalidOperationException>(() => _fixture.Analyze(rootA: "unknown"));
        Assert.Throws<ArgumentException>(() =>
            FileLocationChanges.Filter(_fixture.Analyze(), "unknown")
        );
    }

    [Fact]
    public void Unknown_Scan_Status_And_Scan_Error_Entries_Are_Handled_Explicitly()
    {
        string a = _fixture.Seed("a", ["old"]);
        string b = _fixture.Seed("b", ["new"]);
        using (var db = Database.OpenWritable(b, pooling: false))
        {
            db.UpsertFileEntry(
                db.GetFileEntry("r", "new")! with
                {
                    Status = FileStatus.ScanError,
                    Error = "Access denied",
                }
            );
        }
        var report = _fixture.Analyze();
        Assert.All(
            report.Groups,
            group => Assert.Equal(FileLocationChanges.Unverified, group.Classification)
        );
        Assert.Contains(report.Groups, group => group.VerificationReason == "Access denied");
        using (var db = Database.OpenWritable(a, pooling: false))
        {
            db.UpsertRoot(
                new StorageRootRow(
                    "not-scanned",
                    "Not scanned",
                    "/offline",
                    false,
                    "device",
                    "sensitive",
                    LocationChangesFixture.Modified
                )
            );
        }
        Assert.Throws<InvalidOperationException>(() => _fixture.Analyze(rootA: "not-scanned"));
    }

    [Fact]
    public void Cli_Reports_Json_And_Filtered_Csv_And_Validates_Required_Arguments()
    {
        string a = _fixture.Seed("a", ["old"]);
        string b = _fixture.Seed("b", ["new"]);
        string[] args =
        [
            "location-changes",
            "--source-db",
            a,
            "--source-root",
            "r",
            "--target-db",
            b,
            "--target-root",
            "r",
        ];
        var original = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Assert.Equal(0, Cli.Run([.. args, "--json"]));
            using var json = JsonDocument.Parse(output.ToString());
            Assert.Equal(
                "old",
                json.RootElement.GetProperty("groups")[0].GetProperty("beforePath").GetString()
            );
            output.GetStringBuilder().Clear();
            Assert.Equal(0, Cli.Run([.. args, "--format", "csv", "--filter", "copied"]));
            Assert.Equal(
                2,
                output
                    .ToString()
                    .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                    .Length
            );
            Assert.Contains("Report metadata", output.ToString());
            string exportPath = Path.Combine(_fixture.DirectoryPath, "cli.json");
            Assert.Equal(0, Cli.Run([.. args, "--output", exportPath]));
            Assert.True(File.Exists(exportPath));
            Assert.Equal(2, Cli.Run(["location-changes"]));
            Assert.Equal(2, Cli.Run([.. args, "--json", "--format", "csv"]));
            Assert.Equal(2, Cli.Run([.. args, "--output", a]));
        }
        finally
        {
            Console.SetOut(original);
        }
    }
}
