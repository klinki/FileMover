using BackupNormalizer;

namespace BackupNormalizer.Tests;

public sealed class TrashSurvivorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bn-trash-" + Guid.NewGuid().ToString("N"));
    public TrashSurvivorTests() { Directory.CreateDirectory(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static void W(string root, string rel, string content)
    {
        var abs = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, content);
    }

    private static string ScanHash(string dbPath, string rootId, string path)
    {
        using var db = new Database(dbPath);
        db.UpsertRoot(new StorageRootRow(rootId, rootId, path, "Backup", true, "fs", "unknown", Database.UtcNow()));
        var sc = new Scanner(db);
        sc.ScanRoot(rootId);
        sc.HashNeeded(rootId, true, 1);
        return dbPath;
    }

    private static string DigestOf(string abs)
        => HasherFactory.Create(null).HashFile(abs, new FileInfo(abs).Length);

    [Fact]
    public void Trash_Refused_When_No_Surviving_Copy()
    {
        var data = Path.Combine(_dir, "d"); Directory.CreateDirectory(data);
        W(data, "victim.txt", "lonely-bytes");
        string dbp = ScanHash(Path.Combine(_dir, "t.db"), "disk", data);
        string hash = DigestOf(Path.Combine(data, "victim.txt"));
        using (var db = new Database(dbp))
        {
            db.InsertPlan("p-lonely", "disk", 0);
            db.InsertOperation("p-lonely", 1, "TRASH", "disk", "victim.txt", "disk", null, 12, hash);
        }
        using (var db = new Database(dbp))
        {
            var sum = new Executor(db).Execute("p-lonely");
            Assert.Equal(1, sum.Conflicts);
        }
        Assert.True(File.Exists(Path.Combine(data, "victim.txt"))); // untouched
    }

    [Fact]
    public void Trash_Allowed_After_Same_Plan_Copy_Completes()
    {
        var data = Path.Combine(_dir, "d2"); Directory.CreateDirectory(data);
        W(data, "orig.txt", "shared-bytes");
        string dbp = ScanHash(Path.Combine(_dir, "t2.db"), "disk", data);
        string hash = DigestOf(Path.Combine(data, "orig.txt"));
        using (var db = new Database(dbp))
        {
            db.InsertPlan("p-copytrash", "disk", 12);
            db.InsertOperation("p-copytrash", 1, "COPY", "disk", "orig.txt", "disk", "copy.txt", 12, hash);
            db.InsertOperation("p-copytrash", 2, "TRASH", "disk", "orig.txt", "disk", null, 12, hash);
        }
        using (var db = new Database(dbp))
        {
            var sum = new Executor(db).Execute("p-copytrash");
            Assert.Equal(0, sum.Failed);
            Assert.Equal(0, sum.Conflicts);
        }
        Assert.True(File.Exists(Path.Combine(data, "copy.txt")));
        Assert.False(File.Exists(Path.Combine(data, "orig.txt")));
    }

    [Fact]
    public void Trash_Refused_When_Db_Survivor_Missing_On_Disk_Replay_Drift()
    {
        var data = Path.Combine(_dir, "d3"); Directory.CreateDirectory(data);
        W(data, "keep.txt", "same-bytes!!");
        W(data, "dup.txt", "same-bytes!!");
        string dbp = ScanHash(Path.Combine(_dir, "t3.db"), "disk", data);
        string hash = DigestOf(Path.Combine(data, "dup.txt"));
        // Drift: the surviving copy disappears before execution (stale replay).
        File.Delete(Path.Combine(data, "keep.txt"));
        using (var db = new Database(dbp))
        {
            db.InsertPlan("p-drift", "disk", 0);
            db.InsertOperation("p-drift", 1, "TRASH", "disk", "dup.txt", "disk", null, 13, hash);
        }
        using (var db = new Database(dbp))
        {
            var sum = new Executor(db).Execute("p-drift");
            Assert.Equal(1, sum.Conflicts);
        }
        Assert.True(File.Exists(Path.Combine(data, "dup.txt"))); // last copy preserved
    }
}
