using System.Text.Json;
using BackupNormalizer;
using Microsoft.VisualBasic.FileIO;

namespace BackupNormalizer.Tests;

[Collection("Console")]
public sealed class GroupedFileReportsTests : IDisposable
{
    private readonly LocationChangesFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private LocationChangesReport Analyze(bool enabled = true, string extensions = ".zip,.mp4") =>
        FileLocationChanges.Analyze(
            new(_fixture.PathFor("a"), "r"),
            new(_fixture.PathFor("b"), "r"),
            new FileDifferenceOptions(enabled, extensions)
        );

    [Fact]
    public void Repeated_Filenames_Keep_Shared_And_Added_Versions_With_All_Copies()
    {
        _fixture.Seed(
            "a",
            ["camera/video.mp4", "archive/video.mp4", "backup/video.mp4"],
            content: "left"
        );
        _fixture.Seed("b", ["archive/video.mp4", "backup/video.mp4"], content: "left");
        _fixture.Seed("b", ["edited/video.mp4"], content: "rght");
        var beforeA = File.ReadAllBytes(_fixture.PathFor("a"));
        var beforeB = File.ReadAllBytes(_fixture.PathFor("b"));
        var report = Analyze();
        var family = Assert.Single(report.FilenameGroups);
        Assert.Equal("video.mp4", family.Filename);
        Assert.Equal(1, family.VerifiedVersionsA);
        Assert.Equal(2, family.VerifiedVersionsB);
        Assert.Equal(3, family.CopiesA);
        Assert.Equal(3, family.CopiesB);
        var shared = Assert.Single(
            family.Versions,
            v => v.Digest == LocationChangesFixture.Digest("left")
        );
        Assert.Equal(3, shared.CopiesA);
        Assert.Equal(2, shared.CopiesB);
        Assert.Equal("Same content in A and B", shared.State);
        Assert.Equal(
            "Content only in B",
            Assert.Single(family.Versions, v => v.CopiesA == 0).State
        );
        Assert.Equal(6, family.Versions.Sum(v => v.Locations.Count));
        Assert.Equal(3, Assert.Single(report.DuplicatesA).Copies);
        Assert.Equal(2, Assert.Single(report.DuplicatesB).Copies);
        Assert.Equal(beforeA, File.ReadAllBytes(_fixture.PathFor("a")));
        Assert.Equal(beforeB, File.ReadAllBytes(_fixture.PathFor("b")));
    }

    [Fact]
    public void Several_Content_Versions_Are_Sets_Without_Guessed_Pairs()
    {
        _fixture.Seed("a", ["one/movie.mp4", "two/movie.mp4"], content: "shared");
        _fixture.Seed("a", ["three/movie.mp4", "four/movie.mp4"], content: "before");
        _fixture.Seed("b", ["one/movie.mp4", "two/movie.mp4", "five/movie.mp4"], content: "shared");
        _fixture.Seed(
            "b",
            ["six/movie.mp4", "seven/movie.mp4", "eight/movie.mp4", "nine/movie.mp4"],
            content: "after"
        );
        var family = Assert.Single(Analyze().FilenameGroups);
        Assert.Equal(3, family.Versions.Count);
        Assert.Equal(2, family.VerifiedVersionsA);
        Assert.Equal(2, family.VerifiedVersionsB);
        Assert.Equal(11, family.Versions.Sum(v => v.Locations.Count));
        Assert.Contains(family.Versions, v => v.State == "Content only in A" && v.CopiesA == 2);
        Assert.Contains(family.Versions, v => v.State == "Content only in B" && v.CopiesB == 4);
    }

    [Fact]
    public void Matching_Is_Opt_In_And_Extensions_Only_Constrain_Filename_Candidates()
    {
        _fixture.Seed("a", ["old/gallery.zip", "old/01.jpg", "extra/copy.txt"], content: "left");
        _fixture.Seed("b", ["new/gallery.zip", "new/01.jpg"], content: "rght");
        Assert.Empty(Analyze(false).FilenameGroups);
        var report = Analyze(extensions: "ZIP");
        Assert.Equal("gallery.zip", Assert.Single(report.FilenameGroups).Filename);
        Assert.Equal(3, Assert.Single(report.DuplicatesA).Copies);
        Assert.Equal(new[] { ".zip" }, report.FilenameExtensions);
        Assert.Equal(2, Analyze(extensions: "zip,jpg").FilenameGroups.Count);
    }

    [Fact]
    public void Same_Path_Changes_And_Copy_Only_Changes_Do_Not_Add_Filename_Candidates()
    {
        _fixture.Seed("a", ["movie.mp4"], content: "left");
        _fixture.Seed("b", ["movie.mp4"], content: "rght");
        Assert.Empty(Analyze().FilenameGroups);
        Assert.Equal(
            FileLocationChanges.ContentChanged,
            Assert.Single(Analyze().Groups).Classification
        );
        _fixture.Seed("b", ["movie.mp4", "copy/movie.mp4"], content: "left");
        Assert.Empty(Analyze().FilenameGroups);
        Assert.Equal(FileLocationChanges.Copied, Assert.Single(Analyze().Groups).Classification);
        Assert.Equal(2, Assert.Single(Analyze().DuplicatesB).Copies);
    }

    [Theory]
    [InlineData("insensitive", "insensitive", 1)]
    [InlineData("insensitive", "sensitive", 0)]
    [InlineData("sensitive", "insensitive", 0)]
    public void Filename_Case_Uses_Both_Roots_And_Extensions_Ignore_Case(
        string aCase,
        string bCase,
        int expected
    )
    {
        _fixture.Seed("a", ["old\\MOVIE.MP4"], content: "left", caseSensitivity: aCase);
        _fixture.Seed("b", ["new/movie.mp4"], content: "rght", caseSensitivity: bCase);
        Assert.Equal(expected, Analyze().FilenameGroups.Count);
    }

    [Fact]
    public void Unknown_Files_Stay_Separate_And_Never_Count_As_Confirmed_Duplicates()
    {
        _fixture.Seed("a", ["one/movie.mp4", "two/movie.mp4"], hashed: false);
        _fixture.Seed("b", ["three/movie.mp4", "four/movie.mp4", "five/movie.mp4"], hashed: false);
        var report = Analyze();
        var family = Assert.Single(report.FilenameGroups);
        Assert.Equal(5, family.UnverifiedFiles);
        Assert.Equal(0, family.VerifiedVersionsA);
        Assert.Equal(0, family.VerifiedVersionsB);
        Assert.Equal(5, family.Versions.Count);
        Assert.All(
            family.Versions,
            v =>
            {
                Assert.Null(v.Digest);
                Assert.Single(v.Locations);
            }
        );
        Assert.Empty(report.DuplicatesA);
        Assert.Empty(report.DuplicatesB);
        Assert.Equal(5, report.UnverifiedLocations.Count);
    }

    [Fact]
    public void Stale_And_Scan_Error_Entries_Cannot_Join_Verified_Duplicate_Groups()
    {
        string a = _fixture.Seed("a", ["one/movie.mp4", "two/movie.mp4", "three/movie.mp4"]);
        _fixture.Seed("b", ["new/movie.mp4"], content: "rght");
        using (var db = Database.OpenWritable(a, pooling: false))
        {
            db.UpsertFileEntry(
                db.GetFileEntry("r", "two/movie.mp4")! with
                {
                    ModifiedUtc = "2026-02-01T00:00:00Z",
                }
            );
            db.UpsertFileEntry(
                db.GetFileEntry("r", "three/movie.mp4")! with
                {
                    Status = FileStatus.ScanError,
                    Error = "Access denied",
                }
            );
        }
        var report = Analyze();
        Assert.Empty(report.DuplicatesA);
        Assert.Equal(2, report.UnverifiedLocations.Count);
        Assert.Equal(2, Assert.Single(report.FilenameGroups).UnverifiedFiles);
        Assert.Contains(report.UnverifiedLocations, f => f.Reason == "Access denied");
    }

    [Fact]
    public void Duplicates_Use_Complete_Locations_And_Never_Combine_Snapshots()
    {
        _fixture.Seed("a", ["movie.mp4", "other.txt", "copy.jpg"], content: "left");
        _fixture.Seed("b", ["movie.mp4"], content: "rght");
        var report = Analyze();
        var group = Assert.Single(report.DuplicatesA);
        Assert.Equal(3, group.Copies);
        Assert.Equal(2, group.ExtraCopies);
        Assert.Equal(8, group.PotentialSavingsBytes);
        Assert.Contains(group.Locations, l => l.RelativePath == "movie.mp4");
        Assert.Empty(report.DuplicatesB);
        _fixture.Seed("a", ["single.mp4"], root: "single");
        var same = FileLocationChanges.Analyze(
            new(_fixture.PathFor("a"), "single"),
            new(_fixture.PathFor("a"), "single")
        );
        Assert.Empty(same.DuplicatesA);
        Assert.Empty(same.DuplicatesB);
    }

    [Fact]
    public void Excluded_Missing_And_Linked_Files_Are_Absent_From_All_Grouped_Views()
    {
        string a = _fixture.Seed(
            "a",
            [
                "old/movie.mp4",
                "copy.txt",
                "excluded/movie.mp4",
                "missing/movie.mp4",
                "link",
                "link/movie.mp4",
            ]
        );
        string b = _fixture.Seed("b", ["new/movie.mp4", "excluded/movie.mp4"], content: "rght");
        using (var db = Database.OpenWritable(a, pooling: false))
        {
            db.UpsertFileEntry(
                db.GetFileEntry("r", "missing/movie.mp4")! with
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
        }
        using (var db = Database.OpenWritable(b, pooling: false))
            db.SetExcludedPathRegexes("r", ["^excluded/"]);
        var report = Analyze();
        Assert.Equal(2, Assert.Single(report.DuplicatesA).Copies);
        Assert.Equal(2, Assert.Single(report.FilenameGroups).Versions.Sum(v => v.Locations.Count));
        Assert.Empty(report.DuplicatesB);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..zip")]
    [InlineData("*.mp4")]
    [InlineData("folder/zip")]
    [InlineData("tar.gz")]
    public void Enabled_Matching_Rejects_Invalid_Extension_Lists(string extensions) =>
        Assert.Throws<ArgumentException>(() => Analyze(extensions: extensions));

    [Fact]
    public void Extension_Lists_Are_Normalized_And_Deduplicated() =>
        Assert.Equal(
            new[] { ".mp4", ".zip" },
            GroupedFileReports.NormalizeExtensions(" ZIP; .mp4\tzip\nMP4 ")
        );

    [Fact]
    public void Large_Repeated_Family_Keeps_Linear_Location_Counts_And_Cancellation()
    {
        const int count = 2000;
        _fixture.Seed(
            "a",
            Enumerable.Range(0, count).Select(i => $"a{i}/movie.mp4").ToArray(),
            content: "left"
        );
        _fixture.Seed(
            "b",
            Enumerable.Range(0, count).Select(i => $"b{i}/movie.mp4").ToArray(),
            content: "rght"
        );
        var family = Assert.Single(Analyze().FilenameGroups);
        Assert.Equal(2, family.Versions.Count);
        Assert.Equal(count * 2, family.Versions.Sum(v => v.Locations.Count));
        using var token = new CancellationTokenSource();
        token.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            FileLocationChanges.Analyze(
                new(_fixture.PathFor("a"), "r"),
                new(_fixture.PathFor("b"), "r"),
                new FileDifferenceOptions(true),
                token.Token
            )
        );
    }

    [Fact]
    public void Grouped_Exports_Preserve_Versions_Paths_Options_And_Side_Specific_Savings()
    {
        _fixture.Seed("a", ["old/a,\"film\".mp4", "copy/a,\"film\".mp4"], content: "left");
        _fixture.Seed("b", ["new/a,\"film\".mp4"], content: "rght");
        _fixture.Seed("b", ["unknown.txt"], hashed: false);
        var report = Analyze();
        using var output = new StringWriter();
        LocationChangesExport.Write(output, report, "json", "filename-differences");
        using (var json = JsonDocument.Parse(output.ToString()))
        {
            Assert.True(json.RootElement.GetProperty("filenameMatchingEnabled").GetBoolean());
            Assert.Equal(
                2,
                json.RootElement.GetProperty("filenameGroups")[0]
                    .GetProperty("versions")
                    .GetArrayLength()
            );
            Assert.Equal("A → B", json.RootElement.GetProperty("direction").GetString());
        }
        output.GetStringBuilder().Clear();
        LocationChangesExport.Write(output, report, "csv", "filename-differences");
        using (
            var parser = new TextFieldParser(new StringReader(output.ToString()))
            {
                HasFieldsEnclosedInQuotes = true,
            }
        )
        {
            parser.SetDelimiters(",");
            var header = parser.ReadFields()!;
            var rows = new List<string[]>();
            while (!parser.EndOfData)
                rows.Add(parser.ReadFields()!);
            Assert.Equal(3, rows.Count);
            Assert.All(
                rows,
                row =>
                {
                    Assert.Equal(header.Length, row.Length);
                    Assert.Equal("a,\"film\".mp4", row[2]);
                    Assert.Equal("A → B", row[18]);
                }
            );
            Assert.Contains(rows, row => row[6] == "old/a,\"film\".mp4");
        }
        output.GetStringBuilder().Clear();
        LocationChangesExport.Write(output, report, "json", "duplicates-in-a");
        using (var json = JsonDocument.Parse(output.ToString()))
        {
            var group = json.RootElement.GetProperty("duplicateGroups")[0];
            Assert.Equal("A", group.GetProperty("side").GetString());
            Assert.Equal(2, group.GetProperty("copies").GetInt32());
            Assert.Equal(4, group.GetProperty("potentialSavingsBytes").GetInt64());
            Assert.Empty(json.RootElement.GetProperty("unverifiedLocations").EnumerateArray());
        }
        output.GetStringBuilder().Clear();
        LocationChangesExport.Write(output, report, "json", "duplicates-in-b");
        using (var json = JsonDocument.Parse(output.ToString()))
        {
            Assert.Empty(json.RootElement.GetProperty("duplicateGroups").EnumerateArray());
            Assert.Single(json.RootElement.GetProperty("unverifiedLocations").EnumerateArray());
        }
        output.GetStringBuilder().Clear();
        LocationChangesExport.Write(output, report, "csv", "duplicates-in-b");
        Assert.Contains("Unverified", output.ToString());
        Assert.DoesNotContain("unknown.txt", Export(report, "duplicates-in-a"));
        Assert.Contains(
            "Report metadata",
            Export(report with { DuplicatesA = [] }, "duplicates-in-a")
        );
        Assert.Throws<ArgumentException>(() => Export(Analyze(false), "filename-differences"));
        Assert.Throws<ArgumentException>(() =>
            FileLocationChanges.Filter(report, "filename-differences")
        );
    }

    private static string Export(LocationChangesReport report, string filter)
    {
        using var output = new StringWriter();
        LocationChangesExport.Write(output, report, "csv", filter);
        return output.ToString();
    }

    [Fact]
    public void Cli_Requires_Explicit_Filename_Mode_And_Exposes_Grouped_Views()
    {
        string a = _fixture.Seed("a", ["old/movie.mp4", "copy/movie.mp4"], content: "left");
        string b = _fixture.Seed("b", ["new/movie.mp4"], content: "rght");
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
        var previous = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Assert.Equal(2, Cli.Run([.. args, "--filter", "filename-differences", "--json"]));
            output.GetStringBuilder().Clear();
            Assert.Equal(
                0,
                Cli.Run([
                    .. args,
                    "--match-filenames",
                    "--extensions",
                    "mp4",
                    "--filter",
                    "filename-differences",
                    "--json",
                ])
            );
            using var json = JsonDocument.Parse(output.ToString());
            Assert.Single(json.RootElement.GetProperty("filenameGroups").EnumerateArray());
            output.GetStringBuilder().Clear();
            Assert.Equal(
                0,
                Cli.Run([.. args, "--match-filenames", "--filter", "filename-differences"])
            );
            Assert.Contains("Filename-based candidate", output.ToString());
            output.GetStringBuilder().Clear();
            Assert.Equal(0, Cli.Run([.. args, "--filter", "duplicates-in-a"]));
            Assert.Contains("2 copies", output.ToString());
            Assert.Contains("potential savings: 4 bytes", output.ToString());
            Assert.Equal(2, Cli.Run([.. args, "--match-filenames", "--extensions", "*", "--json"]));
        }
        finally
        {
            Console.SetOut(previous);
        }
    }
}
