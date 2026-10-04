using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BackupNormalizer.Ui.Models;
using BackupNormalizer.Ui.ViewModels;
using BackupNormalizer.Ui.Views;

namespace BackupNormalizer.Tests;

[Collection("UI")]
public sealed class InventoryDragStagingTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        AppContext.BaseDirectory,
        "bn-inventory-drag-" + Guid.NewGuid().ToString("N")
    );
    private const string RootId = "disk";
    private const string Modified = "2026-10-04T12:00:00.0000000Z";

    public InventoryDragStagingTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        Assert.StartsWith(AppContext.BaseDirectory, Path.GetFullPath(_directory));
        Directory.Delete(_directory, true);
    }

    private InventorySnapshot Inventory(
        string name,
        Dictionary<string, string?> files,
        string scanStatus = ScanStatus.Completed,
        string sensitivity = "sensitive",
        bool writable = true
    )
    {
        string path = Path.Combine(_directory, name + ".db");
        using (var db = Database.OpenWritable(path, pooling: false))
        {
            db.UpsertRoot(
                new StorageRootRow(
                    RootId,
                    name,
                    Path.Combine(_directory, name + "-root"),
                    writable,
                    name,
                    sensitivity,
                    Modified
                )
            );
            long scan = db.BeginScan(RootId);
            foreach (var (relative, contents) in files)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(contents ?? "unhashed");
                long id = db.UpsertFileEntry(
                    new FileEntryRow(
                        0,
                        RootId,
                        relative,
                        relative.Split('/')[^1],
                        bytes.Length,
                        Modified,
                        null,
                        null,
                        scan,
                        FileStatus.Ok,
                        null
                    )
                );
                if (contents != null)
                    db.UpsertHash(
                        new FileHashRow(
                            id,
                            "sha256",
                            Convert.ToHexStringLower(SHA256.HashData(bytes)),
                            bytes.Length,
                            Modified,
                            Modified,
                            HashState.Ok
                        )
                    );
            }
            db.FinishScan(scan, scanStatus);
        }
        return InventorySnapshot.Load(path);
    }

    private static MainViewModel Panels(InventorySnapshot left, InventorySnapshot right)
    {
        var vm = new MainViewModel();
        vm.Left.LoadSnapshot(left);
        vm.Right.LoadSnapshot(right);
        return vm;
    }

    [Fact]
    public async Task Offline_Folder_And_Marked_Files_Stage_Only_Their_Contents_And_Retain_Separate_Roots()
    {
        var source = Inventory(
            "source",
            new()
            {
                ["folder/a.txt"] = "alpha",
                ["folder/nested/b.txt"] = "beta",
                ["extra.txt"] = "extra",
                ["untouched.txt"] = "untouched",
            }
        );
        var target = Inventory("target", new() { ["archive/keep.txt"] = "keep" });
        byte[] sourceBefore = File.ReadAllBytes(source.DatabasePath),
            targetBefore = File.ReadAllBytes(target.DatabasePath);
        var vm = Panels(source, target);
        await vm.CompareFolders();
        vm.LinkedBrowsing = false;
        vm.Right.NavigateInventory("archive");
        vm.Left.Entries.Single(e => e.Name == "folder").IsMarked = true;
        vm.Left.Entries.Single(e => e.Name == "extra.txt").IsMarked = true;
        Assert.True(vm.CanDragStage);
        Assert.True(vm.StageCopyCommand.CanExecute(null));
        Assert.False(vm.StageMoveCommand.CanExecute(null));
        await vm.StageCopy();

        Assert.Equal(3, vm.Staged.Count(op => op.Type == OpType.Copy));
        Assert.Contains(
            vm.Staged,
            op => op.Type == OpType.Mkdir && op.Dest == "archive/folder/nested"
        );
        Assert.Contains(
            vm.Staged,
            op => op.Source == "folder/nested/b.txt" && op.Dest == "archive/folder/nested/b.txt"
        );
        Assert.DoesNotContain(vm.Staged, op => op.Source == "untouched.txt");
        Assert.False(vm.CanChangePanelSource);
        Assert.True(vm.CanReviewStaged);
        var document = Assert.IsType<PlanExecutionViewModel>(vm.ReviewStagedExecution()).Document;
        Assert.Equal(source.DatabasePath, document.SourceDatabasePath);
        Assert.Equal(source.Roots[0].Root.Path, document.SourcePath);
        Assert.Equal(target.Roots[0].Root.Path, document.TargetPath);
        Assert.Equal(RootId, document.SourceRoot);
        Assert.Equal(RootId, document.TargetRoot);
        Assert.All(
            document.Operations.Where(op => op.Type == OpType.Copy),
            op => Assert.Equal(SourceScope.Source, op.SourceKind)
        );
        Assert.Equal(14, document.EstimatedBytesCopied);
        int count = vm.Staged.Count;
        await vm.StageCopy();
        Assert.Equal(count, vm.Staged.Count);
        Assert.Contains("Already staged", vm.StatusMessage);
        Assert.Equal(sourceBefore, File.ReadAllBytes(source.DatabasePath));
        Assert.Equal(targetBefore, File.ReadAllBytes(target.DatabasePath));
        Assert.False(Directory.Exists(document.TargetPath));
        Assert.False(Directory.Exists(document.SourcePath));

        vm.JsonPath = Path.Combine(_directory, "copies.json");
        vm.SaveJson();
        var imported = PlanStaging.ImportJson(vm.JsonPath);
        Assert.Equal(document.Operations, imported.Operations);
        Assert.Equal(document.SourcePath, imported.SourcePath);
        Assert.Equal(document.TargetPath, imported.TargetPath);
        vm.DbPath = Path.Combine(_directory, "copies.db");
        vm.WriteToDb();
        using (var db = Database.OpenReadOnly(vm.DbPath, pooling: false))
        {
            var saved = new Planner(db).ExportPlan(vm.PlanId);
            Assert.Equal(
                document.Operations.Select(op => op.SourceKind ?? SourceScope.Target),
                saved.Operations.Select(op => op.SourceKind)
            );
            Assert.Equal(document.TargetPath, saved.TargetPath);
        }
    }

    [Fact]
    public async Task Direction_And_Sources_Are_Locked_Until_Staged_Operations_Are_Cleared()
    {
        var left = Inventory("left", new() { ["left.txt"] = "left" });
        var right = Inventory("right", new() { ["right.txt"] = "right" });
        var vm = Panels(left, right);
        await vm.StageInventoryCopyAsync(vm.Left, vm.Right, ["left.txt"], "");
        await vm.StageInventoryCopyAsync(vm.Right, vm.Left, ["right.txt"], "");
        Assert.Contains("direction", vm.StatusMessage);
        Assert.Single(vm.Staged);
        await vm.RefreshAll();
        Assert.Same(left, vm.Left.Snapshot);
        vm.SwapPanels();
        Assert.Same(left, vm.Left.Snapshot);
        vm.UseLivePanel("Left");
        Assert.True(vm.Left.IsDatabase);
        vm.RemoveStaged(Assert.Single(vm.Staged));
        Assert.True(vm.CanChangePanelSource);
        Assert.False(vm.CanReviewStaged);
        await vm.StageInventoryCopyAsync(vm.Right, vm.Left, ["right.txt"], "");
        var document = Assert.IsType<PlanExecutionViewModel>(vm.ReviewStagedExecution()).Document;
        Assert.Equal(right.DatabasePath, document.SourceDatabasePath);
        Assert.Equal(left.Roots[0].Root.Path, document.TargetPath);
        Assert.Equal("right.txt", Assert.Single(document.Operations).SourcePath);
    }

    [Theory]
    [InlineData("source.db")]
    [InlineData("target.db")]
    [InlineData("target.db-wal")]
    [InlineData("source.db-shm")]
    public async Task Export_Cannot_Overwrite_An_Input_Inventory_Or_Companion(string destination)
    {
        var source = Inventory("source", new() { ["a.txt"] = "a" });
        var target = Inventory("target", new());
        var vm = Panels(source, target);
        await vm.StageInventoryCopyAsync(vm.Left, vm.Right, ["a.txt"], "");
        byte[] sourceBefore = File.ReadAllBytes(source.DatabasePath),
            targetBefore = File.ReadAllBytes(target.DatabasePath);
        vm.JsonPath = vm.DbPath = Path.Combine(_directory, destination);
        vm.SaveJson();
        Assert.Contains("inventory databases", vm.StatusMessage);
        vm.WriteToDb();
        Assert.Contains("inventory databases", vm.StatusMessage);
        Assert.Equal(sourceBefore, File.ReadAllBytes(source.DatabasePath));
        Assert.Equal(targetBefore, File.ReadAllBytes(target.DatabasePath));
    }

    [Theory]
    [InlineData("missing hash")]
    [InlineData("incomplete scan")]
    [InlineData("case collision")]
    [InlineData("target file blocks folder")]
    [InlineData("target folder blocks file")]
    [InlineData("read-only target")]
    [InlineData("invalid source path")]
    [InlineData("invalid destination path")]
    public async Task Invalid_Drop_Does_Not_Publish_A_Partial_Plan(string failure)
    {
        var source = Inventory(
            "source",
            new()
            {
                ["folder/a.txt"] = "a",
                ["folder/b.txt"] = failure == "missing hash" ? null : "b",
                ["folder/A.txt"] = "capital",
            },
            failure == "incomplete scan" ? ScanStatus.Incomplete : ScanStatus.Completed
        );
        var target = Inventory(
            "target",
            failure switch
            {
                "target file blocks folder" => new() { ["folder"] = "file" },
                "target folder blocks file" => new() { ["folder/a.txt/child"] = "file" },
                _ => new(),
            },
            sensitivity: failure == "case collision" ? "insensitive" : "sensitive",
            writable: failure != "read-only target"
        );
        var vm = Panels(source, target);
        await vm.StageInventoryCopyAsync(
            vm.Left,
            vm.Right,
            [failure == "invalid source path" ? "../folder" : "folder"],
            failure == "invalid destination path" ? "../outside" : ""
        );
        Assert.Empty(vm.Staged);
        Assert.Contains("failed", vm.StatusMessage);
        Assert.False(vm.IsBusy);
        Assert.True(vm.CanChangePanelSource);
    }

    [Fact]
    public async Task Existing_Destination_Is_Verified_And_Links_Are_Skipped()
    {
        var source = Inventory(
            "source",
            new() { ["same.txt"] = "source", ["folder/a.txt"] = "child" }
        );
        var target = Inventory("target", new() { ["same.txt"] = "different" });
        AddLink(source.DatabasePath, "linked", EntryKind.DirectoryLink);
        AddLink(target.DatabasePath, "folder", EntryKind.DirectoryLink);
        var vm = Panels(
            InventorySnapshot.Load(source.DatabasePath),
            InventorySnapshot.Load(target.DatabasePath)
        );
        await vm.StageInventoryCopyAsync(vm.Left, vm.Right, ["same.txt", "folder", "linked"], "");
        Assert.Equal(OpType.Verify, vm.Staged.Single(op => op.Dest == "same.txt").Type);
        Assert.Equal(2, vm.Staged.Count(op => op.Type == OpType.SkipLink));
        Assert.DoesNotContain(vm.Staged, op => op.Type == OpType.Copy || op.Type == OpType.Mkdir);
    }

    private static void AddLink(string database, string path, string kind)
    {
        using var db = Database.OpenWritable(database, pooling: false);
        db.UpsertFileEntry(
            new FileEntryRow(
                0,
                RootId,
                path,
                path,
                0,
                Modified,
                null,
                null,
                1,
                FileStatus.Ok,
                null,
                kind,
                "outside",
                LinkNote: "Excluded link"
            )
        );
    }

    [Fact]
    public async Task Reviewed_Inventory_Copy_Executes_From_The_Source_Root_With_Equal_Root_Ids()
    {
        var source = Inventory("source", new() { ["folder/a.txt"] = "alpha" });
        var target = Inventory("target", new());
        var vm = Panels(source, target);
        await vm.StageInventoryCopyAsync(vm.Left, vm.Right, ["folder"], "archive");
        var review = Assert.IsType<PlanExecutionViewModel>(vm.ReviewStagedExecution());
        string sourceRoot = source.Roots[0].Root.Path,
            targetRoot = target.Roots[0].Root.Path;
        Directory.CreateDirectory(Path.Combine(sourceRoot, "folder"));
        Directory.CreateDirectory(targetRoot);
        File.WriteAllText(Path.Combine(sourceRoot, "folder", "a.txt"), "alpha");
        Assert.False(Directory.Exists(Path.Combine(targetRoot, "archive")));
        review.DatabasePath = Path.Combine(_directory, "execution.db");
        await review.RunAsync();
        Assert.Equal("Completed", review.LastResult?.Outcome);
        Assert.Equal(
            "alpha",
            File.ReadAllText(Path.Combine(targetRoot, "archive", "folder", "a.txt"))
        );
        Assert.Equal("alpha", File.ReadAllText(Path.Combine(sourceRoot, "folder", "a.txt")));
        using var db = Database.OpenReadOnly(target.DatabasePath, pooling: false);
        Assert.Empty(db.ListPlanIds());
    }

    [Fact]
    public void Routed_Inventory_Drop_Stages_Copies_And_Enables_Review()
    {
        var source = Inventory("source", new() { ["folder/a.txt"] = "alpha" });
        var target = Inventory("target", new() { ["archive/keep.txt"] = "keep" });
        UiTestHost.Run(() =>
        {
            var vm = Panels(source, target);
            vm.IsComparisonEnabled = true;
            var window = new MainWindow
            {
                DataContext = vm,
                Width = 1300,
                Height = 800,
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var right = window
                .GetLogicalDescendants()
                .OfType<DataGrid>()
                .First(g => (g.Tag as string) == "Right" && g.IsEffectivelyVisible);
            using var transfer = new DataTransfer();
            transfer.Add(
                DataTransferItem.Create(
                    DataFormat.CreateStringApplicationFormat("x-bn-inventory-copy"),
                    JsonSerializer.Serialize(
                        new
                        {
                            Side = "Left",
                            DatabasePath = source.DatabasePath,
                            RootId,
                            Paths = new[] { "folder" },
                        }
                    )
                )
            );
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext());
            try
            {
                var over = new DragEventArgs(
                    DragDrop.DragOverEvent,
                    transfer,
                    right,
                    new Point(2, 2),
                    KeyModifiers.None
                );
                right.RaiseEvent(over);
                Assert.Equal(DragDropEffects.Copy, over.DragEffects);
                var folderRow = right
                    .GetVisualDescendants()
                    .OfType<DataGridRow>()
                    .Single(r => r.DataContext is FileEntryItem { Name: "archive" });
                Point point = folderRow
                    .TranslatePoint(new Point(12, folderRow.Bounds.Height / 2), right)!
                    .Value;
                var drop = new DragEventArgs(
                    DragDrop.DropEvent,
                    transfer,
                    right,
                    point,
                    KeyModifiers.None
                );
                right.RaiseEvent(drop);
                Assert.True(drop.Handled);
                var timeout = Stopwatch.StartNew();
                while (vm.IsBusy && timeout.Elapsed < TimeSpan.FromSeconds(10))
                {
                    Dispatcher.UIThread.RunJobs();
                    Thread.Sleep(10);
                }
                Dispatcher.UIThread.RunJobs();
                Assert.False(vm.IsBusy);
                Assert.Contains(
                    vm.Staged,
                    op => op.Type == OpType.Copy && op.Dest == "archive/folder/a.txt"
                );
                Assert.True(vm.CanReviewStaged);
                var review = window
                    .GetVisualDescendants()
                    .OfType<Button>()
                    .Single(b => (b.Content as string) == "Review / execute...");
                Assert.True(review.IsEnabled);
                Assert.False(vm.IsLeftActive);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
                window.Close();
            }
        });
    }
}
