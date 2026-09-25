using BackupNormalizer;

namespace BackupNormalizer.Tests;

public sealed class IntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bn-it-" + Guid.NewGuid().ToString("N"));
    public IntegrationTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static void Write(string root, string relative, string contents)
    {
        var path = Paths.CombineRoot(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private static void Scan(string dbPath, string id, string root)
    {
        using var db = new Database(dbPath);
        db.UpsertRoot(new StorageRootRow(id, id, root, true, "fs", "unknown", Database.UtcNow()));
        var scanner = new Scanner(db);
        Assert.Equal(0, scanner.ScanRoot(id).errors);
        scanner.HashNeeded(id, true, 1);
    }

    [Fact]
    public void Diff_Plan_Execute_And_Resume_Across_Two_Databases()
    {
        var source = Path.Combine(_dir, "source"); Directory.CreateDirectory(source);
        var target = Path.Combine(_dir, "target"); Directory.CreateDirectory(target);
        Write(source, "Photos/a.jpg", "bytes");
        Write(target, "Old/a.jpg", "bytes");
        var sourceDbPath = Path.Combine(_dir, "source.db");
        var targetDbPath = Path.Combine(_dir, "target.db");
        Scan(sourceDbPath, "disk", source);
        Scan(targetDbPath, "disk", target);

        var diff = Inventory.Diff(sourceDbPath, "disk", targetDbPath, "disk");
        Assert.Equal(1, diff.SourceOnly);
        Assert.Equal(1, diff.TargetOnly);

        using var sourceDb = Database.OpenReadOnly(sourceDbPath);
        using var targetDb = new Database(targetDbPath);
        var result = new Planner(targetDb).PlanFromRoots(sourceDb, "disk", "disk", "plan");
        Assert.Equal(1, result.Move);
        Assert.Equal(0, result.Copy);
        Assert.Equal("Target", new Planner(targetDb).ExportPlan("plan").Operations.Single(o => o.Type == "MOVE").SourceKind);

        var execution = new Executor(targetDb).Execute("plan");
        Assert.Equal(0, execution.Conflicts + execution.Failed);
        Assert.True(File.Exists(Path.Combine(target, "Photos", "a.jpg")));
        Assert.False(File.Exists(Path.Combine(target, "Old", "a.jpg")));
        Assert.Equal(0, new Executor(targetDb).Execute("plan", resume: true).Failed);
    }

    [Fact]
    public void Same_Database_Can_Compare_Disjoint_Roots()
    {
        var source = Path.Combine(_dir, "s"); Directory.CreateDirectory(source);
        var target = Path.Combine(_dir, "t"); Directory.CreateDirectory(target);
        Write(source, "a.txt", "payload");
        var dbPath = Path.Combine(_dir, "both.db");
        Scan(dbPath, "source", source);
        Scan(dbPath, "target", target);
        var diff = Inventory.Diff(dbPath, "source", dbPath, "target");
        Assert.Equal(1, diff.SourceOnly);
        using var db = new Database(dbPath);
        var result = new Planner(db).PlanFromRoots(db, "source", "target", "same-db");
        Assert.Equal(1, result.Copy);
        Assert.Equal(0, new Executor(db).Execute("same-db").Failed);
        Assert.Equal("payload", File.ReadAllText(Path.Combine(target, "a.txt")));
    }

    [Fact]
    public void Changed_Destination_Is_Preserved_As_Conflict()
    {
        var source = Path.Combine(_dir, "s2"); Directory.CreateDirectory(source);
        var target = Path.Combine(_dir, "t2"); Directory.CreateDirectory(target);
        Write(source, "a.txt", "good");
        Write(target, "a.txt", "evil");
        var sourceDbPath = Path.Combine(_dir, "s2.db");
        var targetDbPath = Path.Combine(_dir, "t2.db");
        Scan(sourceDbPath, "s", source);
        Scan(targetDbPath, "t", target);
        using var sd = Database.OpenReadOnly(sourceDbPath);
        using var td = new Database(targetDbPath);
        new Planner(td).PlanFromRoots(sd, "s", "t", "conflict");
        var result = new Executor(td).Execute("conflict");
        Assert.Equal(1, result.Conflicts);
        Assert.Equal("evil", File.ReadAllText(Path.Combine(target, "a.txt")));
    }

    [Fact]
    public void Diff_Direction_And_Selected_Roots_Are_Independent()
    {
        var left = Path.Combine(_dir, "left"); Directory.CreateDirectory(left);
        var right = Path.Combine(_dir, "right"); Directory.CreateDirectory(right);
        var unrelated = Path.Combine(_dir, "unrelated"); Directory.CreateDirectory(unrelated);
        Write(left, "left.txt", "one");
        Write(right, "right.txt", "two");
        Write(unrelated, "left.txt", "one");
        var leftDbPath = Path.Combine(_dir, "left.db");
        var rightDbPath = Path.Combine(_dir, "right.db");
        Scan(leftDbPath, "selected", left);
        Scan(leftDbPath, "other", unrelated);
        Scan(rightDbPath, "selected", right);

        var forward = Inventory.Diff(leftDbPath, "selected", rightDbPath, "selected");
        var reverse = Inventory.Diff(rightDbPath, "selected", leftDbPath, "selected");
        Assert.Contains("source-only: left.txt", forward.Samples);
        Assert.Contains("source-only: right.txt", reverse.Samples);
        Assert.Equal(1, forward.SourceOnly);
        Assert.Equal(1, forward.TargetOnly);
        Assert.Equal(1, reverse.SourceOnly);
        Assert.Equal(1, reverse.TargetOnly);
    }

    [Fact]
    public void Missing_Source_At_Execution_Produces_Conflict()
    {
        var source = Path.Combine(_dir, "missing-source"); Directory.CreateDirectory(source);
        var target = Path.Combine(_dir, "missing-target"); Directory.CreateDirectory(target);
        Write(source, "copy.txt", "payload");
        var sourceDbPath = Path.Combine(_dir, "missing-source.db");
        var targetDbPath = Path.Combine(_dir, "missing-target.db");
        Scan(sourceDbPath, "disk", source);
        Scan(targetDbPath, "disk", target);
        using var sd = Database.OpenReadOnly(sourceDbPath);
        using var td = new Database(targetDbPath);
        Assert.Equal(1, new Planner(td).PlanFromRoots(sd, "disk", "disk", "missing").Copy);
        File.Delete(Path.Combine(source, "copy.txt"));
        var result = new Executor(td).Execute("missing");
        Assert.Equal(1, result.Conflicts);
        Assert.False(File.Exists(Path.Combine(target, "copy.txt")));
    }

    [Fact]
    public void Keep_Verifies_Target_Again_At_Execution()
    {
        var source = Path.Combine(_dir, "keep-source"); Directory.CreateDirectory(source);
        var target = Path.Combine(_dir, "keep-target"); Directory.CreateDirectory(target);
        Write(source, "a.txt", "good");
        Write(target, "a.txt", "good");
        var sourceDbPath = Path.Combine(_dir, "keep-source.db");
        var targetDbPath = Path.Combine(_dir, "keep-target.db");
        Scan(sourceDbPath, "disk", source);
        Scan(targetDbPath, "disk", target);
        using var sd = Database.OpenReadOnly(sourceDbPath);
        using var td = new Database(targetDbPath);
        Assert.Equal(1, new Planner(td).PlanFromRoots(sd, "disk", "disk", "keep").Keep);
        Write(target, "a.txt", "evil");
        Assert.Equal(1, new Executor(td).Execute("keep").Conflicts);
    }

    [Fact]
    public void Move_Resume_Accepts_Landed_File_After_Crash()
    {
        var source = Path.Combine(_dir, "resume-source"); Directory.CreateDirectory(source);
        var target = Path.Combine(_dir, "resume-target"); Directory.CreateDirectory(target);
        Write(source, "new.txt", "same");
        Write(target, "old.txt", "same");
        var sourceDbPath = Path.Combine(_dir, "resume-source.db");
        var targetDbPath = Path.Combine(_dir, "resume-target.db");
        Scan(sourceDbPath, "disk", source);
        Scan(targetDbPath, "disk", target);
        using var sd = Database.OpenReadOnly(sourceDbPath);
        using var td = new Database(targetDbPath);
        Assert.Equal(1, new Planner(td).PlanFromRoots(sd, "disk", "disk", "resume").Move);
        File.Move(Path.Combine(target, "old.txt"), Path.Combine(target, "new.txt"));
        var result = new Executor(td).Execute("resume", resume: true);
        Assert.Equal(0, result.Failed + result.Conflicts);
        Assert.Equal("same", File.ReadAllText(Path.Combine(target, "new.txt")));
    }
}
