using BackupNormalizer;

namespace BackupNormalizer.Tests;

public sealed class UiStagingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bn-ui-" + Guid.NewGuid().ToString("N"));
    public UiStagingTests() { Directory.CreateDirectory(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static void W(string root, string rel, string content)
    {
        var abs = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, content);
    }

    [Fact]
    public void Staged_Move_Is_Drive_Local_And_Executor_Compatible()
    {
        var baseDir = Path.Combine(_dir, "D1"); Directory.CreateDirectory(baseDir);
        W(baseDir, "Old/a.txt", "payload-1");
        Directory.CreateDirectory(Path.Combine(baseDir, "New"));

        // UI stages: MOVE Old/a.txt -> New/a.txt (drive-local, nothing executed yet)
        Assert.False(File.Exists(Path.Combine(baseDir, "New", "a.txt"))); // plan-only
        var staged = PlanStaging.StageMove(baseDir, Path.Combine(baseDir, "Old", "a.txt"), Path.Combine(baseDir, "New"));
        Assert.Contains(staged, s => s.Type == "MOVE");
        var doc = PlanStaging.BuildPlanDoc("ui-001", "disk", staged);
        string json = PlanStaging.ToJson(doc);
        Assert.Contains("MOVE", json);

        // Same executor runs it: write to DB then execute (simulates `plan import` + `execute`)
        string dbp = Path.Combine(_dir, "ui.db");
        using (var db = new Database(dbp))
            PlanStaging.WriteToDatabase(db, doc, "disk", baseDir);
        using (var db = new Database(dbp))
            Assert.True(db.PlanExists("ui-001"));
        using (var db = new Database(dbp))
        {
            var sum = new Executor(db).Execute("ui-001", new Dictionary<string, string> { ["disk"] = baseDir });
            Assert.Equal(0, sum.Failed);
            Assert.Equal(0, sum.Conflicts);
        }
        Assert.True(File.Exists(Path.Combine(baseDir, "New", "a.txt")));
        Assert.False(File.Exists(Path.Combine(baseDir, "Old", "a.txt")));
    }

    [Fact]
    public void Staged_Plan_Replays_On_Identical_Drive_Via_Remap()
    {
        // The "crazy idea": same mutations re-executed on another drive with same structure.
        var a = Path.Combine(_dir, "A"); Directory.CreateDirectory(a);
        W(a, "Old/a.txt", "same-bytes");
        Directory.CreateDirectory(Path.Combine(a, "New"));
        var staged = PlanStaging.StageMove(a, Path.Combine(a, "Old", "a.txt"), Path.Combine(a, "New"));
        var doc = PlanStaging.BuildPlanDoc("ui-remap", "disk", staged);
        string jsonPath = Path.Combine(_dir, "ui-remap.json");
        File.WriteAllText(jsonPath, PlanStaging.ToJson(doc));

        // Drive B has identical starting structure
        var b = Path.Combine(_dir, "B"); Directory.CreateDirectory(b);
        W(b, "Old/a.txt", "same-bytes");
        Directory.CreateDirectory(Path.Combine(b, "New"));

        // Import JSON into B's DB and execute with remapped root
        string dbp = Path.Combine(_dir, "b.db");
        var imported = PlanStaging.ImportJson(jsonPath);
        using (var db = new Database(dbp))
            PlanStaging.WriteToDatabase(db, imported, "disk", b);
        using (var db = new Database(dbp))
        {
            var sum = new Executor(db).Execute("ui-remap", new Dictionary<string, string> { ["disk"] = b });
            Assert.Equal(0, sum.Failed);
        }
        Assert.True(File.Exists(Path.Combine(b, "New", "a.txt")));
    }

    [Fact]
    public void Staged_Plan_Refuses_When_Remapped_Drive_Drifted()
    {
        var a = Path.Combine(_dir, "A2"); Directory.CreateDirectory(a);
        W(a, "Old/a.txt", "good");
        Directory.CreateDirectory(Path.Combine(a, "New"));
        var staged = PlanStaging.StageMove(a, Path.Combine(a, "Old", "a.txt"), Path.Combine(a, "New"));
        var doc = PlanStaging.BuildPlanDoc("ui-stale", "disk", staged);

        var b = Path.Combine(_dir, "B2"); Directory.CreateDirectory(b);
        W(b, "Old/a.txt", "DIFFERENT-bytes");
        Directory.CreateDirectory(Path.Combine(b, "New"));
        string dbp = Path.Combine(_dir, "b2.db");
        using (var db = new Database(dbp))
            PlanStaging.WriteToDatabase(db, doc, "disk", b);
        using (var db = new Database(dbp))
        {
            var sum = new Executor(db).Execute("ui-stale", new Dictionary<string, string> { ["disk"] = b });
            Assert.True(sum.Failed + sum.Conflicts > 0);
        }
        // Drifted file must NOT have been moved over
        Assert.True(File.Exists(Path.Combine(b, "Old", "a.txt")));
    }

    [Fact]
    public void Staging_Rejects_Paths_Outside_Base()
    {
        var baseDir = Path.Combine(_dir, "Base"); Directory.CreateDirectory(baseDir);
        var outside = Path.Combine(_dir, "Outside"); Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "x.txt"), "x");
        Assert.Throws<InvalidOperationException>(() =>
            PlanStaging.StageCopy(baseDir, Path.Combine(outside, "x.txt"), baseDir));
    }
}
