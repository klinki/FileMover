using System.Collections.Generic;
using System.Linq;
using BackupNormalizer;
using BackupNormalizer.Ui.Models;
using BackupNormalizer.Ui.ViewModels;

namespace BackupNormalizer.Tests;

public sealed class UiDatabasePlanTests : IDisposable
{
    private const string Modified = "2026-10-01T12:00:00.0000000Z";
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "bn-ui-database-plan-" + Guid.NewGuid().ToString("N")
    );

    public UiDatabasePlanTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Planning_Uses_Whole_Selected_Roots_And_Leaves_Both_Inventories_Unchanged()
    {
        string sourceRoot = NewRoot("source");
        string targetRoot = NewRoot("target");
        Write(sourceRoot, "root.txt", "one");
        Write(sourceRoot, "nested/child.txt", "two");
        string sourceDb = CreateDatabase("source", "source-id", sourceRoot);
        string targetDb = CreateDatabase("target", "target-id", targetRoot);
        var sourceBefore = DatabaseBytes(sourceDb);
        var targetBefore = DatabaseBytes(targetDb);
        var tempPlansBefore = TemporaryPlanDirectories();

        var vm = LoadPanels(sourceDb, targetDb);
        vm.PlanId = "whole-root-plan";
        vm.Left.NavigateInventory("nested");
        var review = await vm.PreparePlanAsync(DatabasePlanDirection.LeftToRight);
        if (review is null)
        {
            throw new Xunit.Sdk.XunitException(vm.StatusMessage);
        }

        Assert.Equal(Path.GetFullPath(sourceDb), review.Document.SourceDatabasePath);
        Assert.Equal(Path.GetFullPath(sourceRoot), review.Document.SourcePath);
        Assert.Equal(Path.GetFullPath(targetRoot), review.Document.TargetPath);
        Assert.Equal(DatabasePlanDirection.LeftToRight, review.Direction);
        Assert.Equal(2, review.CopyCount);
        Assert.Equal(6, review.EstimatedBytesCopied);
        Assert.Contains(
            review.Operations,
            operation => operation.Source.EndsWith("root.txt", StringComparison.Ordinal)
        );
        Assert.Contains(
            review.Operations,
            operation =>
                operation.Source.EndsWith(
                    "nested" + Path.DirectorySeparatorChar + "child.txt",
                    StringComparison.Ordinal
                )
        );
        AssertDatabaseUnchanged(sourceBefore, sourceDb);
        AssertDatabaseUnchanged(targetBefore, targetDb);
        Assert.Equal(
            tempPlansBefore.OrderBy(path => path),
            TemporaryPlanDirectories().OrderBy(path => path)
        );
        Assert.True(File.Exists(Path.Combine(sourceRoot, "root.txt")));
        Assert.False(File.Exists(Path.Combine(targetRoot, "root.txt")));
    }

    [Fact]
    public async Task Reverse_Direction_Uses_Right_Root_As_Source_And_JSON_RoundTrips()
    {
        string leftRoot = NewRoot("left");
        string rightRoot = NewRoot("right");
        Write(leftRoot, "left-only.txt", "left");
        Write(rightRoot, "right-only.txt", "right");
        string leftDb = CreateDatabase("left", "left-id", leftRoot);
        string rightDb = CreateDatabase("right", "right-id", rightRoot);
        var beforeLeft = DatabaseBytes(leftDb);
        var beforeRight = DatabaseBytes(rightDb);
        var vm = LoadPanels(leftDb, rightDb);
        vm.PlanId = "reverse-plan";

        var review = await vm.PreparePlanAsync(DatabasePlanDirection.RightToLeft);
        if (review is null)
        {
            throw new Xunit.Sdk.XunitException(vm.StatusMessage);
        }

        Assert.Equal("Right → Left", review.DirectionLabel);
        Assert.Equal(Path.GetFullPath(rightDb), review.Document.SourceDatabasePath);
        Assert.Equal(Path.GetFullPath(rightRoot), review.Document.SourcePath);
        Assert.Equal(Path.GetFullPath(leftRoot), review.Document.TargetPath);
        var copy = Assert.Single(
            review.Document.Operations,
            operation => operation.Type == OpType.Copy
        );
        Assert.Equal(SourceScope.Source, copy.SourceKind);
        Assert.Equal("right-only.txt", copy.SourcePath);

        review.ExportJson(leftDb);
        Assert.Contains("Cannot export plan JSON", review.StatusMessage);
        AssertDatabaseUnchanged(beforeLeft, leftDb);
        AssertDatabaseUnchanged(beforeRight, rightDb);

        string jsonPath = Path.Combine(_dir, "reverse-plan.json");
        review.ExportJson(jsonPath);
        Assert.True(File.Exists(jsonPath));
        var imported = PlanStaging.ImportJson(jsonPath);
        Assert.Equal(review.Document.PlanId, imported.PlanId);
        Assert.Equal(review.Document.SourceDatabasePath, imported.SourceDatabasePath);
        Assert.Equal(review.Document.Operations, imported.Operations);

        string importedDb = Path.Combine(_dir, "imported.db");
        using (var db = Database.OpenWritable(importedDb, pooling: false))
        {
            PlanStaging.WriteToDatabase(db, imported, imported.TargetRoot, imported.TargetPath);
        }

        using (var db = Database.OpenReadOnly(importedDb, pooling: false))
        {
            var plan = Assert.IsType<Database.PlanInfo>(db.GetPlan("reverse-plan"));
            Assert.Equal(Path.GetFullPath(rightRoot), plan.SourceRootPath);
            Assert.Equal(Path.GetFullPath(leftRoot), plan.TargetRootPath);
            Assert.Equal(imported.Operations.Count, db.ListPlanOperations("reverse-plan").Count);
        }
        AssertDatabaseUnchanged(beforeLeft, leftDb);
        AssertDatabaseUnchanged(beforeRight, rightDb);
        Assert.True(File.Exists(Path.Combine(rightRoot, "right-only.txt")));
        Assert.False(File.Exists(Path.Combine(leftRoot, "right-only.txt")));
    }

    [Theory]
    [InlineData(ScanStatus.Incomplete, true)]
    [InlineData(ScanStatus.Completed, false)]
    public async Task Planner_Readiness_Failures_Leave_Inventories_And_Temporary_Directory_Clean(
        string sourceScanStatus,
        bool hashSource
    )
    {
        string sourceRoot = NewRoot("not-ready-source");
        string targetRoot = NewRoot("not-ready-target");
        Write(sourceRoot, "file.txt", "contents");
        string sourceDb = CreateDatabase(
            "not-ready-source",
            "s",
            sourceRoot,
            hash: hashSource,
            scanStatus: sourceScanStatus
        );
        string targetDb = CreateDatabase("not-ready-target", "t", targetRoot);
        var sourceBefore = DatabaseBytes(sourceDb);
        var targetBefore = DatabaseBytes(targetDb);
        var tempPlansBefore = TemporaryPlanDirectories();
        var vm = LoadPanels(sourceDb, targetDb);
        vm.PlanId = "blocked-plan";

        var review = await vm.PreparePlanAsync(DatabasePlanDirection.LeftToRight);

        Assert.Null(review);
        Assert.Contains(
            sourceScanStatus == ScanStatus.Incomplete
                ? "complete successful scan"
                : "not fully hashed",
            vm.StatusMessage
        );
        AssertDatabaseUnchanged(sourceBefore, sourceDb);
        AssertDatabaseUnchanged(targetBefore, targetDb);
        Assert.Equal(
            tempPlansBefore.OrderBy(path => path).ToArray(),
            TemporaryPlanDirectories().OrderBy(path => path).ToArray()
        );
    }

    [Fact]
    public async Task Link_Collision_Is_Reviewed_As_Skipped_And_Does_Not_Block_Export()
    {
        string sourceRoot = NewRoot("link-source");
        string targetRoot = NewRoot("link-target");
        Write(sourceRoot, "linked.txt", "source data");
        Write(sourceRoot, "copy.txt", "copy data");
        string sourceDb = CreateDatabase("link-source", "s", sourceRoot);
        string targetDb = CreateDatabase("link-target", "t", targetRoot);
        using (var db = Database.OpenWritable(targetDb, pooling: false))
        {
            var scan = db.LatestScan("t")!;
            db.UpsertFileEntry(
                new FileEntryRow(
                    0,
                    "t",
                    "linked.txt",
                    "linked.txt",
                    0,
                    Modified,
                    null,
                    null,
                    scan.Id,
                    FileStatus.Ok,
                    null,
                    EntryKind.FileLink,
                    "elsewhere",
                    null,
                    "fixture link"
                )
            );
        }
        var vm = LoadPanels(sourceDb, targetDb);
        vm.PlanId = "link-plan";

        var review = await vm.PreparePlanAsync(DatabasePlanDirection.LeftToRight);
        if (review is null)
        {
            throw new Xunit.Sdk.XunitException(vm.StatusMessage);
        }

        Assert.True(review.SkippedLinkCount > 0);
        Assert.Contains(
            review.Issues,
            issue => issue.Kind == "Skipped link" && !issue.BlocksExport
        );
        Assert.Contains(
            review.Operations,
            operation =>
                operation.Type == OpType.Copy
                && operation.Source.EndsWith("copy.txt", StringComparison.Ordinal)
        );
        Assert.True(review.CanExportJson);
        string jsonPath = Path.Combine(_dir, "links.json");
        review.ExportJson(jsonPath);
        Assert.True(File.Exists(jsonPath));
    }

    [Fact]
    public async Task Known_Content_Mismatch_Is_Explained_By_Verify_Without_Blocking_Safe_Export()
    {
        string sourceRoot = NewRoot("content-source");
        string targetRoot = NewRoot("content-target");
        Write(sourceRoot, "same.txt", "source content");
        Write(targetRoot, "same.txt", "target content");
        string sourceDb = CreateDatabase("content-source", "s", sourceRoot);
        string targetDb = CreateDatabase("content-target", "t", targetRoot);
        var sourceBefore = DatabaseBytes(sourceDb);
        var targetBefore = DatabaseBytes(targetDb);

        var vm = LoadPanels(sourceDb, targetDb);
        vm.PlanId = "content-conflict-plan";
        var review = await vm.PreparePlanAsync(DatabasePlanDirection.LeftToRight);
        if (review is null)
        {
            throw new Xunit.Sdk.XunitException(vm.StatusMessage);
        }

        Assert.Equal(1, review.ContentConflictCount);
        Assert.Equal(1, review.VerifyCount);
        Assert.True(review.CanExportJson);
        Assert.Contains(
            review.Issues,
            issue =>
                issue.Kind == "Content conflict"
                && issue.Message.Contains("without overwriting", StringComparison.Ordinal)
        );
        AssertDatabaseUnchanged(sourceBefore, sourceDb);
        AssertDatabaseUnchanged(targetBefore, targetDb);
        string jsonPath = Path.Combine(_dir, "content-conflict.json");
        review.ExportJson(jsonPath);
        Assert.True(File.Exists(jsonPath));
        Assert.Equal("source content", File.ReadAllText(Path.Combine(sourceRoot, "same.txt")));
        Assert.Equal("target content", File.ReadAllText(Path.Combine(targetRoot, "same.txt")));
    }

    [Fact]
    public async Task File_Directory_Type_Conflict_Explains_Why_Export_Is_Blocked()
    {
        string sourceRoot = NewRoot("type-source");
        string targetRoot = NewRoot("type-target");
        Write(sourceRoot, "node", "source file");
        Write(targetRoot, "node/child.txt", "target file");
        string sourceDb = CreateDatabase("type-source", "s", sourceRoot);
        string targetDb = CreateDatabase("type-target", "t", targetRoot);
        var review = await LoadPanels(sourceDb, targetDb)
            .PreparePlanAsync(DatabasePlanDirection.LeftToRight);
        if (review is null)
        {
            throw new Xunit.Sdk.XunitException(
                "Type-conflict plan unexpectedly failed during build."
            );
        }

        Assert.True(review.TypeConflictCount > 0);
        Assert.True(review.HasBlockingIssues);
        Assert.False(review.CanExportJson);
        Assert.Contains(
            review.Issues,
            issue =>
                issue.Kind == "Type conflict"
                && issue.BlocksExport
                && issue.Message.Contains("file-versus-directory", StringComparison.Ordinal)
        );
    }

    private MainViewModel LoadPanels(string leftDatabase, string rightDatabase)
    {
        var vm = new MainViewModel();
        vm.Left.LoadSnapshot(InventorySnapshot.Load(leftDatabase));
        vm.Right.LoadSnapshot(InventorySnapshot.Load(rightDatabase));
        return vm;
    }

    private string CreateDatabase(
        string name,
        string rootId,
        string rootPath,
        bool hash = true,
        string scanStatus = ScanStatus.Completed
    )
    {
        string path = Path.Combine(_dir, name + ".db");
        using (var db = Database.OpenWritable(path, pooling: false))
        {
            db.UpsertRoot(
                new StorageRootRow(rootId, name, rootPath, true, "fs", "sensitive", Modified)
            );
            Assert.Equal(0, new Scanner(db).ScanRoot(rootId).errors);
            if (hash)
            {
                new Scanner(db).HashNeeded(rootId, true, 1);
            }

            if (scanStatus != ScanStatus.Completed)
            {
                long scanId = db.BeginScan(rootId);
                db.FinishScan(scanId, scanStatus);
            }
        }
        return path;
    }

    private string NewRoot(string name)
    {
        string root = Path.Combine(_dir, name);
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Write(string root, string relative, string content)
    {
        string path = Paths.CombineRoot(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static SortedDictionary<string, string> DatabaseBytes(string path)
    {
        var bytes = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // SQLite may create an empty WAL while opening a WAL-mode inventory read-only.
        // Its contents are runtime state; compare the database and any durable WAL/journal bytes.
        foreach (string suffix in new[] { "", "-wal", "-journal" })
        {
            string companion = path + suffix;
            if (
                File.Exists(companion) && (suffix.Length == 0 || new FileInfo(companion).Length > 0)
            )
            {
                bytes[companion] = Convert.ToBase64String(File.ReadAllBytes(companion));
            }
        }
        return bytes;
    }

    private static void AssertDatabaseUnchanged(
        SortedDictionary<string, string> before,
        string path
    ) => Assert.Equal(before.ToArray(), DatabaseBytes(path).ToArray());

    private static HashSet<string> TemporaryPlanDirectories() =>
        Directory
            .EnumerateDirectories(
                Path.GetTempPath(),
                "backup-normalizer-ui-plan-*",
                SearchOption.TopDirectoryOnly
            )
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
