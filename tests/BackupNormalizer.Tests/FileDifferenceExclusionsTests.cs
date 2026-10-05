using System.Text.Json;
using BackupNormalizer;
using Microsoft.VisualBasic.FileIO;

namespace BackupNormalizer.Tests;

[Collection("Console")]
public sealed class FileDifferenceExclusionsTests : IDisposable
{
    private readonly LocationChangesFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private LocationChangesReport Analyze(params string[] paths) =>
        FileLocationChanges.Analyze(
            new(_fixture.PathFor("a"), "r"),
            new(_fixture.PathFor("b"), "r"),
            new FileDifferenceOptions(true, ExcludedPaths: paths)
        );

    [Fact]
    public void Exclusions_Apply_Before_Content_Location_Filename_And_Duplicate_Computation()
    {
        _fixture.Seed("a", ["old/movie.mp4", "cache/copy.mp4"]);
        _fixture.Seed("b", ["new/movie.mp4"]);
        _fixture.Seed("a", ["cache/image.jpg", "cache/old/gallery.zip"], content: "left");
        _fixture.Seed("b", ["cache/image.jpg", "cache/new/gallery.zip"], content: "rght");
        _fixture.Seed("b", ["cache/unknown"], hashed: false);
        var beforeA = File.ReadAllBytes(_fixture.PathFor("a"));
        var beforeB = File.ReadAllBytes(_fixture.PathFor("b"));

        var report = Analyze("cache");

        var move = Assert.Single(report.Groups);
        Assert.Equal(FileLocationChanges.Moved, move.Classification);
        Assert.Equal("old/movie.mp4", move.BeforePath);
        Assert.Equal("new/movie.mp4", move.AfterPath);
        Assert.Empty(report.FilenameGroups);
        Assert.Empty(report.DuplicatesA);
        Assert.Empty(report.DuplicatesB);
        Assert.Empty(report.UnverifiedLocations);
        Assert.Equal(0, report.UnverifiedFiles);
        Assert.Equal(1, report.A.Files);
        Assert.Equal(1, report.B.Files);
        Assert.Equal(3, report.A.SkippedEntries);
        Assert.Equal(3, report.B.SkippedEntries);
        Assert.Equal(beforeA, File.ReadAllBytes(_fixture.PathFor("a")));
        Assert.Equal(beforeB, File.ReadAllBytes(_fixture.PathFor("b")));
    }

    [Fact]
    public void Literal_Paths_Normalize_Separators_And_Preserve_Sibling_Prefixes()
    {
        string[] paths =
        [
            "cache/image",
            "cache-old/image",
            "albums [old]/image",
            "albums old/image",
            "single.mp4",
            "single.mp4.backup",
        ];
        _fixture.Seed("a", paths, content: "left");
        _fixture.Seed("b", paths, content: "rght");

        var report = Analyze(" ./cache\\ ", "albums [old]/", "single.mp4", "cache");

        Assert.Equal(new[] { "albums [old]", "cache", "single.mp4" }, report.ExcludedPaths);
        Assert.Equal(
            new[] { "albums old/image", "cache-old/image", "single.mp4.backup" },
            report.Groups.Select(g => g.BeforePath).Order(StringComparer.Ordinal)
        );
        Assert.All(
            report.Groups,
            g => Assert.Equal(FileLocationChanges.ContentChanged, g.Classification)
        );
    }

    [Theory]
    [InlineData("insensitive", "insensitive", 0)]
    [InlineData("insensitive", "sensitive", 1)]
    [InlineData("sensitive", "insensitive", 1)]
    [InlineData("sensitive", "sensitive", 1)]
    public void Exclusions_Use_The_Report_Path_Case_Rules(string aCase, string bCase, int remaining)
    {
        _fixture.Seed("a", ["Cache/image"], content: "left", caseSensitivity: aCase);
        _fixture.Seed("b", ["Cache/image"], content: "rght", caseSensitivity: bCase);
        Assert.Equal(remaining, Analyze("cache").Groups.Count);
    }

    [Fact]
    public void Duplicate_Counts_And_Savings_Use_Only_Included_Copies_And_Stored_Exclusions_Still_Apply()
    {
        string a = _fixture.Seed("a", ["first", "second", "cache/third", "stored/fourth"]);
        _fixture.Seed("b", ["new"]);
        using (var db = Database.OpenWritable(a, pooling: false))
            db.SetExcludedPathRegexes("r", ["^stored$"]);

        var report = Analyze("cache");

        var duplicate = Assert.Single(report.DuplicatesA);
        Assert.Equal(2, duplicate.Copies);
        Assert.Equal(4, duplicate.PotentialSavingsBytes);
        Assert.Equal(new[] { "first", "second" }, duplicate.Locations.Select(l => l.RelativePath));
        Assert.Equal(FileLocationChanges.Ambiguous, Assert.Single(report.Groups).Classification);
        Assert.Equal(2, report.A.SkippedEntries);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("/")]
    [InlineData("/cache")]
    [InlineData("C:\\cache")]
    [InlineData("\\\\server\\share")]
    [InlineData("../cache")]
    [InlineData("cache/../other")]
    [InlineData("cache/./other")]
    [InlineData("cache/*")]
    [InlineData("cache?")]
    public void Invalid_Exclusions_Are_Rejected_Before_Opening_Inventories(string path) =>
        Assert.Throws<ArgumentException>(() => Analyze(path));

    [Theory]
    [InlineData("all")]
    [InlineData("filename-differences")]
    [InlineData("duplicates-in-a")]
    public void Exports_Retain_Exclusion_Metadata_Even_When_Empty(string filter)
    {
        _fixture.Seed("a", ["cache/old"]);
        _fixture.Seed("b", ["cache/new"]);
        var report = Analyze("cache", "one,\"two\"");
        using var output = new StringWriter();
        LocationChangesExport.Write(output, report, "json", filter);
        using (var json = JsonDocument.Parse(output.ToString()))
            Assert.Equal(
                report.ExcludedPaths,
                json.RootElement.GetProperty("excludedPaths")
                    .EnumerateArray()
                    .Select(p => p.GetString())
            );
        output.GetStringBuilder().Clear();
        LocationChangesExport.Write(output, report, "csv", filter);
        using var parser = new TextFieldParser(new StringReader(output.ToString()))
        {
            HasFieldsEnclosedInQuotes = true,
        };
        parser.SetDelimiters(",");
        var header = parser.ReadFields()!;
        var row = parser.ReadFields()!;
        Assert.Equal(header.Length, row.Length);
        Assert.Equal(
            "[\"cache\",\"one,\\u0022two\\u0022\"]",
            row[Array.IndexOf(header, "ExcludedPaths")]
        );
        Assert.True(parser.EndOfData);
    }

    [Fact]
    public void Cli_Accepts_Repeated_Exclusions_And_Rejects_Missing_Values()
    {
        string a = _fixture.Seed("a", ["cache/image", "single.mp4", "keep"], content: "left");
        string b = _fixture.Seed("b", ["cache/image", "single.mp4", "keep"], content: "rght");
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
            Assert.Equal(
                0,
                Cli.Run([
                    .. args,
                    "--exclude-path",
                    "cache",
                    "--exclude-path",
                    "single.mp4",
                    "--json",
                ])
            );
            using var json = JsonDocument.Parse(output.ToString());
            Assert.Equal(
                "keep",
                Assert
                    .Single(json.RootElement.GetProperty("groups").EnumerateArray())
                    .GetProperty("beforePath")
                    .GetString()
            );
            Assert.Equal(2, json.RootElement.GetProperty("excludedPaths").GetArrayLength());
            Assert.Equal(2, Cli.Run([.. args, "--exclude-path"]));
            Assert.Equal(2, Cli.Run([.. args, "--exclude-path", "--json"]));
            Assert.Equal(2, Cli.Run([.. args, "--exclude-path", "../cache"]));
        }
        finally
        {
            Console.SetOut(previous);
        }
    }
}
