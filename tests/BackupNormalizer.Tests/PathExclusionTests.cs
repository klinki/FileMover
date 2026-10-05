using BackupNormalizer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BackupNormalizer.Tests;

public sealed class PathExclusionTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "bn-exclusions-" + Guid.NewGuid().ToString("N")
    );
    private string Root => Path.Combine(_directory, "data");
    private string DbPath => Path.Combine(_directory, "inventory.db");

    public PathExclusionTests() => Directory.CreateDirectory(Root);

    public void Dispose()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = DbPath,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                ForeignKeys = true,
            }.ToString()
        );
        Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
        Directory.Delete(_directory, true);
    }

    private Database Open(string caseSensitivity = "sensitive")
    {
        var db = Database.OpenWritable(DbPath, pooling: false);
        db.UpsertRoot(new("r", "r", Root, true, "fs", caseSensitivity, Database.UtcNow()));
        return db;
    }

    private string Write(string relative, string content = "content")
    {
        string path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Theory]
    [InlineData("cache", true)]
    [InlineData("cache/child/file.txt", true)]
    [InlineData("nested/cache/child.txt", true)]
    [InlineData("nested\\cache\\child.txt", true)]
    [InlineData("CACHE/file.txt", false)]
    [InlineData("cacheable/file.txt", false)]
    public void Regexes_Match_Normalized_Paths_And_Ancestors(string path, bool expected)
    {
        var exclusions = new PathExclusions(["(^|/)cache$"]);
        Assert.Equal(expected, exclusions.IsExcluded(path));
        Assert.True(new PathExclusions(["^cache$"], ignoreCase: true).IsExcluded("CACHE/file.txt"));
    }

    [Fact]
    public void Recursive_Scan_Prunes_Directories_And_Retires_Previously_Indexed_Paths()
    {
        Write("keep.txt");
        Write("cache/nested/skip.txt");
        Write("other/drop.tmp");
        using var db = Open();
        Assert.Equal((3, 0), new Scanner(db, usnMode: "off").ScanRoot("r"));
        var exclusions = new PathExclusions(["^cache$", "\\.tmp$"]);
        var enumerated = Scanner.EnumerateRecursive(Root, exclusions).ToList();
        Assert.Single(enumerated);
        Assert.EndsWith("keep.txt", enumerated[0].Path);
        Assert.Equal(
            (1, 0),
            new Scanner(db, usnMode: "off", excludedPathRegexes: exclusions.Patterns).ScanRoot("r")
        );
        Assert.Equal(FileStatus.Missing, db.GetFileEntry("r", "cache/nested/skip.txt")!.Status);
        Assert.Equal(FileStatus.Missing, db.GetFileEntry("r", "other/drop.tmp")!.Status);
        Assert.Equal(ScanStatus.Completed, db.LatestScanStatus("r"));
        Assert.Equal((1, 2, 0), new Scanner(db).HashNeeded("r", parallelism: 1));
        Assert.True(db.GetInventoryStatus("r").PlanningReady);
        Assert.Equal(exclusions.Patterns, db.GetExcludedPathRegexes("r"));
    }

    [Fact]
    public void Direct_Metadata_Enumeration_Skips_Excluded_Errors_And_Link_Entries()
    {
        string keep = Write("keep.txt");
        using var db = Open();
        var scanner = new Scanner(
            db,
            _ =>
                [
                    new(
                        Path.Combine(Root, "cache"),
                        true,
                        0,
                        default,
                        default,
                        true,
                        false,
                        "Access denied."
                    ),
                    new(
                        Path.Combine(Root, "cache/hidden"),
                        false,
                        0,
                        default,
                        default,
                        true,
                        true,
                        null
                    ),
                    new(
                        keep,
                        false,
                        7,
                        File.GetLastWriteTimeUtc(keep),
                        File.GetCreationTimeUtc(keep),
                        true,
                        false,
                        null
                    ),
                ],
            excludedPathRegexes: ["^cache$"]
        );
        Assert.Equal((1, 0), scanner.ScanRoot("r"));
        Assert.Single(db.ListFiles("r"));
        Assert.Empty(db.ListScanDiagnostics(db.LatestScan("r")!.Id));
    }

    [Fact]
    public void Root_Enumeration_Failures_Are_Not_Hidden_By_Exclusions()
    {
        Write("unseen.txt");
        using var db = Open();
        new Scanner(db).ScanRoot("r");
        var scanner = new Scanner(
            db,
            _ => [new(Root, true, 0, default, default, false, false, "Root enumeration denied.")],
            excludedPathRegexes: ["^cache$"]
        );
        Assert.Equal((0, 1), scanner.ScanRoot("r"));
        Assert.Equal(ScanStatus.Incomplete, db.LatestScanStatus("r"));
        Assert.Equal(FileStatus.Ok, db.GetFileEntry("r", "unseen.txt")!.Status);
        Assert.Equal(Root, Assert.Single(db.ListScanDiagnostics(db.LatestScan("r")!.Id)).Path);
    }

    [Fact]
    public void Persisted_Rules_Survive_Reopening_And_Clearing_Rehashes_Returning_Files()
    {
        Write("keep.txt");
        string excluded = Write("cache/skip.txt", "before");
        DateTime modified = File.GetLastWriteTimeUtc(excluded);
        using (var db = Open())
        {
            new Scanner(db).ScanRoot("r");
            new Scanner(db).HashNeeded("r", parallelism: 1);
            new Scanner(db, excludedPathRegexes: ["^cache$"]).ScanRoot("r");
        }
        File.WriteAllText(excluded, "after!");
        File.SetLastWriteTimeUtc(excluded, modified);
        using var reopened = Database.OpenWritable(DbPath, pooling: false);
        Assert.Equal((1, 0), new Scanner(reopened).ScanRoot("r"));
        new Scanner(reopened, excludedPathRegexes: []).ScanRoot("r");
        var returning = reopened.GetFileEntry("r", "cache/skip.txt")!;
        Assert.Equal(HashState.Stale, reopened.GetHash(returning.Id, "sha256")!.State);
        Assert.Equal((1, 1, 0), new Scanner(reopened).HashNeeded("r", parallelism: 1));
        Assert.Equal(
            HasherFactory.Create(null).HashFile(excluded, 6),
            reopened.GetHash(returning.Id, "sha256")!.Digest
        );
    }

    [Fact]
    public void Failed_Scan_Preserves_Unseen_Entries_But_Hashing_Skips_Excluded_Content()
    {
        string keep = Write("keep.txt");
        Write("cache/skip.txt");
        using var db = Open();
        new Scanner(db).ScanRoot("r");
        var scanner = new Scanner(
            db,
            _ => [new(keep, false, 0, default, default, false, false, "Metadata unavailable.")],
            excludedPathRegexes: ["^cache$"]
        );
        Assert.Equal((0, 1), scanner.ScanRoot("r"));
        Assert.Equal(FileStatus.Ok, db.GetFileEntry("r", "cache/skip.txt")!.Status);
        Assert.Equal((1, 1, 0), new Scanner(db).HashNeeded("r", parallelism: 1));
        Assert.Null(db.GetHash(db.GetFileEntry("r", "cache/skip.txt")!.Id, "sha256"));
        Assert.False(db.GetInventoryStatus("r").PlanningReady);
    }

    [Fact]
    public void Changed_Rules_Require_A_Full_Baseline_Before_Usn_Replay()
    {
        Write("keep.txt");
        Write("cache/skip.txt");
        using var db = Open();
        var journal = new UsnScanTests.FakeJournal();
        journal.Parents[1] = Root;
        int enumerations = 0;
        Scanner Create(IReadOnlyList<string>? patterns = null) =>
            new(
                db,
                _ =>
                {
                    enumerations++;
                    return Scanner.EnumerateRecursive(Root);
                },
                _ => journal,
                patterns
            );
        Create().ScanRoot("r");
        Assert.Equal(1, enumerations);
        var changed = Create(["^cache$"]);
        Assert.Equal((1, 0), changed.ScanRoot("r"));
        Assert.False(changed.LastScanWasIncremental);
        Assert.Equal(2, enumerations);
        Assert.NotNull(db.GetScanCheckpoint("r"));
        journal.State = journal.State with { NextUsn = 200 };
        journal.Records =
        [
            new(20, 1, 110, UsnReplay.Close | 1, FileAttributes.Normal, "keep.txt"),
            new(21, 1, 111, UsnReplay.Close | 1, FileAttributes.Normal, "cache"),
        ];
        var incremental = Create();
        Assert.Equal((1, 0), incremental.ScanRoot("r"));
        Assert.True(incremental.LastScanWasIncremental);
        Assert.Equal(2, enumerations);
        Assert.Equal(200, db.GetScanCheckpoint("r")!.NextUsn);
        Assert.Equal((2, 0), Create([]).ScanRoot("r"));
        Assert.Equal(3, enumerations);
    }

    [Fact]
    public void Legacy_Database_Fails_Fast_Read_Only_And_Upgrade_Preserves_Inventory()
    {
        var options = new DbContextOptionsBuilder<BackupNormalizerDbContext>()
            .UseSqlite(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                {
                    DataSource = DbPath,
                    Pooling = false,
                }.ToString()
            )
            .Options;
        using (var old = new BackupNormalizerDbContext(options))
        {
            old.GetService<IMigrator>().Migrate("20261003132247_RecordScanDiagnostics");
            old.Database.ExecuteSqlRaw(
                "INSERT INTO StorageRoot (Id,Name,Path,CreatedUtc) VALUES ('r','r','offline','before')"
            );
        }
        byte[] before = File.ReadAllBytes(DbPath);
        Assert.Throws<DatabaseNeedsMigrationException>(() =>
            Database.OpenReadOnly(DbPath, pooling: false)
        );
        Assert.Equal(before, File.ReadAllBytes(DbPath));
        using var upgraded = Database.OpenWritable(DbPath, pooling: false);
        Assert.Equal("offline", upgraded.GetRoot("r")!.Path);
        Assert.Empty(upgraded.GetExcludedPathRegexes("r"));
        Assert.Empty(upgraded.PendingMigrations());
    }

    [Fact]
    public void Invalid_Regex_Does_Not_Start_A_Scan_And_Pathological_Regex_Is_Bounded()
    {
        using var db = Open();
        Assert.Throws<ArgumentException>(() => new Scanner(db, excludedPathRegexes: ["["]));
        Assert.Null(db.LatestScan("r"));
        var exclusions = new PathExclusions(["^(a+)+$"]);
        Assert.Throws<InvalidOperationException>(() =>
            exclusions.IsExcluded(new string('a', 2000) + "!")
        );
    }

    [Fact]
    public void Planning_And_Diff_Preserve_Paths_Excluded_By_Either_Root()
    {
        Write("source/keep.txt");
        Write("source/ignore/private.txt");
        Write("source/onlytarget/new.txt");
        Write("target/ignore/duplicate.txt");
        Write("target/onlytarget/duplicate.txt");
        using var db = Open();
        db.UpsertRoot(
            new("s", "s", Path.Combine(Root, "source"), false, "fs", "sensitive", Database.UtcNow())
        );
        db.UpsertRoot(
            new("t", "t", Path.Combine(Root, "target"), true, "fs", "sensitive", Database.UtcNow())
        );
        new Scanner(db, excludedPathRegexes: ["^ignore$"]).ScanRoot("s");
        new Scanner(db, excludedPathRegexes: ["^onlytarget$"]).ScanRoot("t");
        new Scanner(db).HashNeeded(parallelism: 1);
        var result = new Planner(db).PlanFromRoots(db, "s", "t", "excluded");
        Assert.Equal(1, result.Copy);
        Assert.Equal(0, result.Move);
        Assert.Equal(0, result.Trash);
        Assert.Single(db.ListPlanOperations("excluded"));
        Assert.Equal("keep.txt", db.ListPlanOperations("excluded")[0].DestPath);
        var diff = Inventory.Diff(DbPath, "s", DbPath, "t");
        Assert.Equal(1, diff.SourceOnly);
        Assert.Equal(0, diff.TargetOnly);
        var executed = new Executor(db).Execute("excluded");
        Assert.Equal(1, executed.Completed);
        Assert.Equal(
            "content",
            File.ReadAllText(Path.Combine(Root, "target/ignore/duplicate.txt"))
        );
        Assert.Equal(
            "content",
            File.ReadAllText(Path.Combine(Root, "target/onlytarget/duplicate.txt"))
        );
        Assert.False(File.Exists(Path.Combine(Root, "target/onlytarget/new.txt")));
    }
}
