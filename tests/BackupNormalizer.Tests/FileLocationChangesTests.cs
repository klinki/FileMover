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
    public void Changed_Content_Is_Paired_And_Selected_Roots_Are_Isolated_Even_In_One_Database()
    {
        _fixture.Seed("a", ["photo.jpg"], content: "left");
        _fixture.Seed("a", ["photo.jpg"], root: "other", content: "rght");
        var report = _fixture.Analyze("a", "a", "r", "other");
        var group = Assert.Single(report.Groups);
        Assert.Equal(FileLocationChanges.ContentChanged, group.Classification);
        Assert.Equal(
            new FileContentComparison(
                4,
                4,
                LocationChangesFixture.Digest("left"),
                LocationChangesFixture.Digest("rght")
            ),
            group.ContentComparison
        );
        Assert.Equal("photo.jpg", group.BeforePath);
        Assert.Equal("photo.jpg", group.AfterPath);
        Assert.Null(group.Digest);
        Assert.Single(FileLocationChanges.Filter(report, "quick-differences"));
        Assert.Single(FileLocationChanges.Filter(report, "content-changed"));
        Assert.Empty(FileLocationChanges.Filter(report, "changes"));
    }

    [Theory]
    [InlineData("longer", true, true, FileLocationChanges.ContentChanged)]
    [InlineData("longer", false, false, FileLocationChanges.ContentChanged)]
    [InlineData("longer", true, false, FileLocationChanges.ContentChanged)]
    [InlineData("rght", false, false, FileLocationChanges.Unverified)]
    [InlineData("rght", true, false, FileLocationChanges.Unverified)]
    public void Same_Path_Uses_Size_Or_Current_Hashes_Without_Inventing_Unknown_Content(
        string after,
        bool hashA,
        bool hashB,
        string classification
    )
    {
        _fixture.Seed("a", ["photos/image.jpg"], content: "left", hashed: hashA);
        _fixture.Seed("b", ["photos/image.jpg"], content: after, hashed: hashB);
        var report = _fixture.Analyze();
        var group = Assert.Single(report.Groups);
        Assert.Equal(classification, group.Classification);
        Assert.Equal(2, group.Locations.Count);
        Assert.Equal(
            new FileContentComparison(
                4,
                after.Length,
                hashA ? LocationChangesFixture.Digest("left") : null,
                hashB ? LocationChangesFixture.Digest(after) : null
            ),
            group.ContentComparison
        );
        Assert.Equal((hashA ? 0 : 1) + (hashB ? 0 : 1), report.UnverifiedFiles);
        Assert.Equal(
            classification == FileLocationChanges.ContentChanged ? 1 : 0,
            FileLocationChanges.Filter(report, "quick-differences").Length
        );
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("invalid")]
    [InlineData("scan-error")]
    public void Same_Path_Rejects_Stale_Invalid_And_Scan_Error_Evidence(string error)
    {
        _fixture.Seed("a", ["image.jpg"], content: "left");
        string b = _fixture.Seed("b", ["image.jpg"], content: "rght");
        using (var db = Database.OpenWritable(b, pooling: false))
        {
            var entry = db.GetFileEntry("r", "image.jpg")!;
            if (error == "invalid")
                db.UpsertHash(
                    new FileHashRow(
                        entry.Id,
                        "sha256",
                        new string('z', 64),
                        4,
                        LocationChangesFixture.Modified,
                        LocationChangesFixture.Modified,
                        HashState.Ok
                    )
                );
            else
                db.UpsertFileEntry(
                    error == "stale"
                        ? entry with
                        {
                            ModifiedUtc = "2026-01-02T00:00:00Z",
                        }
                        : entry with
                        {
                            Size = 42,
                            Status = FileStatus.ScanError,
                            Error = "Access denied",
                        }
                );
        }
        var group = Assert.Single(_fixture.Analyze().Groups);
        Assert.Equal(FileLocationChanges.Unverified, group.Classification);
        Assert.Null(group.ContentComparison!.AfterDigest);
        Assert.NotNull(group.VerificationReason);
        if (error == "scan-error")
            Assert.Contains("Access denied", group.VerificationReason);
    }

    [Theory]
    [InlineData("photos/album-a/01.jpg", "photos/album-b/01.jpg")]
    [InlineData("old/gallery-us-in-vienna.zip", "new/gallery-us-in-vienna.zip")]
    public void Same_Filename_In_Different_Folders_Is_Not_A_Content_Comparison(
        string pathA,
        string pathB
    )
    {
        _fixture.Seed("a", [pathA], content: "left");
        _fixture.Seed("b", [pathB], content: "rght");
        var report = _fixture.Analyze();
        Assert.Equal(2, report.Groups.Count);
        Assert.All(report.Groups, group => Assert.Null(group.ContentComparison));
        Assert.Empty(FileLocationChanges.Filter(report, "content-changed"));
    }

    [Theory]
    [InlineData("insensitive", "insensitive", 1)]
    [InlineData("insensitive", "sensitive", 2)]
    [InlineData("sensitive", "insensitive", 2)]
    public void Content_Comparison_Respects_Both_Roots_Case_Rules_And_Normalizes_Separators(
        string aCase,
        string bCase,
        int count
    )
    {
        _fixture.Seed("a", ["Photos\\Image.jpg"], content: "left", caseSensitivity: aCase);
        _fixture.Seed("b", ["photos/image.jpg"], content: "rght", caseSensitivity: bCase);
        var report = _fixture.Analyze();
        Assert.Equal(count, report.Groups.Count);
        Assert.Equal(
            count == 1 ? 1 : 0,
            FileLocationChanges.Filter(report, "content-changed").Length
        );
        Assert.Equal(
            "Photos/Image.jpg",
            Assert
                .Single(report.Groups.SelectMany(g => g.Locations), l => l.Side == "A")
                .RelativePath
        );
    }

    [Fact]
    public void Replacement_Still_Reports_Moved_Content_And_Keeps_All_Copies_For_Uniqueness()
    {
        _fixture.Seed("a", ["image.jpg"], content: "left");
        _fixture.Seed("b", ["moved/image.jpg"], content: "left");
        _fixture.Seed("b", ["image.jpg"], content: "rght");
        var report = _fixture.Analyze();
        Assert.Equal(2, report.Groups.Count);
        Assert.Single(report.Groups, g => g.Classification == FileLocationChanges.ContentChanged);
        Assert.Equal(
            "moved/image.jpg",
            Assert
                .Single(report.Groups, g => g.Classification == FileLocationChanges.Moved)
                .AfterPath
        );

        _fixture.Seed("a", ["original-copy.jpg"], content: "left");
        report = _fixture.Analyze();
        var ambiguous = Assert.Single(
            report.Groups,
            g => g.Classification == FileLocationChanges.Ambiguous
        );
        Assert.Equal(3, ambiguous.Locations.Count);
        Assert.DoesNotContain(report.Groups, g => g.Classification == FileLocationChanges.Moved);
        Assert.Null(ambiguous.BeforePath);
    }

    [Fact]
    public void Pairing_One_Sided_Versions_Preserves_Unpaired_Copies_And_Reverses_Metadata()
    {
        _fixture.Seed("a", ["image.jpg", "extra.jpg"], content: "left");
        _fixture.Seed("b", ["image.jpg"], content: "longer");
        var report = _fixture.Analyze();
        Assert.Equal(2, report.Groups.Count);
        Assert.Equal(
            "extra.jpg",
            Assert
                .Single(
                    Assert
                        .Single(report.Groups, g => g.Classification == FileLocationChanges.OnlyInA)
                        .Locations
                )
                .RelativePath
        );
        var reversed = Assert.Single(
            _fixture.Analyze("b", "a").Groups,
            g => g.Classification == FileLocationChanges.ContentChanged
        );
        Assert.Equal(
            new FileContentComparison(
                6,
                4,
                LocationChangesFixture.Digest("longer"),
                LocationChangesFixture.Digest("left")
            ),
            reversed.ContentComparison
        );
    }

    [Fact]
    public void Content_Exports_Preserve_Per_Side_Sizes_And_Hashes()
    {
        _fixture.Seed("a", ["image.jpg"], content: "left");
        _fixture.Seed("b", ["image.jpg"], content: "longer", hashed: false);
        var report = _fixture.Analyze();
        using var jsonOutput = new StringWriter();
        LocationChangesExport.Write(jsonOutput, report, "json");
        using var json = JsonDocument.Parse(jsonOutput.ToString());
        Assert.Equal("Quick differences", json.RootElement.GetProperty("filter").GetString());
        var group = json.RootElement.GetProperty("groups")[0];
        Assert.Equal(JsonValueKind.Null, group.GetProperty("digest").ValueKind);
        var comparison = group.GetProperty("contentComparison");
        Assert.Equal(4, comparison.GetProperty("beforeSize").GetInt64());
        Assert.Equal(6, comparison.GetProperty("afterSize").GetInt64());
        Assert.Equal(
            LocationChangesFixture.Digest("left"),
            comparison.GetProperty("beforeDigest").GetString()
        );
        Assert.Equal(JsonValueKind.Null, comparison.GetProperty("afterDigest").ValueKind);
        using var csvOutput = new StringWriter();
        LocationChangesExport.Write(csvOutput, report, "csv");
        using var parser = new TextFieldParser(new StringReader(csvOutput.ToString()))
        {
            HasFieldsEnclosedInQuotes = true,
        };
        parser.SetDelimiters(",");
        parser.ReadFields();
        var before = parser.ReadFields()!;
        var after = parser.ReadFields()!;
        Assert.Equal("A", before[2]);
        Assert.Equal("4", before[7]);
        Assert.Equal(LocationChangesFixture.Digest("left"), before[8]);
        Assert.Equal("B", after[2]);
        Assert.Equal("6", after[7]);
        Assert.Equal("", after[8]);
        Assert.True(parser.EndOfData);
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
            _fixture.Seed("a", ["image.jpg"], content: "left");
            _fixture.Seed("b", ["image.jpg"], content: "longer");
            output.GetStringBuilder().Clear();
            Assert.Equal(0, Cli.Run([.. args, "--json"]));
            using (var changed = JsonDocument.Parse(output.ToString()))
            {
                Assert.Equal(
                    "Quick differences",
                    changed.RootElement.GetProperty("filter").GetString()
                );
                Assert.Contains(
                    changed.RootElement.GetProperty("groups").EnumerateArray(),
                    g =>
                        g.GetProperty("classification").GetString()
                        == FileLocationChanges.ContentChanged
                );
            }
            output.GetStringBuilder().Clear();
            Assert.Equal(0, Cli.Run([.. args, "--filter", "content-changed"]));
            Assert.Contains("Content changed | 4 → 6 bytes", output.ToString());
            Assert.Contains(LocationChangesFixture.Digest("left"), output.ToString());
            Assert.Contains(LocationChangesFixture.Digest("longer"), output.ToString());
            output.GetStringBuilder().Clear();
            Assert.Equal(0, Cli.Run([.. args, "--filter", "changes", "--json"]));
            using (var locations = JsonDocument.Parse(output.ToString()))
                Assert.DoesNotContain(
                    locations.RootElement.GetProperty("groups").EnumerateArray(),
                    g =>
                        g.GetProperty("classification").GetString()
                        == FileLocationChanges.ContentChanged
                );
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
