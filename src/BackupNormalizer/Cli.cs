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
            backup-normalizer 0.1.0 — per-drive source/target comparison
              Usage: BackupNormalizer <command> [options]
              init [--db PATH] [--config PATH]
              root add <id> <path> [--name N] [--writable true|false] [--db PATH]
              root list [--db PATH]
              scan <rootId|--all> [--db PATH]
              hash --needed [--db PATH] [--parallelism N] | hash <rootId> --all [--db PATH]
              plan --source-db S.db --source-root R --target-db T.db --target-root R [--plan ID]
              plan show <plan-id> [--db PATH] | plan export <plan-id> [--format json] [--output F] [--db PATH]
              plan import <plan.json> [--db PATH] [--target-path ABS]
              plan conflicts <plan-id> [--db PATH]
              execute <plan-id> [--db PATH] [--source-path ABS] [--target-path ABS] [--resume] [--stop-on-error] [--yes]
              verify <plan-id> [--db PATH] [--source-path ABS] [--target-path ABS]
              purge --older-than 30d --yes [--db PATH] [--path ROOTPATH]
              diff --source-db S.db --source-root R --target-db T.db --target-root R
              db-test [--db PATH] | scan-test <path> | --version
            Each database can inventory one drive with multiple named roots. Select the source and target
            for each diff or plan. Automatic plans require fully scanned, disjoint roots.
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
            foreach (var r in d.ListRoots()) Console.WriteLine($"{r.Id}\t{r.Name}\t{r.Path}\twritable={r.Writable}");
            return 0;
        }
        if (a[0] == "add" && a.Length >= 3)
        {
            string id = a[1], path = Path.GetFullPath(a[2]);
            string name = Opt(a, "--name", id);
            bool writable = !string.Equals(Opt(a, "--writable", "true"), "false", StringComparison.OrdinalIgnoreCase);
            if (!Directory.Exists(path)) return Fail($"path not found: {path}");
            using var d = new Database(db);
            d.UpsertRoot(new StorageRootRow(id, name, path, writable, Paths.GetFileSystemId(path), Paths.DetectCaseSensitivity(path), Database.UtcNow()));
            Console.WriteLine($"root '{id}' -> {path}");
            return 0;
        }
        return Fail("root add <id> <path> [--name N] [--writable true|false] | root list");
    }

    private static int Scan(string[] a)
    {
        if (a.Length == 0) return Fail("scan <rootId|--all>");
        string db = Opt(a, "--db", AppConfig.Load(Opt(a, "--config", AppConfig.DefaultPath)).Database);
        string algo = Opt(a, "--hash-algo", AppConfig.Load(Opt(a, "--config", AppConfig.DefaultPath)).HashAlgorithm);
        using var d = new Database(db);
        var sc = new Scanner(d, algo);
        int totalErrors = 0;
        if (a[0] == "--all")
        {
            foreach (var r in d.ListRoots())
            {
                var (s, e) = sc.ScanRoot(r.Id);
                Console.WriteLine($"scan {r.Id}: {s} files, {e} errors ({(e == 0 ? "complete" : "incomplete")})");
                totalErrors += e;
            }
            return totalErrors == 0 ? 0 : 3;
        }
        var (scanned, errors) = sc.ScanRoot(a[0]);
        Console.WriteLine($"scan {a[0]}: {scanned} files, {errors} errors ({(errors == 0 ? "complete" : "incomplete")})");
        return errors == 0 ? 0 : 3;
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
            Console.WriteLine($"Source: {doc.SourceRoot} at {doc.SourcePath}");
            Console.WriteLine($"Target: {doc.TargetRoot} at {doc.TargetPath}");
            foreach (var o in doc.Operations) Console.WriteLine($"  {o.Id:D4} {o.Type,-7} {o.SourceKind}:{o.SourceRoot}:{o.SourcePath} -> {o.DestinationRoot}:{o.DestinationPath} size={o.ExpectedSize}");
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
        if (a[0] == "conflicts" && a.Length >= 2)
        {
            string db = Opt(a, "--db", AppConfig.Load(Opt(a, "--config", AppConfig.DefaultPath)).Database);
            using var d = new Database(db);
            if (!d.PlanExists(a[1])) return Fail($"unknown plan '{a[1]}'");
            var bad = d.ListPlanOperations(a[1], onlyProblems: true);
            if (bad.Count == 0) { Console.WriteLine($"plan {a[1]}: no conflicts or failures"); return 0; }
            Console.WriteLine($"plan {a[1]}: {bad.Count} problem(s)");
            foreach (var o in bad)
                Console.WriteLine($"  {o.Sequence:D4} {o.Status,-8} {o.Type,-7} {o.SourceRoot}:{o.SourcePath} -> {o.DestRoot}:{o.DestPath} : {o.Error}");
            return 3;
        }
        if (a[0] == "import" && a.Length >= 2)
        {
            // UI-generated plan JSON -> DB, so the same executor can run it.
            string db = Opt(a, "--db", AppConfig.Load(Opt(a, "--config", AppConfig.DefaultPath)).Database);
            string targetPath = Opt(a, "--target-path", "");
            var doc = PlanStaging.ImportJson(a[1]);
            using var d = new Database(db);
            string rootPath = targetPath;
            if (string.IsNullOrEmpty(rootPath)) rootPath = d.GetRoot(doc.TargetRoot)?.Path ?? doc.TargetPath;
            PlanStaging.WriteToDatabase(d, doc, doc.TargetRoot, rootPath);
            Console.WriteLine($"imported plan {doc.PlanId} ({doc.Operations.Count} ops) into {db}");
            return 0;
        }
        string sourceDbPath = Opt(a, "--source-db", "");
        string sourceRoot = Opt(a, "--source-root", "");
        string targetDbPath = Opt(a, "--target-db", "");
        string targetRoot = Opt(a, "--target-root", "");
        if (string.IsNullOrEmpty(sourceDbPath) || string.IsNullOrEmpty(sourceRoot)
            || string.IsNullOrEmpty(targetDbPath) || string.IsNullOrEmpty(targetRoot))
            return Fail("plan requires --source-db S.db --source-root R --target-db T.db --target-root R");
        string planId = Opt(a, "--plan", DateTime.UtcNow.ToString("yyyy-MM-dd-HHmmss"));
        using var td = new Database(targetDbPath);
        using var sd = Paths.PathEquals(sourceDbPath, targetDbPath) ? null : Database.OpenReadOnly(sourceDbPath);
        var result = new Planner(td).PlanFromRoots(sd ?? td, sourceRoot, targetRoot, planId);
        PrintPlan(result);
        return 0;
    }

    private static void PrintPlan(Planner.PlanResult r)
    {
        Console.WriteLine($"Plan {r.PlanId}");
        Console.WriteLine($"KEEP {r.Keep}  MOVE {r.Move}  COPY {r.Copy}  TRASH {r.Trash}  MKDIR {r.Mkdir}");
        Console.WriteLine($"Bytes requiring actual copying: {r.BytesToCopy}");
        Console.WriteLine($"Bytes avoided through moves: {r.BytesAvoided}");
        Console.WriteLine("No files were modified.");
    }

    private static int Execute(string[] a)
    {
        if (a.Length == 0) return Fail("execute <plan-id>");
        string db = Opt(a, "--db", AppConfig.Load(Opt(a, "--config", AppConfig.DefaultPath)).Database);
        string? sourcePath = Has(a, "--source-path") ? Opt(a, "--source-path", "") : null;
        string? targetPath = Has(a, "--target-path") ? Opt(a, "--target-path", "") : null;
        using var d = new Database(db);
        if (!d.PlanExists(a[0])) return Fail($"unknown plan '{a[0]}'");
        if (!Has(a, "--yes"))
        {
            var (counts, est) = PlanSummary(d, a[0]);
            int total = counts.Values.Sum();
            Console.WriteLine($"Plan {a[0]}: " +
                string.Join("  ", counts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}")) +
                $"  estBytes={est}");
            Console.Write($"Execute {total} operations ({est} bytes to copy)? [y/N] ");
            string? ans = Console.ReadLine();
            if (!string.Equals(ans?.Trim(), "y", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(ans?.Trim(), "yes", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Aborted (nothing was modified). Use --yes to approve non-interactively.");
                return 2;
            }
        }
        var sum = new Executor(d).Execute(a[0], sourcePath, targetPath, Has(a, "--resume"), Has(a, "--stop-on-error"));
        Console.WriteLine($"execute {a[0]}: completed={sum.Completed} failed={sum.Failed} conflicts={sum.Conflicts}");
        return sum.Failed == 0 && sum.Conflicts == 0 ? 0 : 3;
    }

    private static (Dictionary<string, int> Counts, long EstBytes) PlanSummary(Database db, string planId)
    {
        var counts = db.GetOperationCounts(planId);
        long est = db.GetPlan(planId)?.EstimatedBytesCopied ?? 0L;
        return (counts, est);
    }

    private static int Verify(string[] a)
    {
        if (a.Length == 0) return Fail("verify <plan-id>");
        string db = Opt(a, "--db", AppConfig.Load(Opt(a, "--config", AppConfig.DefaultPath)).Database);
        string? sourcePathOverride = Has(a, "--source-path") ? Opt(a, "--source-path", "") : null;
        string? targetPathOverride = Has(a, "--target-path") ? Opt(a, "--target-path", "") : null;
        using var d = new Database(db);
        var plan = d.GetPlan(a[0]);
        if (plan == null) return Fail($"unknown plan '{a[0]}'");
        string sourcePath = Path.GetFullPath(sourcePathOverride ?? plan.SourceRootPath);
        string targetPath = Path.GetFullPath(targetPathOverride ?? plan.TargetRootPath);
        int ok = 0, bad = 0;
        var hasher = HasherFactory.Create(null);
        var operations = d.ListPlanOperations(a[0]);
        if (operations.Any(operation => operation.SourceKind == SourceScope.Source) && Paths.RootsOverlap(sourcePath, targetPath))
            return Fail("source and target paths overlap; verification requires disjoint roots");
        foreach (var operation in operations)
        {
            string type = operation.Type;
            if (type is OpType.Keep or OpType.Verify or OpType.Move or OpType.Copy)
            {
                string? dp = operation.DestPath;
                if (operation.DestRoot != plan.TargetRootId || dp == null) continue;
                string abs;
                try { abs = Paths.CombineRoot(targetPath, dp); }
                catch (InvalidOperationException) { Console.WriteLine($"INVALID-PATH {dp}"); bad++; continue; }
                if (!File.Exists(abs)) { Console.WriteLine($"MISSING {plan.TargetRootId}:{dp}"); bad++; continue; }
                long sz = operation.ExpectedSize;
                string? eh = operation.ExpectedHash;
                var fi = new FileInfo(abs);
                if (sz != 0 && fi.Length != sz) { Console.WriteLine($"SIZE-MISMATCH {plan.TargetRootId}:{dp}"); bad++; continue; }
                if (eh != null && !string.Equals(hasher.HashFile(abs, fi.Length), eh, StringComparison.OrdinalIgnoreCase))
                { Console.WriteLine($"HASH-MISMATCH {plan.TargetRootId}:{dp}"); bad++; continue; }
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

    private static int Diff(string[] a)
    {
        string sourceDb = Opt(a, "--source-db", ""), sourceRoot = Opt(a, "--source-root", "");
        string targetDb = Opt(a, "--target-db", ""), targetRoot = Opt(a, "--target-root", "");
        if (string.IsNullOrEmpty(sourceDb) || string.IsNullOrEmpty(sourceRoot)
            || string.IsNullOrEmpty(targetDb) || string.IsNullOrEmpty(targetRoot))
            return Fail("diff requires --source-db S.db --source-root R --target-db T.db --target-root R");
        var s = Inventory.Diff(sourceDb, sourceRoot, targetDb, targetRoot);
        Console.WriteLine($"source-only: {s.SourceOnly}  target-only: {s.TargetOnly}  changed: {s.Changed}  identical: {s.Identical}  unverified: {s.Unverified}");
        Console.WriteLine($"scan status: source={s.SourceScanStatus ?? "never scanned"} target={s.TargetScanStatus ?? "never scanned"}");
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
            d.UpsertRoot(new StorageRootRow("t", "t", Path.GetTempPath(), true, "test", "unknown", Database.UtcNow()));
            long sid = d.BeginScan("t");
            d.UpsertFileEntry(new FileEntryRow(0, "t", "a.txt", "a.txt", 3, Database.UtcNow(), null, null, sid, FileStatus.Ok, null));
            var got = d.ListFiles("t");
            if (got.Count != 1) throw new Exception("readback failed");
            using var tx = d.BeginTransaction();
            d.UpsertFileEntry(new FileEntryRow(0, "t", "b.txt", "b.txt", 1, Database.UtcNow(), null, null, sid, FileStatus.Ok, null));
            tx.Rollback();
            if (d.ListFiles("t").Count != 1) throw new Exception("rollback failed");
            d.FinishScan(sid, ScanStatus.Completed);
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
            d.UpsertRoot(new StorageRootRow("s", "s", Path.GetFullPath(p), false, Paths.GetFileSystemId(p), "unknown", Database.UtcNow()));
            var sc = new Scanner(d);
            var (s, e) = sc.ScanRoot("s");
            Console.WriteLine($"scan-test: PASS ({s} files, {e} errors) on {p}");
            Console.WriteLine($"  uname -m equivalent: {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}");
            return 0;
        }
        finally { try { File.Delete(tmp); } catch { } }
    }
}
