using BackupNormalizer;

namespace BackupNormalizer.Tests;

public sealed class ExecuteConfirmTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bn-confirm-" + Guid.NewGuid().ToString("N"));
    public ExecuteConfirmTests() { Directory.CreateDirectory(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static void W(string root, string rel, string content)
    {
        var abs = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, content);
    }

    private static void ScanHash(string dbPath, string rootId, string path)
    {
        using var db = new Database(dbPath);
        db.UpsertRoot(new StorageRootRow(rootId, rootId, path, "Backup", true, "fs", "unknown", Database.UtcNow()));
        var sc = new Scanner(db);
        sc.ScanRoot(rootId);
        sc.HashNeeded(rootId, true, 1);
    }

    private string SetupPlan(string tag)
    {
        var canon = Path.Combine(_dir, tag + "-c"); Directory.CreateDirectory(canon);
        W(canon, "a.txt", "confirm-bytes");
        var target = Path.Combine(_dir, tag + "-t"); Directory.CreateDirectory(target);
        W(target, "Old/a.txt", "confirm-bytes");
        string cdb = Path.Combine(_dir, tag + "-c.db");
        string tdb = Path.Combine(_dir, tag + "-t.db");
        ScanHash(cdb, "disk", canon);
        ScanHash(tdb, "disk", target);
        using var td = new Database(tdb);
        using var cd = new Database(cdb);
        new Planner(td).PlanFromSnapshot(cd, null, "disk", tag);
        return tdb;
    }

    private static int RunExecute(string db, string plan, string stdin, out string stdout, params string[] extra)
    {
        var oldIn = Console.In;
        var oldOut = Console.Out;
        try
        {
            Console.SetIn(new StringReader(stdin));
            var sw = new StringWriter();
            Console.SetOut(sw);
            var args = new List<string> { "execute", plan, "--db", db };
            args.AddRange(extra);
            int rc = Cli.Run(args.ToArray());
            stdout = sw.ToString();
            return rc;
        }
        finally
        {
            Console.SetIn(oldIn);
            Console.SetOut(oldOut);
        }
    }

    [Fact]
    public void Execute_Asks_And_Runs_On_Yes()
    {
        string tdb = SetupPlan("yes");
        int rc = RunExecute(tdb, "yes", "y\n", out string stdout);
        Assert.Equal(0, rc);
        Assert.Contains("Execute", stdout);
        Assert.True(File.Exists(Path.Combine(_dir, "yes-t", "a.txt")));
    }

    [Fact]
    public void Execute_Aborts_On_No_Without_Modifying()
    {
        string tdb = SetupPlan("no");
        int rc = RunExecute(tdb, "no", "n\n", out _);
        Assert.Equal(2, rc);
        Assert.True(File.Exists(Path.Combine(_dir, "no-t", "Old", "a.txt")));
        Assert.False(File.Exists(Path.Combine(_dir, "no-t", "a.txt")));
    }

    [Fact]
    public void Execute_Yes_Flag_Skips_Prompt()
    {
        string tdb = SetupPlan("flag");
        int rc = RunExecute(tdb, "flag", "", out _, "--yes", "--map-root", "disk=" + Path.Combine(_dir, "flag-t"));
        Assert.Equal(0, rc);
        Assert.True(File.Exists(Path.Combine(_dir, "flag-t", "a.txt")));
    }
}
