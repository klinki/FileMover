using BackupNormalizer;

namespace BackupNormalizer.Tests;

public sealed class IntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bn-it-" + Guid.NewGuid().ToString("N"));
    public IntegrationTests() { Directory.CreateDirectory(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static void W(string root, string rel, string content)
    {
        var abs = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, content);
    }

    private static void ScanHash(string db, string rootId, string path, string role = "Backup")
    {
        using var d = new Database(db);
        d.UpsertRoot(new StorageRootRow(rootId, rootId, path, role, true, "fs-" + path.GetHashCode(), "unknown", Database.UtcNow()));
        var sc = new Scanner(d);
        sc.ScanRoot(rootId);
        sc.HashNeeded(rootId, true, 1);
    }

    [Fact]
    public void Two_Db_Diff_And_Plan_With_Root_Remap()
    {
        // T0 snapshot on drive A, T1 current on drive B; plan migrated via --map-root equivalent
        var a = Path.Combine(_dir, "A"); Directory.CreateDirectory(a);
        W(a, "Photos/a.jpg", "bytes-1");
        string t0 = Path.Combine(_dir, "t0.db");
        ScanHash(t0, "disk", a);
        var b = Path.Combine(_dir, "B"); Directory.CreateDirectory(b);
        W(b, "Old/a.jpg", "bytes-1");
        string t1 = Path.Combine(_dir, "t1.db");
        // same FileSystemId to allow MOVE within target after remap? Use same fs marker by patching DB
        ScanHash(t1, "disk", b);
        var diff = Inventory.Diff(t0, t1);
        Assert.True(diff.OnlyInOld + diff.OnlyInNew > 0 || diff.Changed > 0 || diff.Identical == 0);
        // Plan: make B look like A (canonical=t0)
        using var td = new Database(t1);
        using var cd = new Database(t0);
        // Force same FS id for move detection
        using (var c = td.Conn.CreateCommand()) { c.CommandText = "UPDATE StorageRoot SET FileSystemId='shared'"; c.ExecuteNonQuery(); }
        var res = new Planner(td).PlanFromSnapshot(cd, null, "disk", "mig-1");
        Assert.Equal(1, res.Move);
        // Execute with remap disk -> B (already), then verify file landed at Photos/a.jpg
        var sum = new Executor(td).Execute("mig-1", new Dictionary<string, string> { ["disk"] = b });
        Assert.Equal(0, sum.Failed);
        Assert.True(File.Exists(Path.Combine(b, "Photos", "a.jpg")));
        // Migrate same plan concept to drive C via remap: copy plan JSON isn't needed; re-plan and execute on C
        var cdir = Path.Combine(_dir, "C"); Directory.CreateDirectory(cdir);
        W(cdir, "Old/a.jpg", "bytes-1");
        string t2 = Path.Combine(_dir, "t2.db");
        ScanHash(t2, "disk", cdir);
        using var td2 = new Database(t2);
        using (var c = td2.Conn.CreateCommand()) { c.CommandText = "UPDATE StorageRoot SET FileSystemId='shared'"; c.ExecuteNonQuery(); }
        new Planner(td2).PlanFromSnapshot(cd, null, "disk", "mig-2");
        var sum2 = new Executor(td2).Execute("mig-2", new Dictionary<string, string> { ["disk"] = cdir, ["canon:disk"] = a });
        Assert.True(File.Exists(Path.Combine(cdir, "Photos", "a.jpg")));
    }

    [Fact]
    public void Trash_And_Resume()
    {
        var dir = Path.Combine(_dir, "R"); Directory.CreateDirectory(dir);
        W(dir, "Photos/a.jpg", "same");
        W(dir, "Dup/a.jpg", "same");
        string dbp = Path.Combine(_dir, "r.db");
        ScanHash(dbp, "nas", dir, "Canonical");
        using var db = new Database(dbp);
        new Planner(db).PlanSingleDb("nas", "ptrash");
        var doc = new Planner(db).ExportPlan("ptrash");
        // Photos/a.jpg KEEP + Dup trashed? Both paths exist but only one is... single-DB canonical includes both paths as desired,
        // so no trash. Instead craft snapshot with only Photos:
        var cdir = Path.Combine(_dir, "RC"); Directory.CreateDirectory(cdir);
        W(cdir, "Photos/a.jpg", "same");
        string cdb = Path.Combine(_dir, "rc.db");
        ScanHash(cdb, "nas", cdir, "Canonical");
        using var cd = new Database(cdb);
        new Planner(db).PlanFromSnapshot(cd, null, "nas", "ptrash2");
        var sum = new Executor(db).Execute("ptrash2");
        Assert.Equal(0, sum.Failed);
        Assert.True(File.Exists(Path.Combine(dir, "Photos", "a.jpg")));
        Assert.False(File.Exists(Path.Combine(dir, "Dup", "a.jpg")));
        Assert.True(Directory.Exists(Path.Combine(dir, ".backup-normalizer-trash", "ptrash2")));
        // Resume is idempotent
        var sum2 = new Executor(db).Execute("ptrash2", resume: true);
        Assert.Equal(0, sum2.Failed);
    }

    [Fact]
    public void Safety_Destination_Exists_Different_Bytes_Refuses_Overwrite()
    {
        var tdir = Path.Combine(_dir, "S"); Directory.CreateDirectory(tdir);
        W(tdir, "Old/a.txt", "good");
        string tdb = Path.Combine(_dir, "s-t.db");
        ScanHash(tdb, "nas", tdir);
        var cdir = Path.Combine(_dir, "SC"); Directory.CreateDirectory(cdir);
        W(cdir, "New/a.txt", "good");
        string cdb = Path.Combine(_dir, "s-c.db");
        ScanHash(cdb, "nas", cdir);
        // Poison destination with different bytes before execute
        W(tdir, "New/a.txt", "evil-different**********");
        using var td = new Database(tdb);
        using var cd = new Database(cdb);
        // Need to rescan target to include poisoned dest? Planner sees dest exists different -> VERIFY conflict path.
        var sc = new Scanner(td); sc.ScanRoot("nas"); sc.HashNeeded("nas", true, 1);
        new Planner(td).PlanFromSnapshot(cd, null, "nas", "p-safe");
        var sum = new Executor(td).Execute("p-safe");
        // Must not overwrite evil file with good content silently
        Assert.Equal("evil-different**********", File.ReadAllText(Path.Combine(tdir, "New", "a.txt")));
    }

    [Fact]
    public void Inventory_Export_Import_Roundtrip()
    {
        var d1 = Path.Combine(_dir, "D1"); Directory.CreateDirectory(d1);
        W(d1, "f.txt", "x");
        string db1 = Path.Combine(_dir, "d1.db");
        ScanHash(db1, "d1", d1);
        string exp = Path.Combine(_dir, "d1.inv.db");
        Inventory.ExportRoot(db1, "d1", exp);
        string central = Path.Combine(_dir, "central.db");
        using (var _ = new Database(central)) { }
        Inventory.ImportFile(central, exp);
        using var c = new Database(central);
        Assert.Single(c.ListFiles("d1"));
    }
}
