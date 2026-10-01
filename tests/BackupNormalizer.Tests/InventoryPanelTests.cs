using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BackupNormalizer.Ui.Models;
using BackupNormalizer.Ui.ViewModels;
using BackupNormalizer.Ui.Views;
using Microsoft.Data.Sqlite;

namespace BackupNormalizer.Tests;

public sealed class InventoryPanelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bn-inventory-ui-" + Guid.NewGuid().ToString("N"));
    private const string Modified = "2026-09-30T12:00:00.0000000Z";

    public InventoryPanelTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        foreach (var path in Directory.GetFiles(_dir, "*.db")) ReleasePools(path);
        Directory.Delete(_dir, true);
    }

    private static void ReleasePools(string path)
    {
        foreach (var mode in new[] { SqliteOpenMode.ReadWriteCreate, SqliteOpenMode.ReadOnly })
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = path, Mode = mode, ForeignKeys = true }.ToString());
            SqliteConnection.ClearPool(connection);
        }
    }

    private string CreateDatabase(string name, string rootId = "disk", string sensitivity = "sensitive", string scanStatus = ScanStatus.Completed)
    {
        string path = Path.Combine(_dir, name + ".db");
        using var db = new Database(path);
        db.UpsertRoot(new StorageRootRow(rootId, name, "/offline/" + name, false, "unknown", sensitivity, Modified));
        long scan = db.BeginScan(rootId);
        db.FinishScan(scan, scanStatus);
        return path;
    }

    private static long Add(string path, string relative, long size = 1, string? digest = "aaa",
        string rootId = "disk", string status = FileStatus.Ok, string hashState = HashState.Ok)
    {
        using var db = new Database(path);
        long id = db.UpsertFileEntry(new FileEntryRow(0, rootId, relative, relative.Split('/')[^1], size,
            Modified, null, null, 1, status, status == FileStatus.Ok ? null : "scan failed"));
        if (digest != null) db.UpsertHash(new FileHashRow(id, "sha256", digest, size, Modified, Modified, hashState));
        return id;
    }

    private static InventoryRoot Root(string path, string id = "disk") => InventorySnapshot.Load(path).Roots.Single(r => r.Root.Id == id);

    [Fact]
    public void Snapshot_Reconstructs_Offline_Tree_Without_Modifying_Database()
    {
        string path = CreateDatabase("offline");
        Add(path, "Photos/2026/image.jpg", 42);
        Add(path, "obsolete/removed.txt", status: FileStatus.Missing);
        ReleasePools(path);
        byte[] original = File.ReadAllBytes(path);
        var snapshot = InventorySnapshot.Load(path);
        var panel = new FilePanelViewModel();
        panel.LoadSnapshot(snapshot);
        Assert.True(panel.IsDatabase);
        Assert.False(panel.ShowDriveButtons);
        Assert.False(panel.ShowDriveCombo);
        Assert.DoesNotContain(panel.Entries, e => e.IsParentEntry || e.Name == "obsolete");
        panel.NavigateTo(Assert.Single(panel.Entries));
        Assert.Equal("Photos", panel.InventoryPath);
        panel.NavigateTo(panel.Entries.Single(e => e.Name == "2026"));
        Assert.Equal(42, panel.Entries.Single(e => e.Name == "image.jpg").Size);
        panel.GoUp(); panel.GoUp(); panel.GoUp();
        Assert.Equal("", panel.InventoryPath);
        Assert.Equal(original, File.ReadAllBytes(path));
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.Contains("Completed", panel.Status);
    }

    [Fact]
    public void Roots_In_One_Database_Are_Isolated()
    {
        string path = CreateDatabase("multi", rootId: "first");
        using (var db = new Database(path))
            db.UpsertRoot(new StorageRootRow("second", "Other drive", "Q:\\Offline", false, "unknown", "insensitive", Modified));
        Add(path, "first.txt", rootId: "first");
        Add(path, "second.txt", rootId: "second");
        var panel = new FilePanelViewModel();
        panel.LoadSnapshot(InventorySnapshot.Load(path), "first");
        Assert.Equal("first.txt", Assert.Single(panel.Entries).Name);
        panel.SelectedInventoryRoot = panel.InventoryRoots.Single(r => r.Root.Id == "second");
        Assert.Equal("second.txt", Assert.Single(panel.Entries).Name);
        Assert.Equal("Q:\\Offline", panel.CurrentPath);
    }

    [Fact]
    public void Comparison_Classifies_Content_And_Propagates_Nested_Differences()
    {
        string left = CreateDatabase("left"), right = CreateDatabase("right");
        foreach (var path in new[] { left, right })
        {
            Add(path, "equal/ok.txt");
            Add(path, "shared/nested.txt", digest: path == left ? "aaa" : "bbb");
            Add(path, "unhashed.txt", digest: null);
            Add(path, "stale.txt", hashState: HashState.Stale);
        }
        Add(left, "left-only/sub/file.txt");
        Add(right, "right-only/file.txt");
        Add(left, "sized.txt", 1, digest: null);
        Add(right, "sized.txt", 2, digest: null);
        var comparison = InventoryComparison.Compare(Root(left), "", Root(right), "");
        Assert.Equal(ComparisonState.Equal, comparison.Left["equal"]);
        Assert.Equal(ComparisonState.Different, comparison.Left["shared"]);
        Assert.Equal(ComparisonState.Different, comparison.Right["shared/nested.txt"]);
        Assert.Equal(ComparisonState.OnlyLeft, comparison.Left["left-only"]);
        Assert.Equal(ComparisonState.OnlyLeft, comparison.Left["left-only/sub/file.txt"]);
        Assert.Equal(ComparisonState.OnlyRight, comparison.Right["right-only"]);
        Assert.Equal(ComparisonState.Unverified, comparison.Left["unhashed.txt"]);
        Assert.Equal(ComparisonState.Unverified, comparison.Left["stale.txt"]);
        Assert.Equal(ComparisonState.Different, comparison.Left["sized.txt"]);
        Assert.DoesNotContain("Warning", comparison.Summary);
        Assert.Contains("1 only left", comparison.Summary);
    }

    [Theory]
    [InlineData("insensitive", "insensitive", ComparisonState.Equal)]
    [InlineData("sensitive", "insensitive", ComparisonState.OnlyLeft)]
    [InlineData("unknown", "unknown", ComparisonState.OnlyLeft)]
    public void Comparison_Uses_Recorded_Case_Sensitivity(string leftCase, string rightCase, ComparisonState expected)
    {
        string left = CreateDatabase("left", sensitivity: leftCase), right = CreateDatabase("right", sensitivity: rightCase);
        Add(left, "PHOTO.jpg"); Add(right, "photo.jpg");
        var comparison = InventoryComparison.Compare(Root(left), "", Root(right), "");
        Assert.Equal(expected, comparison.Left["PHOTO.jpg"]);
    }

    [Fact]
    public void Comparison_Supports_Different_Subfolder_Bases_And_Type_Conflicts()
    {
        string left = CreateDatabase("left"), right = CreateDatabase("right");
        Add(left, "Photos/file.txt"); Add(right, "Backup/file.txt");
        Add(left, "Photos/collision/sub.txt"); Add(right, "Backup/collision");
        var comparison = InventoryComparison.Compare(Root(left), "Photos", Root(right), "Backup");
        Assert.Equal(ComparisonState.Equal, comparison.Left["Photos/file.txt"]);
        Assert.Equal(ComparisonState.TypeConflict, comparison.Left["Photos/collision"]);
        Assert.Equal(ComparisonState.TypeConflict, comparison.Right["Backup/collision"]);
        Assert.DoesNotContain("", comparison.Left.Keys);
    }

    [Fact]
    public void Incomplete_Scans_And_Scan_Errors_Are_Visible()
    {
        string left = CreateDatabase("left", scanStatus: ScanStatus.Incomplete), right = CreateDatabase("right");
        Add(left, "broken.txt", status: FileStatus.ScanError); Add(right, "broken.txt");
        var comparison = InventoryComparison.Compare(Root(left), "", Root(right), "");
        Assert.Equal(ComparisonState.ScanError, comparison.Left["broken.txt"]);
        Assert.Contains("Warning", comparison.Summary);
    }

    [Fact]
    public async Task Two_Databases_With_Equal_Root_Ids_Compare_And_Link_Navigation()
    {
        string left = CreateDatabase("left"), right = CreateDatabase("right");
        Add(left, "shared/equal.txt"); Add(right, "shared/equal.txt");
        Add(left, "shared/extra.txt"); Add(left, "left-only/child.txt");
        var vm = new MainViewModel();
        await vm.LoadDatabaseAsync("Left", left);
        await vm.LoadDatabaseAsync("Right", right);
        await vm.CompareFolders();
        Assert.True(vm.IsComparisonEnabled);
        Assert.False(vm.CanStage);
        Assert.False(vm.StageCopyCommand.CanExecute(null));
        vm.DifferencesOnly = true;
        vm.Left.SelectedEntry = vm.Left.Entries.Single(e => e.Name == "shared");
        vm.EnterSelected();
        Assert.Equal("shared", vm.Right.InventoryPath);
        Assert.Equal("extra.txt", vm.Left.Entries.Single(e => !e.IsParentEntry).Name);
        Assert.DoesNotContain(vm.Right.Entries, e => !e.IsParentEntry);
        vm.GoUpActive();
        vm.Left.SelectedEntry = vm.Left.Entries.Single(e => e.Name == "left-only");
        vm.EnterSelected();
        Assert.Equal("left-only", vm.Right.InventoryPath);
        Assert.Contains("not indexed", vm.Right.Status);
        vm.IsLeftActive = false;
        vm.GoUpActive();
        Assert.Equal("", vm.Left.InventoryPath);
        Assert.Equal("", vm.Right.InventoryPath);
        vm.ClearComparison();
        Assert.All(vm.Left.Entries, e => Assert.Equal(ComparisonState.None, e.Comparison));
    }

    [Fact]
    public async Task Failed_Load_Preserves_Source_And_Does_Not_Create_A_Database()
    {
        string path = CreateDatabase("valid");
        Add(path, "file.txt");
        var vm = new MainViewModel();
        await vm.LoadDatabaseAsync("Left", path);
        var previous = vm.Left.Snapshot;
        string absent = Path.Combine(_dir, "absent.db");
        await vm.LoadDatabaseAsync("Left", absent);
        Assert.Same(previous, vm.Left.Snapshot);
        Assert.False(File.Exists(absent));
        Assert.Contains("failed", vm.StatusMessage);
        Assert.False(vm.IsBusy);
        vm.UseLivePanel("Left");
        Assert.True(vm.CanStage);
    }

    [Fact]
    public async Task Changing_Root_Clears_Comparison_And_Same_Database_Panels_Remain_Independent()
    {
        string path = CreateDatabase("multi", rootId: "first");
        using (var db = new Database(path))
        {
            db.UpsertRoot(new StorageRootRow("second", "Second", "/offline/second", false, "unknown", "sensitive", Modified));
            long scan = db.BeginScan("second");
            db.FinishScan(scan, ScanStatus.Completed);
        }
        Add(path, "shared.txt", rootId: "first");
        Add(path, "other.txt", rootId: "second");
        var vm = new MainViewModel();
        await vm.LoadDatabaseAsync("Left", path);
        await vm.LoadDatabaseAsync("Right", path);
        await vm.CompareFolders();
        Assert.Equal(ComparisonState.Equal, Assert.Single(vm.Left.Entries).Comparison);
        vm.Right.SelectedInventoryRoot = vm.Right.InventoryRoots.Single(r => r.Root.Id == "second");
        Assert.False(vm.IsComparisonEnabled);
        Assert.Equal("first", vm.Left.SelectedInventoryRoot!.Root.Id);
        Assert.Equal(ComparisonState.None, Assert.Single(vm.Left.Entries).Comparison);
        await vm.CompareFolders();
        Assert.Equal(ComparisonState.OnlyLeft, Assert.Single(vm.Left.Entries).Comparison);
        Assert.Equal(ComparisonState.OnlyRight, Assert.Single(vm.Right.Entries).Comparison);
    }

    [Fact]
    public async Task Database_Panels_Block_All_Live_Staging_Entry_Points()
    {
        string path = CreateDatabase("inventory");
        Add(path, "file.txt");
        var vm = new MainViewModel();
        await vm.LoadDatabaseAsync("Left", path);
        vm.Left.SelectedEntry = Assert.Single(vm.Left.Entries);
        vm.StageCopy(); vm.StageMove(); vm.StageTrash(); vm.StageMkdirFromDialog("new");
        vm.StageMovePaths(new[] { "file.txt" }, _dir);
        Assert.Empty(vm.Staged);
        Assert.Empty(vm.VirtualDirs);
        Assert.Contains("read-only", vm.StatusMessage);
    }

    [Fact]
    public async Task Staged_Operations_Block_Loading_And_Source_Switching()
    {
        string path = CreateDatabase("inventory");
        var vm = new MainViewModel { BasePath = _dir };
        vm.ApplyBase();
        vm.StageMkdirFromDialog("staged");
        await vm.LoadDatabaseAsync("Left", path);
        Assert.True(vm.Left.IsLive);
        Assert.False(vm.Left.CanChangeSource);
        Assert.Contains("Clear staged", vm.StatusMessage);
        vm.ClearStaged();
        await vm.LoadDatabaseAsync("Left", path);
        Assert.True(vm.Left.IsDatabase);
    }

    [Fact]
    public async Task Refresh_Reloads_Inventory_And_Swap_Preserves_Source_And_Path()
    {
        string left = CreateDatabase("left"), right = CreateDatabase("right");
        Add(left, "sub/a.txt"); Add(right, "b.txt");
        var vm = new MainViewModel();
        await vm.LoadDatabaseAsync("Left", left);
        await vm.LoadDatabaseAsync("Right", right);
        vm.Left.NavigateTo(Assert.Single(vm.Left.Entries));
        vm.SwapPanels();
        Assert.Equal(left, vm.Right.Snapshot!.DatabasePath);
        Assert.Equal("sub", vm.Right.InventoryPath);
        Add(left, "sub/new.txt");
        vm.IsLeftActive = false;
        await vm.RefreshActive();
        Assert.Contains(vm.Right.Entries, e => e.Name == "new.txt");
        vm.ClearStaged();
        Assert.Equal("sub", vm.Right.InventoryPath);
    }
}

[Collection("UI")]
public sealed class HeadlessInventoryPanelTests
{
    [Theory]
    [InlineData(860, 560)]
    [InlineData(1280, 800)]
    public void Database_Controls_Status_Column_And_Highlights_Render_In_Both_Panels(int width, int height)
    {
        var left = new InventoryRoot(new StorageRootRow("disk", "PC", "G:\\Photos", false, "unknown", "sensitive", ""), ScanStatus.Completed);
        var right = new InventoryRoot(new StorageRootRow("disk", "NAS", "/share/Photos", false, "unknown", "sensitive", ""), ScanStatus.Completed);
        foreach (var root in new[] { left, right })
            root.Nodes[""] = new InventoryNode("", "", true, root.Comparer);
        var folder = new InventoryNode("Only here", "Only here", true, left.Comparer);
        left.Nodes["Only here"] = folder;
        left.Nodes[""].Children[folder.Name] = folder;
        var comparison = InventoryComparison.Compare(left, "", right, "");
        UiTestHost.Run(() =>
        {
            var vm = new MainViewModel();
            vm.Left.LoadSnapshot(new InventorySnapshot("pc.db", new[] { left }));
            vm.Right.LoadSnapshot(new InventorySnapshot("nas.db", new[] { right }));
            vm.IsComparisonEnabled = true;
            vm.Left.ApplyComparison(comparison.Left, false);
            var window = new MainWindow { DataContext = vm, Width = width, Height = height };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(50);
            Dispatcher.UIThread.RunJobs();
            var grids = window.GetLogicalDescendants().OfType<DataGrid>().Where(g => g.IsEffectivelyVisible).ToList();
            Assert.Equal(2, grids.Count);
            Assert.All(grids, g => Assert.True(g.Columns.Single(c => Equals(c.Header, "Comparison")).IsVisible));
            Assert.All(grids, g => Assert.True(g.Columns.Single(c => Equals(c.Tag, "Name")).ActualWidth >= 100));
            Assert.All(grids, g => Assert.Equal(g.Bounds.Width >= 600, g.Columns.Single(c => Equals(c.Tag, "Modified")).IsVisible));
            var buttons = window.GetVisualDescendants().OfType<Button>().Where(b => Equals(b.Content, "Load database...")).ToList();
            Assert.Equal(2, buttons.Count);
            Assert.Contains(buttons, b => Equals(b.Tag, "Right"));
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Only left");
            Assert.Contains(window.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("onlyLeft") && b.Background != null);
            window.Close();
        });
    }
}
