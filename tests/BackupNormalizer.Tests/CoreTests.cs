using BackupNormalizer;
namespace BackupNormalizer.Tests;

public sealed class PathTests
{
    [Fact]
    public void Normalize_Uses_Forward_Slash_Preserves_Case()
    {
        Assert.Equal("a/B/c.txt", Paths.NormalizeRelative(@"a\B\c.txt"));
        Assert.Equal("Photos/A.JPG", Paths.NormalizeRelative("Photos/A.JPG"));
    }

    [Fact]
    public void Relative_Roundtrip()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var full = Path.Combine(root, "sub", "f.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "x");
            Assert.Equal("sub/f.txt", Paths.GetRelative(root, full));
            Assert.Equal(full, Paths.CombineRoot(root, "sub/f.txt"));
        }
        finally { Directory.Delete(root, true); }
    }
}

public sealed class MatcherTests
{
    [Fact]
    public void Groups_By_Size_And_Hash()
    {
        var files = new List<PhysicalFile>
        {
            new("r", "a.txt", 3, "abc", "", "/tmp/a"),
            new("r", "b.txt", 3, "abc", "", "/tmp/b"),
            new("r", "c.txt", 3, "def", "", "/tmp/c"),
            new("r", "d.txt", 4, "abc", "", "/tmp/d"),
        };
        var groups = Matcher.BuildGroups(files);
        Assert.Equal(3, groups.Count);
        Assert.Contains(groups, g => g.Hash == "abc" && g.Size == 3 && g.Copies.Count == 2);
    }

    [Fact]
    public void Same_Size_Different_Hash_Not_Grouped()
    {
        var files = new List<PhysicalFile>
        {
            new("r", "a.bin", 100, "h1", "", "/a"),
            new("r", "b.bin", 100, "h2", "", "/b"),
        };
        var groups = Matcher.BuildGroups(files);
        Assert.Equal(2, groups.Count);
    }

}

public sealed class HashCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bn-" + Guid.NewGuid().ToString("N"));
    private readonly string _db;
    public HashCacheTests() { Directory.CreateDirectory(_dir); _db = Path.Combine(_dir, "t.db"); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Hash_Reused_When_Mtime_And_Size_Match()
    {
        var dataDir = Path.Combine(_dir, "data"); Directory.CreateDirectory(dataDir);
        File.WriteAllText(Path.Combine(dataDir, "a.txt"), "hello");
        using var db = new Database(_db);
        db.UpsertRoot(new StorageRootRow("r", "r", dataDir, true, "fs", "unknown", Database.UtcNow()));
        var sc = new Scanner(db);
        sc.ScanRoot("r");
        var r1 = sc.HashNeeded("r", true, 1);
        Assert.Equal(1, r1.hashed);
        var r2 = sc.HashNeeded("r", false, 1);
        Assert.Equal(0, r2.hashed);
        Assert.Equal(1, r2.skipped);
    }
}
