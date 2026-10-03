using BackupNormalizer.Ui.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace BackupNormalizer.Tests;

[Collection("Console")]
public sealed class ReviewFixTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "bn-review-fixes-" + Guid.NewGuid().ToString("N")
    );

    public ReviewFixTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch { }
    }

    private string Root(string name)
    {
        string path = Path.Combine(_dir, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Put(string root, string rel, string text = "payload")
    {
        string path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private static PlanDoc MovePlan(string root, string planId = "move")
    {
        string source = Put(root, "old/a.txt");
        return PlanStaging.BuildPlanDoc(
            planId,
            "disk",
            root,
            PlanStaging.StageMove(root, source, Path.Combine(root, "new"))
        );
    }

    private static void Register(Database db, string root) =>
        db.UpsertRoot(
            new StorageRootRow("disk", "disk", root, true, "fs", "unknown", Database.UtcNow())
        );

    [Fact]
    public void Staged_Base_Cannot_Be_Redirected_By_Editing_Or_Applying_Text()
    {
        string a = Root("A"),
            b = Root("B");
        string source = Put(a, "old/a.txt");
        Put(b, "old/a.txt");
        var vm = new MainViewModel
        {
            BasePath = a,
            PlanId = "base",
            JsonPath = Path.Combine(_dir, "base.json"),
            DbPath = Path.Combine(_dir, "base.db"),
        };
        vm.ApplyBase();
        vm.StageMovePaths([source], Path.Combine(a, "new"));
        Assert.False(vm.CanChangeBase);
        Assert.False(vm.ApplyBaseCommand.CanExecute(null));

        vm.BasePath = b;
        vm.ApplyBase();
        Assert.Equal(a, vm.AppliedBasePath);
        Assert.Equal(a, vm.Left.CurrentPath);
        Assert.Equal(a, vm.Right.CurrentPath);
        vm.SaveJson();
        Assert.Equal(a, PlanStaging.ImportJson(vm.JsonPath).TargetPath);
        vm.WriteToDb();
        using var db = new Database(vm.DbPath);
        Assert.Equal(a, db.GetPlan("base")!.TargetRootPath);
        Assert.Equal(0, new Executor(db).Execute("base").Conflicts);
        Assert.True(File.Exists(Path.Combine(a, "new/a.txt")));
        Assert.True(File.Exists(Path.Combine(b, "old/a.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Empty_Queue_Unlocks_Base_And_Removes_Virtual_Folders(bool removeLast)
    {
        string a = Root("A"),
            b = Root("B");
        var vm = new MainViewModel { BasePath = a };
        vm.ApplyBase();
        vm.StageMkdirFromDialog("virtual");
        string virtualPath = Assert.Single(vm.VirtualDirs);
        vm.Left.CurrentPath = virtualPath;
        if (removeLast)
        {
            vm.RemoveStaged(Assert.Single(vm.Staged));
        }
        else
        {
            vm.ClearStaged();
        }

        Assert.Empty(vm.VirtualDirs);
        Assert.Empty(vm.Staged);
        Assert.True(vm.CanChangeBase);
        Assert.True(vm.ApplyBaseCommand.CanExecute(null));
        Assert.Equal(a, vm.Left.CurrentPath);
        Assert.DoesNotContain(vm.Right.Entries, e => e.FullPath == virtualPath);
        vm.BasePath = b;
        vm.ApplyBase();
        Assert.Equal(b, vm.AppliedBasePath);
    }

    [Fact]
    public void Invalid_Apply_Preserves_The_Applied_Base_And_Panels()
    {
        string root = Root("A");
        var vm = new MainViewModel { BasePath = root };
        vm.ApplyBase();
        vm.BasePath = Path.Combine(_dir, "missing");
        vm.ApplyBase();
        Assert.Equal(root, vm.AppliedBasePath);
        Assert.Equal(root, vm.Left.CurrentPath);
        Assert.Equal(root, vm.Right.CurrentPath);
        vm.StageMkdirFromDialog("new");
        Assert.Contains(Path.Combine(root, "new"), vm.VirtualDirs);
    }

    [Fact]
    public void Copy_Then_Move_Order_Survives_Json_And_Database_Import()
    {
        string root = Root("ordered");
        string source = Put(root, "old/a.txt");
        var staged = PlanStaging.StageCopy(root, source, Path.Combine(root, "copied"));
        staged.AddRange(PlanStaging.StageMove(root, source, Path.Combine(root, "moved")));
        var doc = PlanStaging.BuildPlanDoc("ordered", "disk", root, staged);
        Assert.Equal(
            new[] { OpType.Mkdir, OpType.Copy, OpType.Mkdir, OpType.Move },
            doc.Operations.Select(o => o.Type)
        );
        string json = Path.Combine(_dir, "ordered.json");
        File.WriteAllText(json, PlanStaging.ToJson(doc));
        using var db = new Database(Path.Combine(_dir, "ordered.db"));
        PlanStaging.WriteToDatabase(db, PlanStaging.ImportJson(json), "disk", root);
        Assert.Equal(
            doc.Operations.Select(o => o.Type),
            db.ListPlanOperations(doc.PlanId).Select(o => o.Type)
        );
        var result = new Executor(db).Execute(doc.PlanId);
        Assert.Equal(0, result.Conflicts + result.Failed);
        Assert.Equal("payload", File.ReadAllText(Path.Combine(root, "copied/a.txt")));
        Assert.Equal("payload", File.ReadAllText(Path.Combine(root, "moved/a.txt")));
    }

    [Fact]
    public void Completed_Execution_Rejects_Rebasing_And_Fresh_Import_Replays()
    {
        string a = Root("A"),
            b = Root("B");
        var doc = MovePlan(a);
        Put(b, "old/a.txt");
        string json = Path.Combine(_dir, "move.json");
        File.WriteAllText(json, PlanStaging.ToJson(doc));
        using var original = new Database(Path.Combine(_dir, "a.db"));
        PlanStaging.WriteToDatabase(original, doc, "disk", a);
        Assert.Equal(0, new Executor(original).Execute(doc.PlanId).Conflicts);
        var before = original.ListPlanOperations(doc.PlanId);
        int logsBefore = original.Context.ExecutionLogs.Count();
        var error = Assert.Throws<InvalidOperationException>(() =>
            new Executor(original).Execute(doc.PlanId, targetPathOverride: b)
        );
        Assert.Contains("fresh database", error.Message);
        Assert.Equal(before, original.ListPlanOperations(doc.PlanId));
        Assert.Equal(logsBefore, original.Context.ExecutionLogs.Count());
        Assert.True(File.Exists(Path.Combine(b, "old/a.txt")));
        Assert.False(Directory.Exists(Path.Combine(b, "new")));

        using var replay = new Database(Path.Combine(_dir, "b.db"));
        PlanStaging.WriteToDatabase(replay, PlanStaging.ImportJson(json), "disk", b);
        Assert.Null(replay.GetPlan(doc.PlanId)!.ExecutionTargetRootPath);
        Assert.All(
            replay.ListPlanOperations(doc.PlanId),
            o => Assert.Equal(OpStatus.Planned, o.Status)
        );
        Assert.Equal(0, new Executor(replay).Execute(doc.PlanId).Conflicts);
        Assert.True(File.Exists(Path.Combine(b, "new/a.txt")));
    }

    [Fact]
    public void First_Override_Is_Persisted_And_Used_By_Retry_And_Verify()
    {
        string a = Root("A"),
            b = Root("B");
        var doc = MovePlan(a);
        Put(b, "old/a.txt");
        string dbPath = Path.Combine(_dir, "override.db");
        using (var db = new Database(dbPath))
        {
            PlanStaging.WriteToDatabase(db, doc, "disk", a);
            Assert.Equal(0, new Executor(db).Execute(doc.PlanId, targetPathOverride: b).Conflicts);
            Assert.Equal(b, db.GetPlan(doc.PlanId)!.ExecutionTargetRootPath);
            Assert.Null(db.GetPlan(doc.PlanId)!.ExecutionSourceRootPath);
        }
        using (var reopened = new Database(dbPath))
        {
            Assert.Equal(2, new Executor(reopened).Execute(doc.PlanId, resume: true).Completed);
            Assert.Equal(
                2,
                new Executor(reopened)
                    .Execute(doc.PlanId, targetPathOverride: b + Path.DirectorySeparatorChar)
                    .Completed
            );
        }
        Assert.Equal(0, Cli.Run(["verify", doc.PlanId, "--db", dbPath]));
        Assert.Equal(3, Cli.Run(["verify", doc.PlanId, "--db", dbPath, "--target-path", a]));
        Assert.True(File.Exists(Path.Combine(a, "old/a.txt")));
    }

    [Fact]
    public void Partial_Execution_Resumes_At_Its_Recorded_Root()
    {
        string a = Root("A"),
            b = Root("B");
        var doc = MovePlan(a);
        string sourceB = Put(b, "old/a.txt", "changed");
        using var db = new Database(Path.Combine(_dir, "partial.db"));
        PlanStaging.WriteToDatabase(db, doc, "disk", a);
        Assert.Equal(1, new Executor(db).Execute(doc.PlanId, targetPathOverride: b).Conflicts);
        Assert.Throws<InvalidOperationException>(() =>
            new Executor(db).Execute(doc.PlanId, targetPathOverride: a, resume: true)
        );
        File.WriteAllText(sourceB, "payload");
        Assert.Equal(0, new Executor(db).Execute(doc.PlanId, resume: true).Conflicts);
        Assert.True(File.Exists(Path.Combine(b, "new/a.txt")));
    }

    [Fact]
    public void Source_Overrides_Are_Bound_For_Source_Copy_Operations()
    {
        string hint = Root("hint"),
            source = Root("source"),
            other = Root("other"),
            target = Root("target");
        string file = Put(source, "a.txt");
        var hasher = HasherFactory.Create(null);
        string hash = hasher.HashFile(file, new FileInfo(file).Length);
        var doc = new PlanDoc(
            "source-copy",
            Database.UtcNow(),
            7,
            null,
            "s",
            hint,
            "t",
            target,
            [new PlanOpDoc(1, OpType.Copy, SourceScope.Source, "s", "a.txt", "t", "a.txt", 7, hash)]
        );
        using var db = new Database(Path.Combine(_dir, "source-copy.db"));
        PlanStaging.WriteToDatabase(db, doc, "t", target);
        Assert.Equal(0, new Executor(db).Execute(doc.PlanId, sourcePathOverride: source).Conflicts);
        Assert.Equal(source, db.GetPlan(doc.PlanId)!.ExecutionSourceRootPath);
        Assert.Throws<InvalidOperationException>(() =>
            new Executor(db).Execute(doc.PlanId, sourcePathOverride: other)
        );
        Assert.Equal(1, new Executor(db).Execute(doc.PlanId, resume: true).Completed);
    }

    [Fact]
    public void Historical_Execution_Without_Binding_Requires_Fresh_Import()
    {
        string root = Root("historical");
        var doc = MovePlan(root);
        using var db = new Database(Path.Combine(_dir, "historical.db"));
        PlanStaging.WriteToDatabase(db, doc, "disk", root);
        var op = db.ListPlanOperations(doc.PlanId)[0];
        db.MarkOperation(op.Id, OpStatus.Completed, null, Database.UtcNow());
        var error = Assert.Throws<InvalidOperationException>(() =>
            new Executor(db).Execute(doc.PlanId)
        );
        Assert.Contains("historical execution", error.Message);
        Assert.Null(db.GetPlan(doc.PlanId)!.ExecutionTargetRootPath);
        Assert.True(File.Exists(Path.Combine(root, "old/a.txt")));
    }

    [Theory]
    [InlineData("intact")]
    [InlineData("missing")]
    [InlineData("changed")]
    [InlineData("self")]
    public void Imported_Keep_Is_A_Trash_Survivor_Only_While_Valid(string state)
    {
        string root = Root("trash");
        string survivor = Put(root, "keep.txt");
        string victim = Put(root, "dup.txt");
        string hash = HasherFactory.Create(null).HashFile(victim, 7);
        string keepRel = state == "self" ? "dup.txt" : "keep.txt";
        var doc = PlanStaging.BuildPlanDoc(
            "trash",
            "disk",
            root,
            [
                new PlanStaging.StagedOp(OpType.Keep, keepRel, keepRel, 7, hash),
                new PlanStaging.StagedOp(OpType.Trash, "dup.txt", null, 7, hash),
            ]
        );
        string json = Path.Combine(_dir, "trash.json");
        File.WriteAllText(json, PlanStaging.ToJson(doc));
        using var db = new Database(Path.Combine(_dir, "trash.db"));
        PlanStaging.WriteToDatabase(db, PlanStaging.ImportJson(json), "disk", root);
        Assert.Empty(db.ListFiles());
        if (state is "missing" or "changed")
        {
            db.BindPlanExecution(doc.PlanId, null, root);
            db.MarkOperation(
                db.ListPlanOperations(doc.PlanId)[0].Id,
                OpStatus.Completed,
                null,
                Database.UtcNow()
            );
            if (state == "missing")
            {
                File.Delete(survivor);
            }
            else
            {
                File.WriteAllText(survivor, "CHANGED");
            }
        }
        var result = new Executor(db).Execute(doc.PlanId, resume: true);
        Assert.Equal(0, result.Failed);
        Assert.Equal(state == "intact" ? 0 : 1, result.Conflicts);
        Assert.Equal(state != "intact", File.Exists(victim));
        if (state == "intact")
        {
            Assert.Equal("payload", File.ReadAllText(survivor));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void In_Root_Database_Is_Excluded_From_Recursive_And_Metadata_Scans(bool metadata)
    {
        string root = Root("inventory");
        Put(root, "a.txt");
        Put(root, "ordinary.db", "ordinary content");
        using var db = new Database(Path.Combine(root, "inventory.db"));
        Register(db, root);
        var scanner = metadata
            ? new Scanner(
                db,
                path =>
                    new[]
                    {
                        "a.txt",
                        "ordinary.db",
                        "inventory.db",
                        "inventory.db-wal",
                        "inventory.db-shm",
                        "inventory.db-journal",
                    }.Select(rel =>
                    {
                        var file = new FileInfo(Path.Combine(path, rel));
                        return rel is "a.txt" or "ordinary.db"
                            ? new FsEntry(
                                file.FullName,
                                false,
                                file.Length,
                                file.LastWriteTimeUtc,
                                file.CreationTimeUtc,
                                true,
                                false,
                                null
                            )
                            : new FsEntry(
                                file.FullName,
                                false,
                                0,
                                default,
                                default,
                                true,
                                false,
                                "database state must be excluded even after a metadata error"
                            );
                    })
            )
            : new Scanner(db);
        Assert.Equal((2, 0), scanner.ScanRoot("disk"));
        Assert.Equal(2, scanner.HashNeeded("disk", true, 1).hashed);
        Assert.Equal(
            new[] { "a.txt", "ordinary.db" },
            db.ListFilesWithHashes("disk", "sha256").Select(f => f.RelativePath)
        );

        string target = Root("target");
        using var td = new Database(Path.Combine(target, "target.db"));
        Register(td, target);
        Assert.Equal(0, new Scanner(td).ScanRoot("disk").errors);
        Assert.Equal(2, new Planner(td).PlanFromRoots(db, "disk", "disk", "inventory-plan").Copy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Polluted_Database_Entries_Are_Retired_By_Scan_Or_Hash(bool hashOnly)
    {
        string root = Root("polluted");
        using var db = new Database(Path.Combine(root, "inventory.db"));
        Register(db, root);
        foreach (
            string rel in new[]
            {
                "inventory.db",
                "inventory.db-wal",
                "inventory.db-shm",
                "inventory.db-journal",
            }
        )
        {
            long id = db.UpsertFileEntry(
                new FileEntryRow(
                    0,
                    "disk",
                    rel,
                    rel,
                    1,
                    Database.UtcNow(),
                    null,
                    null,
                    0,
                    FileStatus.Ok,
                    null
                )
            );
            db.UpsertHash(
                new FileHashRow(
                    id,
                    "sha256",
                    "old-digest",
                    1,
                    Database.UtcNow(),
                    Database.UtcNow(),
                    HashState.Ok
                )
            );
        }
        var scanner = new Scanner(db);
        if (hashOnly)
        {
            Assert.Equal(0, scanner.HashNeeded("disk", true, 1).hashed);
        }
        else
        {
            Assert.Equal((0, 0), scanner.ScanRoot("disk"));
        }

        Assert.All(
            db.ListFiles(),
            f =>
            {
                Assert.Equal(FileStatus.Missing, f.Status);
                Assert.Equal(HashState.Stale, db.GetHash(f.Id, "sha256")!.State);
            }
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Enumeration_Failure_Does_Not_Mark_Unseen_Inventory_Missing(bool throwReadError)
    {
        string root = Root("scan-failure");
        Put(root, "a.txt");
        Put(root, "b.txt");
        using var db = new Database(Path.Combine(_dir, "scan.db"));
        Register(db, root);
        new Scanner(db).ScanRoot("disk");
        IEnumerable<FsEntry> Entries(string path)
        {
            yield return new FsEntry(
                Path.Combine(path, "a.txt"),
                false,
                7,
                DateTime.UtcNow,
                DateTime.UtcNow,
                true,
                false,
                null
            );
            if (throwReadError)
            {
                throw new IOException("simulated native read failure");
            }

            yield return new FsEntry(
                path,
                false,
                0,
                default,
                default,
                false,
                false,
                "simulated incomplete enumeration"
            );
        }
        var scanner = new Scanner(db, Entries);
        if (throwReadError)
        {
            Assert.Throws<IOException>(() => scanner.ScanRoot("disk"));
        }
        else
        {
            Assert.Equal(1, scanner.ScanRoot("disk").errors);
        }

        Assert.Equal(
            throwReadError ? ScanStatus.Failed : ScanStatus.Incomplete,
            db.LatestScanStatus("disk")
        );
        Assert.Equal(FileStatus.Ok, db.GetFileEntry("disk", "b.txt")!.Status);
    }
}
