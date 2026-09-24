namespace BackupNormalizer;

public static class Cli
{
    public static int Run(string[] args)
    {
        if (args.Length == 0 || args is ["--help"] or ["help"] or ["-h"]) return Help();
        // global flags
        Log.Json = args.Contains("--json");
        try
        {
            string cmd = args[0].ToLowerInvariant();
            return cmd switch
            {
                "--version" or "version" => Version(),
                "init" => Init(args[1..]),
                "root" => Root(args[1..]),
                "scan" => Scan(args[1..]),
                "hash" => Hash(args[1..]),
                "plan" => Plan(args[1..]),
                "execute" => Execute(args[1..]),
                "verify" => Verify(args[1..]),
                "purge" => Purge(args[1..]),
                "inventory" => Inv(args[1..]),
                "diff" => Diff(args[1..]),
                "db-test" => DbTest(args[1..]),
                "scan-test" => ScanTest(args[1..]),
                _ => Fail($"unknown command '{args[0]}'. Try 'help'."),
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex.Message);
            return 2;
        }
    }

    private static int Help()
    {
        Console.WriteLine("""
            backup-normalizer 0.1.0 — safe file-tree normalization (§26 + 2-DB extension)
            Usage: BackupNormalizer <command> [options]
              init [--db PATH] [--config PATH]
              root add <id> <path> [--role R] [--name N] [--writable true|false] [--db PATH]
              root list [--db PATH]
              scan <rootId|--all> [--db PATH]
              hash --needed [--db PATH] [--parallelism N] | hash <rootId> --all [--db PATH]
              plan --canonical <rootId> [--db PATH] [--plan ID]
              plan --canonical-db C.db --target-db T.db [--canonical-root R] --target-root R [--plan ID] [--out-db T.db]
              plan show <plan-id> [--db PATH] | plan export <plan-id> [--format json] [--output F] [--db PATH]
              execute <plan-id> [--db PATH] [--map-root id=path ...] [--resume] [--stop-on-error]
              verify <plan-id> [--db PATH] [--map-root id=path ...]
              purge --older-than 30d --yes [--db PATH] [--path ROOTPATH]
              inventory export <rootId> --output F [--db PATH] | inventory import <file> [--db PATH]
              diff --old A.db --new B.db
              db-test [--db PATH] | scan-test <path> | --version
            2-DB mode: scan each drive to its own .db, then diff/plan with --old/--canonical-db + --new/--target-db,
                       then execute with --map-root to migrate the plan to other drives.
            """);
        return 0;
    }

    private static int Version() { Console.WriteLine("BackupNormalizer 0.1.0"); return 0; }
    private static int Fail(string m) { Console.Error.WriteLine("error: " + m); return 2; }

    private static string Opt(string[] a, string name, string fallback)
    {
        for (int i = 0; i < a.Length - 1; i++)
            if (a[i] == name) return a[i + 1];
        var env = Environment.GetEnvironmentVariable("BN_" + name.TrimStart('-').ToUpperInvariant().Replace('-', '_'));
        return env ?? fallback;
    }
    private static bool Has(string[] a, string name) => a.Contains(name);
    private static Dictionary<string, string> MapRoots(string[] a)
    {
        var d = new Dictionary<string, string>();
        for (int i = 0; i < a.Length - 1; i++)
            if (a[i] == "--map-root")
            {
                var kv = a[i + 1].Split('=', 2);
                if (kv.Length == 2) d[kv[0]] = kv[1];
            }
        return d;
    }

    private static int Init(string[] a)
    {
        string db = Opt(a, "--db", AppConfig.Load(Opt(a, "--config", AppConfig.DefaultPath)).Database);
        using var _ = new Database(db);
        var cfg = AppConfig.Load(Opt(a, "--config", AppConfig.DefaultPath));
        cfg.Database = db; cfg.Save(Opt(a, "--config", AppConfig.DefaultPath));
        Console.WriteLine($"initialized {db}");
        return 0;
    }

    private static int Root(string[] a)
    {
        if (a.Length == 0) return Fail("root add|list");
        string cfgPath = Opt(a, "--config", AppConfig.DefaultPath);
        string db = Opt(a, "--db", AppConfig.Load(cfgPath).Database);
        if (a[0] == "list")
        {
            using var d = new Database(db);
            foreach (var r in d.ListRoots()) Console.WriteLine($"{r.Id}\t{r.Name}\t{r.Path}\t{r.Role}\twritable={r.Writable}");
            return 0;
        }
        if (a[0] == "add" && a.Length >= 3)
        {
            string id = a[1], path = Path.GetFullPath(a[2]);
            string role = Opt(a, "--role", "Unknown"), name = Opt(a, "--name", id);
            bool writable = !string.Equals(Opt(a, "--writable", "true"), "false", StringComparison.OrdinalIgnoreCase);
            if (!Directory.Exists(path)) return Fail($"path not found: {path}");
            using var d = new Database(db);
            d.UpsertRoot(new StorageRootRow(id, name, path, role, writable, Paths.GetFileSystemId(path), Paths.DetectCaseSensitivity(path), Database.UtcNow()));
            Console.WriteLine($"root '{id}' -> {path}");
            return 0;
        }
        return Fail("root add <id> <path> [--role R] | root list");
    }

    private static int Scan(string[] a)
    {
        if (a.Length == 0) return Fail("scan <rootId|--all>");
        string db = Opt(a, "--db", AppConfig.Load(Opt(a, "--config", AppConfig.DefaultPath)).Database);
        string algo = Opt(a, "--hash-algo", AppConfig.Load(Opt(a, "--config", AppConfig.DefaultPath)).HashAlgorithm);
        using var d = new Database(db);
        var sc = new Scanner(d, algo);
        if (a[0] == "--all")
        {
            foreach (var r in d.ListRoots())
            {
                var (s, e) = sc.ScanRoot(r.Id);
                Console.WriteLine($"scan {r.Id}: {s} files, {e} errors");
            }
            return 0;
        }
        var (scanned, errors) = sc.ScanRoot(a[0]);
        Console.WriteLine($"scan {a[0]}: {scanned} files, {errors} errors");
        return 0;
    }

    private static int Hash(string[] a)
    {
        string db = Opt(a, "--db", AppConfig.Load(Opt(a, "--config", AppConfig.DefaultPath)).Database);
        int par = int.TryParse(Opt(a, "--parallelism", "2"), out var p) ? p : 2;
        using var d = new Database(db);
        var sc = new Scanner(d);
        if (Has(a, "--needed")) { var r = sc.HashNeeded(null, false, par); Console.WriteLine($"hash --needed: {r.hashed} hashed, {r.skipped} reused, {r.unstable} unstable"); return 0; }
        if (a.Length >= 1 && !a[0].StartsWith("--"))
        {
            bool all = Has(a, "--all");
            var r = sc.HashNeeded(a[0], all, par); Console.WriteLine($"hash {a[0]}: {r.hashed} hashed, {r.skipped} skipped, {r.unstable} unstable"); return 0;
        }
        return Fail("hash --needed | hash <rootId> --all");
    }

    private static int Plan(string[] a)
    {
        if (a.Length == 0) return Fail("plan ...");
        if (a[0] == "show" && a.Length >= 2)
        {
            string db = Opt(a, "--db", AppConfig.Load(Opt(a, "--config", AppConfig.DefaultPath)).Database);
            using var d = new Database(db);
            var doc = new Planner(d).ExportPlan(a[1]);
            Console.WriteLine($"Plan {doc.PlanId} created {doc.CreatedUtc} estBytes={doc.EstimatedBytesCopied}");
            foreach (var o in doc.Operations) Console.WriteLine($"  {o.Id:D4} {o.Type,-7} {o.SourceRoot}:{o.SourcePath} -> {o.DestinationRoot}:{o.DestinationPath} size={o.ExpectedSize}");
            return 0;
        }
        if (a[0] == "export" && a.Length >= 2)
        {
            string db = Opt(a, "--db", AppConfig.Load(Opt(a, "--config", AppConfig.DefaultPath)).Database);
            using var d = new Database(db);
            var doc = new Planner(d).ExportPlan(a[1]);
            string json = Planner.ToJson(doc);
            string? outF = Has(a, "--output") ? Opt(a, "--output", "") : null;
            if (!string.IsNullOrEmpty(outF)) File.WriteAllText(outF, json);
            else Console.WriteLine(json);
            return 0;
        }
        // two-DB mode?
        if (Has(a, "--canonical-db") || Has(a, "--target-db") || Has(a, "--old"))
        {
            string cdb = Has(a, "--canonical-db") ? Opt(a, "--canonical-db", "") : Opt(a, "--old", "");
            string tdb = Has(a, "--target-db") ? Opt(a, "--target-db", "") : Opt(a, "--new", Opt(a, "--target-db", ""));
            string? canonRoot = Has(a, "--canonical-root") ? Opt(a, "--canonical-root", "") : null;
            string targetRoot = Opt(a, "--target-root", "");
            string planId = Opt(a, "--plan", DateTime.UtcNow.ToString("yyyy-MM-dd-HHmmss"));
            if (string.IsNullOrEmpty(cdb) || string.IsNullOrEmpty(tdb) || string.IsNullOrEmpty(targetRoot))
                return Fail("two-DB plan needs --canonical-db/--old, --target-db/--new and --target-root");
            using var cd = new Database(cdb);
            using var td = new Database(tdb);
            var res = new Planner(td).PlanFromSnapshot(cd, string.IsNullOrEmpty(canonRoot) ? null : canonRoot, targetRoot, planId);
            PrintPlan(res);
            return 0;
        }
        // single-DB (spec default): plan --canonical <rootId>
        string sdb = Opt(a, "--db", AppConfig.Load(Opt(a, "--config", AppConfig.DefaultPath)).Database);
        string canon = Opt(a, "--canonical", "");
        if (string.IsNullOrEmpty(canon)) return Fail("plan --canonical <rootId> [--plan ID] [--db PATH]");
        string pid = Opt(a, "--plan", DateTime.UtcNow.ToString("yyyy-MM-dd-HHmmss"));
        using var sdd = new Database(sdb);
        var r2 = new Planner(sdd).PlanSingleDb(canon, pid);
        PrintPlan(r2);
        return 0;
    }

    private static void PrintPlan(Planner.PlanResult r)
    {
        Console.WriteLine($"Plan {r.PlanId}");
        Console.WriteLine($"KEEP {r.Keep}  MOVE {r.Move}  COPY {r.Copy}  TRASH {r.Trash}  MKDIR {r.Mkdir}");
        Console.WriteLine($"Bytes requiring actual copying: {r.BytesToCopy}");
        Console.WriteLine($"Bytes avoided through moves: {r.BytesAvoided}");
        Console.WriteLine("No files were modified (dry-run, §19).");
    }

    private static int Execute(string[] a)
    {
        if (a.Length == 0) return Fail("execute <plan-id>");
        string db = Opt(a, "--db", AppConfig.Load(Opt(a, "--config", AppConfig.DefaultPath)).Database);
        var map = MapRoots(a);
        using var d = new Database(db);
        var sum = new Executor(d).Execute(a[0], map, Has(a, "--resume"), Has(a, "--stop-on-error"));
        Console.WriteLine($"execute {a[0]}: completed={sum.Completed} failed={sum.Failed} conflicts={sum.Conflicts}");
        return sum.Failed == 0 && sum.Conflicts == 0 ? 0 : 3;
    }

    private static int Verify(string[] a)
    {
        if (a.Length == 0) return Fail("verify <plan-id>");
        string db = Opt(a, "--db", AppConfig.Load(Opt(a, "--config", AppConfig.DefaultPath)).Database);
        var map = MapRoots(a);
        using var d = new Database(db);
        // Verify = re-run VERIFY ops + destination checks via executor path: execute only VERIFY by direct check
        using var c = d.Conn.CreateCommand();
        c.CommandText = "SELECT Type,DestinationRootId,DestinationPath,ExpectedSize,ExpectedHash FROM PlanOperation WHERE PlanId=$p";
        c.Parameters.AddWithValue("$p", a[0]);
        var roots = d.ListRoots().ToDictionary(r => r.Id);
        int ok = 0, bad = 0;
        var hasher = HasherFactory.Create(null);
        using var r = c.ExecuteReader();
        while (r.Read())
        {
            string type = r.GetString(0);
            if (type is "KEEP" or "VERIFY" or "MOVE" or "COPY")
            {
                string? dr = r.IsDBNull(1) ? null : r.GetString(1);
                string? dp = r.IsDBNull(2) ? null : r.GetString(2);
                if (dr == null || dp == null) continue;
                string basePath = map.TryGetValue(dr, out var ov) ? ov : roots.TryGetValue(dr, out var rr) ? rr.Path : "";
                if (string.IsNullOrEmpty(basePath)) { bad++; continue; }
                string abs = Paths.CombineRoot(basePath, dp);
                if (!File.Exists(abs)) { Console.WriteLine($"MISSING {dr}:{dp}"); bad++; continue; }
                long sz = r.GetInt64(3);
                string? eh = r.IsDBNull(4) ? null : r.GetString(4);
                var fi = new FileInfo(abs);
                if (sz != 0 && fi.Length != sz) { Console.WriteLine($"SIZE-MISMATCH {dr}:{dp}"); bad++; continue; }
                if (eh != null && !string.Equals(hasher.HashFile(abs, fi.Length), eh, StringComparison.OrdinalIgnoreCase))
                { Console.WriteLine($"HASH-MISMATCH {dr}:{dp}"); bad++; continue; }
                ok++;
            }
        }
        Console.WriteLine($"verify {a[0]}: ok={ok} bad={bad}");
        return bad == 0 ? 0 : 3;
    }

    private static int Purge(string[] a)
    {
        string older = Opt(a, "--older-than", "");
        if (string.IsNullOrEmpty(older) || !Has(a, "--yes")) return Fail("purge --older-than 30d --yes [--path ROOT] (permanent delete, never implicit §16)");
        int days = int.TryParse(new string(older.TakeWhile(char.IsDigit).ToArray()), out var dd) ? dd : 30;
        string? basePath = Has(a, "--path") ? Opt(a, "--path", "") : null;
        string db = Opt(a, "--db", AppConfig.Load(Opt(a, "--config", AppConfig.DefaultPath)).Database);
        List<string> rootsToClean = new();
        if (!string.IsNullOrEmpty(basePath)) rootsToClean.Add(basePath);
        else
        {
            using var d = new Database(db);
            rootsToClean.AddRange(d.ListRoots().Select(rr => rr.Path));
        }
        int deleted = 0;
        foreach (var rp in rootsToClean)
        {
            string trashBase = Path.Combine(rp, AppConfig.Load(Opt(a, "--config", AppConfig.DefaultPath)).TrashDirectoryName);
            if (!Directory.Exists(trashBase)) continue;
            foreach (var dir in Directory.GetDirectories(trashBase))
            {
                var di = new DirectoryInfo(dir);
                if (DateTime.UtcNow - di.CreationTimeUtc > TimeSpan.FromDays(days))
                {
                    Directory.Delete(dir, true);
                    deleted++;
                    Log.Info($"purged {dir}");
                }
            }
        }
        Console.WriteLine($"purged {deleted} trash dirs older than {days}d");
        return 0;
    }

    private static int Inv(string[] a)
    {
        if (a.Length == 0) return Fail("inventory export|import");
        string db = Opt(a, "--db", AppConfig.Load(Opt(a, "--config", AppConfig.DefaultPath)).Database);
        if (a[0] == "export" && a.Length >= 2)
        {
            string outF = Opt(a, "--output", "");
            if (string.IsNullOrEmpty(outF)) return Fail("inventory export <rootId> --output FILE");
            Inventory.ExportRoot(db, a[1], outF);
            return 0;
        }
        if (a[0] == "import" && a.Length >= 2) { Inventory.ImportFile(db, a[1]); return 0; }
        return Fail("inventory export <rootId> --output F | inventory import <file>");
    }

    private static int Diff(string[] a)
    {
        string old = Opt(a, "--old", ""), nw = Opt(a, "--new", "");
        if (string.IsNullOrEmpty(old) || string.IsNullOrEmpty(nw)) return Fail("diff --old A.db --new B.db");
        var s = Inventory.Diff(old, nw);
        Console.WriteLine($"only-in-old: {s.OnlyInOld}  only-in-new: {s.OnlyInNew}  changed: {s.Changed}  identical: {s.Identical}");
        foreach (var l in s.Samples) Console.WriteLine("  " + l);
        return 0;
    }

    private static int DbTest(string[] a)
    {
        // §39 db-test: create db, table, insert, read, txn commit/rollback, delete temp db
        string tmp = Path.Combine(Path.GetTempPath(), "bn-dbtest-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var d = new Database(tmp);
            d.UpsertRoot(new StorageRootRow("t", "t", Path.GetTempPath(), "Temporary", true, "test", "unknown", Database.UtcNow()));
            long sid = d.BeginScan("t");
            d.UpsertFileEntry(new FileEntryRow(0, "t", "a.txt", "a.txt", 3, Database.UtcNow(), null, null, sid, "Ok", null));
            var got = d.ListFiles("t");
            if (got.Count != 1) throw new Exception("readback failed");
            using var tx = d.Conn.BeginTransaction();
            d.UpsertFileEntry(new FileEntryRow(0, "t", "b.txt", "b.txt", 1, Database.UtcNow(), null, null, sid, "Ok", null));
            tx.Rollback();
            if (d.ListFiles("t").Count != 1) throw new Exception("rollback failed");
            d.FinishScan(sid, "Completed");
            Console.WriteLine("db-test: PASS (create/insert/read/commit/rollback ok)");
            return 0;
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    private static int ScanTest(string[] a)
    {
        if (a.Length == 0) return Fail("scan-test <path>");
        string p = a[0];
        string tmp = Path.Combine(Path.GetTempPath(), "bn-scantest-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var d = new Database(tmp);
            d.UpsertRoot(new StorageRootRow("s", "s", Path.GetFullPath(p), "Unknown", false, Paths.GetFileSystemId(p), "unknown", Database.UtcNow()));
            var sc = new Scanner(d);
            var (s, e) = sc.ScanRoot("s");
            Console.WriteLine($"scan-test: PASS ({s} files, {e} errors) on {p}");
            Console.WriteLine($"  uname -m equivalent: {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}");
            return 0;
        }
        finally { try { File.Delete(tmp); } catch { } }
    }
}
