using BackupNormalizer;

namespace BackupNormalizer.Tests;

public sealed class PlannerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bn-plan-" + Guid.NewGuid().ToString("N"));
    private readonly string _db;
    public PlannerTests() { Directory.CreateDirectory(_dir); _db = Path.Combine(_dir, "t.db"); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static void WriteFile(string root, string rel, string content)
    {
        var abs = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, content);
    }

    private void ScanAndHash(string dbPath, string rootId, string path)
    {
        using var db = new Database(dbPath);
        db.UpsertRoot(new StorageRootRow(rootId, rootId, path, rootId == "nas" ? "Canonical" : "Backup", true, "fs-" + rootId, "unknown", Database.UtcNow()));
        var sc = new Scanner(db);
        sc.ScanRoot(rootId);
        sc.HashNeeded(rootId, true, 1);
    }

    [Fact]
    public void Move_Detected_Zero_Copy_When_Same_Filesystem()
    {
        // Canonical wants Photos/a.jpg; disk has Old/a.jpg same bytes, same root FS
        var nas = Path.Combine(_dir, "nas"); Directory.CreateDirectory(nas);
        WriteFile(nas, "Old/a.jpg", "content-123");
        // Desired layout lives in same root: create canonical snapshot by scanning, then move file to Old and rescan?
        // Simpler: single-DB with canonical root containing desired path + another path duplicate.
        WriteFile(nas, "Photos/a.jpg", "content-123");
        // Remove desired to simulate "missing at desired, exists elsewhere"? Keep both then delete desired from FS but keep DB?
        // Instead: build two-path scenario via DB manipulation is complex; test planner prefers MOVE over COPY:
        using var db = new Database(_db);
        db.UpsertRoot(new StorageRootRow("nas", "N", nas, "Canonical", true, "fs1", "unknown", Database.UtcNow()));
        var sc = new Scanner(db);
        sc.ScanRoot("nas");
        sc.HashNeeded("nas", true, 1);
        // Delete Photos/a.jpg from FS and rescan to leave only Old/a.jpg in DB? Then canonical provider = snapshot with Photos path.
        File.Delete(Path.Combine(nas, "Photos", "a.jpg"));
        // Build canonical DB snapshot containing desired layout
        var canonDir = Path.Combine(_dir, "canon"); Directory.CreateDirectory(canonDir);
        WriteFile(canonDir, "Photos/a.jpg", "content-123");
        string cdb = Path.Combine(_dir, "c.db");
        ScanAndHash(cdb, "nas", canonDir);
        // Target DB currently has Old/a.jpg (plus stale Photos entry). Rescan target to drop stale? Our scanner doesn't prune; recreate target clean:
        string tdb = Path.Combine(_dir, "t2.db");
        var cleanTarget = Path.Combine(_dir, "target"); Directory.CreateDirectory(cleanTarget);
        WriteFile(cleanTarget, "Old/a.jpg", "content-123");
        ScanAndHash(tdb, "nas", cleanTarget);
        using var td = new Database(tdb);
        using var cd = new Database(cdb);
        // Align FileSystemIds so MOVE is allowed
        var res = new Planner(td).PlanFromSnapshot(cd, null, "nas", "p1");
        Assert.Equal(1, res.Move);
        Assert.Equal(0, res.Copy);
        Assert.Equal(0, res.BytesToCopy);
        Assert.True(res.BytesAvoided > 0);
    }

    [Fact]
    public void Copy_When_No_Same_Filesystem_Copy()
    {
        var cdir = Path.Combine(_dir, "c"); Directory.CreateDirectory(cdir);
        WriteFile(cdir, "Movies/m.mkv", "movie-bytes");
        string cdb = Path.Combine(_dir, "c2.db");
        ScanAndHash(cdb, "disk1", cdir);
        var tdir = Path.Combine(_dir, "t"); Directory.CreateDirectory(tdir);
        string tdb = Path.Combine(_dir, "t3.db");
        using (var tmp = new Database(tdb))
            tmp.UpsertRoot(new StorageRootRow("nas", "N", tdir, "Canonical", true, "fs-other", "unknown", Database.UtcNow()));
        using var td2 = new Database(tdb);
        using var cd2 = new Database(cdb);
        // target empty, canonical has file on different root id -> must COPY (source from canon snapshot unavailable at exec, but plan records COPY)
        // Our planner tags canon sources as canon:disk1 so COPY is planned
        var res = new Planner(td2).PlanFromSnapshot(cd2, null, "nas", "p-copy");
        Assert.Equal(1, res.Copy);
    }

    [Fact]
    public void Duplicate_Requires_Full_Hash_Never_Size_Only()
    {
        var dir = Path.Combine(_dir, "dup"); Directory.CreateDirectory(dir);
        WriteFile(dir, "keep/a.bin", "AAA");
        WriteFile(dir, "extra/b.bin", "BBB"); // same size 3, different content
        string dbp = Path.Combine(_dir, "dup.db");
        ScanAndHash(dbp, "nas", dir);
        using var db = new Database(dbp);
        using var cd = new Database(Path.Combine(_dir, "canon-dup.db"));
        // canonical snapshot: only keep/a.bin
        var cdir = Path.Combine(_dir, "cdup"); Directory.CreateDirectory(cdir);
        WriteFile(cdir, "keep/a.bin", "AAA");
        ScanAndHash(cd.FilePath(dbp: Path.Combine(_dir, "canon-dup.db")), "nas", cdir);
        using var cdd = new Database(Path.Combine(_dir, "canon-dup.db"));
        var res = new Planner(db).PlanFromSnapshot(cdd, null, "nas", "p-dup");
        var doc = new Planner(db).ExportPlan("p-dup");
        // b.bin must NOT be trashed (different hash, no surviving identical copy)
        Assert.DoesNotContain(doc.Operations, o => o.Type == "TRASH" && o.SourcePath == "extra/b.bin");
    }

    [Fact]
    public void Stale_Plan_Fails_Safely_Source_Disappears()
    {
        var dir = Path.Combine(_dir, "stale"); Directory.CreateDirectory(dir);
        WriteFile(dir, "Old/a.txt", "data");
        string tdb = Path.Combine(_dir, "stale-t.db");
        ScanAndHash(tdb, "nas", dir);
        var cdir = Path.Combine(_dir, "stale-c"); Directory.CreateDirectory(cdir);
        WriteFile(cdir, "New/a.txt", "data");
        string cdb = Path.Combine(_dir, "stale-c.db");
        ScanAndHash(cdb, "nas", cdir);
        using var td = new Database(tdb);
        using var cd = new Database(cdb);
        new Planner(td).PlanFromSnapshot(cd, null, "nas", "p-stale");
        // Delete source before execute -> MOVE/COPY must Conflict/Failed, never silently succeed
        File.Delete(Path.Combine(dir, "Old", "a.txt"));
        var sum = new Executor(td).Execute("p-stale");
        Assert.True(sum.Conflicts + sum.Failed > 0);
    }
}

file static class DbPathExt
{
    public static string FilePath(this Database _, string dbp) => dbp;
}
