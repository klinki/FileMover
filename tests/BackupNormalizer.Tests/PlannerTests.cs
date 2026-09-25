using BackupNormalizer;

namespace BackupNormalizer.Tests;

public sealed class PlannerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bn-plan-" + Guid.NewGuid().ToString("N"));
    public PlannerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static void Write(string root, string relative, string contents)
    {
        var path = Paths.CombineRoot(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private static Database CreateDatabase(string dbPath, string id, string root, bool hash = true)
    {
        var db = new Database(dbPath);
        db.UpsertRoot(new StorageRootRow(id, id, root, true, "fs", "unknown", Database.UtcNow()));
        var scanner = new Scanner(db);
        Assert.Equal(0, scanner.ScanRoot(id).errors);
        if (hash) scanner.HashNeeded(id, true, 1);
        return db;
    }

    [Fact]
    public void Duplicate_Desired_Content_Uses_One_Move_And_One_Copy()
    {
        var source = Path.Combine(_dir, "source"); Directory.CreateDirectory(source);
        var target = Path.Combine(_dir, "target"); Directory.CreateDirectory(target);
        Write(source, "new/a.txt", "same");
        Write(source, "new/b.txt", "same");
        Write(target, "old/a.txt", "same");
        using var sd = CreateDatabase(Path.Combine(_dir, "s.db"), "disk", source);
        using var td = CreateDatabase(Path.Combine(_dir, "t.db"), "disk", target);
        var result = new Planner(td).PlanFromRoots(sd, "disk", "disk", "duplicates");
        Assert.Equal(1, result.Move);
        Assert.Equal(1, result.Copy);
        Assert.Equal(0, result.Trash);
        var ops = new Planner(td).ExportPlan("duplicates").Operations;
        Assert.DoesNotContain(ops, op => op.Type == "TRASH" && op.SourcePath == "old/a.txt");
        var execution = new Executor(td).Execute("duplicates");
        Assert.Equal(0, execution.Failed + execution.Conflicts);
        Assert.True(File.Exists(Path.Combine(target, "new", "a.txt")));
        Assert.True(File.Exists(Path.Combine(target, "new", "b.txt")));
    }

    [Fact]
    public void Existing_Target_Copy_Supplies_Missing_Duplicate()
    {
        var source = Path.Combine(_dir, "source-duplicate"); Directory.CreateDirectory(source);
        var target = Path.Combine(_dir, "target-duplicate"); Directory.CreateDirectory(target);
        Write(source, "a.txt", "same");
        Write(source, "z.txt", "same");
        Write(target, "z.txt", "same");
        using var sd = CreateDatabase(Path.Combine(_dir, "source-duplicate.db"), "r", source);
        using var td = CreateDatabase(Path.Combine(_dir, "target-duplicate.db"), "r", target);
        var result = new Planner(td).PlanFromRoots(sd, "r", "r", "local-copy");
        Assert.Equal(1, result.Copy);
        var copy = Assert.Single(new Planner(td).ExportPlan("local-copy").Operations, op => op.Type == "COPY");
        Assert.Equal("Target", copy.SourceKind);
        Assert.Equal("z.txt", copy.SourcePath);
        Assert.Equal(0, new Executor(td).Execute("local-copy").Conflicts);
    }

    [Fact]
    public void Extra_Is_Trashed_Only_With_Verified_Target_Survivor()
    {
        var source = Path.Combine(_dir, "source2"); Directory.CreateDirectory(source);
        var target = Path.Combine(_dir, "target2"); Directory.CreateDirectory(target);
        Write(source, "keep.txt", "same");
        Write(target, "keep.txt", "same");
        Write(target, "duplicate.txt", "same");
        Write(target, "different.txt", "other");
        using var sd = CreateDatabase(Path.Combine(_dir, "s2.db"), "disk", source);
        using var td = CreateDatabase(Path.Combine(_dir, "t2.db"), "disk", target);
        var result = new Planner(td).PlanFromRoots(sd, "disk", "disk", "trash");
        Assert.Equal(1, result.Trash);
        var execution = new Executor(td).Execute("trash");
        Assert.Equal(0, execution.Failed + execution.Conflicts);
        Assert.True(File.Exists(Path.Combine(target, "different.txt")));
        Assert.False(File.Exists(Path.Combine(target, "duplicate.txt")));
        Assert.True(File.Exists(Path.Combine(target, ".backup-normalizer-trash", "trash", "duplicate.txt")));
    }

    [Fact]
    public void Planning_Requires_Hashes_And_Disjoint_Roots()
    {
        var source = Path.Combine(_dir, "source3"); Directory.CreateDirectory(source);
        var target = Path.Combine(_dir, "target3"); Directory.CreateDirectory(target);
        Write(source, "x.txt", "x");
        using var sd = CreateDatabase(Path.Combine(_dir, "s3.db"), "s", source, hash: false);
        using var td = CreateDatabase(Path.Combine(_dir, "t3.db"), "t", target);
        Assert.Throws<InvalidOperationException>(() => new Planner(td).PlanFromRoots(sd, "s", "t", "unhashed"));
        new Scanner(sd).HashNeeded("s", true, 1);
        td.UpsertRoot(td.GetRoot("t")! with { Path = source });
        Assert.Throws<InvalidOperationException>(() => new Planner(td).PlanFromRoots(sd, "s", "t", "overlap"));
    }

    [Fact]
    public void Complete_Rescan_Marks_Removed_Files_Missing()
    {
        var root = Path.Combine(_dir, "rescan"); Directory.CreateDirectory(root);
        Write(root, "gone.txt", "x");
        using var db = CreateDatabase(Path.Combine(_dir, "rescan.db"), "r", root);
        File.Delete(Path.Combine(root, "gone.txt"));
        Assert.Equal(0, new Scanner(db).ScanRoot("r").errors);
        Assert.Equal("Missing", db.GetFileEntry("r", "gone.txt")!.Status);
        Assert.Empty(Matcher.LoadFromDb(db, "sha256", "r"));
    }

    [Fact]
    public void Diff_Allows_Overlap_But_Plan_Rejects_It()
    {
        var root = Path.Combine(_dir, "shared"); Directory.CreateDirectory(root);
        Write(root, "x.txt", "same");
        using var left = CreateDatabase(Path.Combine(_dir, "left.db"), "r", root);
        using var right = CreateDatabase(Path.Combine(_dir, "right.db"), "r", root);
        Assert.Equal(1, Inventory.Diff(left.DbPath, "r", right.DbPath, "r").Identical);
        Assert.Throws<InvalidOperationException>(() => new Planner(right).PlanFromRoots(left, "r", "r", "overlap"));
    }

    [Fact]
    public void Diff_Marks_Same_Size_Without_Hash_Unverified()
    {
        var source = Path.Combine(_dir, "unverified-source"); Directory.CreateDirectory(source);
        var target = Path.Combine(_dir, "unverified-target"); Directory.CreateDirectory(target);
        Write(source, "x.txt", "aaa");
        Write(target, "x.txt", "bbb");
        using var left = CreateDatabase(Path.Combine(_dir, "unverified-left.db"), "r", source, hash: false);
        using var right = CreateDatabase(Path.Combine(_dir, "unverified-right.db"), "r", target, hash: false);
        var diff = Inventory.Diff(left.DbPath, "r", right.DbPath, "r");
        Assert.Equal(1, diff.Unverified);
        Assert.Equal(0, diff.Identical);
    }

    [Fact]
    public void Failed_Scan_Cannot_Be_Used_For_Planning()
    {
        var source = Path.Combine(_dir, "scan-source"); Directory.CreateDirectory(source);
        var target = Path.Combine(_dir, "scan-target"); Directory.CreateDirectory(target);
        Write(source, "x.txt", "x");
        using var left = CreateDatabase(Path.Combine(_dir, "scan-left.db"), "r", source);
        using var right = CreateDatabase(Path.Combine(_dir, "scan-right.db"), "r", target);
        Directory.Delete(source, true);
        Assert.Throws<DirectoryNotFoundException>(() => new Scanner(left).ScanRoot("r"));
        Assert.Equal("Failed", left.LatestScanStatus("r"));
        Assert.Throws<InvalidOperationException>(() => new Planner(right).PlanFromRoots(left, "r", "r", "failed-scan"));
    }

    [Fact]
    public void Changing_Root_Path_Invalidates_Scan_And_Hashes()
    {
        var original = Path.Combine(_dir, "original"); Directory.CreateDirectory(original);
        var replacement = Path.Combine(_dir, "replacement"); Directory.CreateDirectory(replacement);
        Write(original, "x.txt", "one");
        Write(replacement, "x.txt", "two");
        using var db = CreateDatabase(Path.Combine(_dir, "repath.db"), "r", original);
        var entry = db.GetFileEntry("r", "x.txt")!;
        Assert.Equal("Ok", db.GetHash(entry.Id, "sha256")!.State);
        db.UpsertRoot(db.GetRoot("r")! with { Path = replacement });
        Assert.Equal("Invalidated", db.LatestScanStatus("r"));
        Assert.Equal("Stale", db.GetHash(entry.Id, "sha256")!.State);
    }
}
