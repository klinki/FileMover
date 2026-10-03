using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using BackupNormalizer.Ui.Models;
using BackupNormalizer.Ui.ViewModels;
using BackupNormalizer.Ui.Views;

namespace BackupNormalizer.Tests;

[Collection("UI")]
public sealed class UiPortabilityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bn-ui-portable-" + Guid.NewGuid().ToString("N"));
    public UiPortabilityTests() => Directory.CreateDirectory(_directory);
    public void Dispose()
    {
        Directory.Delete(_directory, true);
    }

    private string Inventory()
    {
        string path = Path.Combine(_directory, "inventory.db");
        using var db = Database.OpenWritable(path, pooling: false);
        foreach (string root in new[] { "first", "second" })
        {
            db.UpsertRoot(new StorageRootRow(root, root, "/offline/" + root, false, "unknown", "sensitive", Database.UtcNow()));
            long scan = db.BeginScan(root);
            db.UpsertFileEntry(new FileEntryRow(0, root, "folder/file.txt", "file.txt", 1,
                Database.UtcNow(), null, null, scan, FileStatus.Ok, null));
            db.FinishScan(scan, ScanStatus.Completed);
        }
        return path;
    }

    [Fact]
    public void Session_Stores_Panels_And_Column_Widths_And_Handles_Bad_Json()
    {
        var store = new GuiSessionStore(Path.Combine(_directory, "session.json"));
        Assert.Null(store.Load());
        var session = new GuiSession(new("left.db", "r1", "Photos"), new(null, null, _directory), false, 0.65,
            new() { new("Name", 2, true), new("Size", 120, false) });
        store.Save(session);
        var loaded = store.Load()!;
        Assert.Equal(session.Left, loaded.Left);
        Assert.Equal(session.Right, loaded.Right);
        Assert.Equal(0.65, loaded.LeftFraction);
        Assert.Equal(session.LeftColumns, loaded.LeftColumns);
        File.WriteAllText(store.Path, "broken json");
        Assert.Null(store.Load());
        File.WriteAllText(store.Path, "{\"Left\":null,\"Right\":{\"DatabasePath\":null,\"RootId\":null,\"Folder\":null}}");
        Assert.Null(store.Load());
        File.WriteAllText(store.Path,
            "{\"Left\":{\"Folder\":null},\"Right\":{\"Folder\":null},\"LeftColumns\":[null,{\"Key\":null,\"Width\":2,\"IsStar\":true},{\"Key\":\"Name\",\"Width\":0,\"IsStar\":false}]}");
        var normalized = store.Load()!;
        Assert.Equal("", normalized.Left.Folder);
        Assert.Equal("", normalized.Right.Folder);
        Assert.Empty(normalized.LeftColumns!);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task Session_Restores_Independent_Offline_Roots_And_Falls_Back_From_Missing_Folders()
    {
        string path = Inventory();
        var vm = new MainViewModel();
        await vm.RestoreSessionAsync(new(new(path, "first", "folder/removed"), new(path, "second", "folder"), false));
        Assert.Equal("first", vm.Left.SelectedInventoryRoot!.Root.Id);
        Assert.Equal("second", vm.Right.SelectedInventoryRoot!.Root.Id);
        Assert.Equal("folder", vm.Left.InventoryPath);
        Assert.Equal("folder", vm.Right.InventoryPath);
        Assert.False(vm.IsLeftActive);
        Assert.False(vm.IsBusy);
        Assert.Contains("Recorded root", vm.Left.RecordedRootLabel);
        Assert.Contains("unavailable", vm.Left.LocalRootLabel);
        Assert.Contains("folder 'folder/removed' is unavailable", vm.StatusMessage);
        var captured = vm.CaptureSession();
        Assert.Equal(path, captured.Left.DatabasePath);
        Assert.Equal("first", captured.Left.RootId);
    }

    [Fact]
    public async Task Session_Missing_Root_Falls_Back_To_The_First_Root_And_Reports_It()
    {
        string path = Inventory();
        var vm = new MainViewModel();
        await vm.RestoreSessionAsync(new(new(path, "removed", "folder"), new(path, "second", "folder"), true));

        Assert.Equal("first", vm.Left.SelectedInventoryRoot!.Root.Id);
        Assert.Equal("", vm.Left.InventoryPath);
        Assert.Contains("inventory root 'removed' is unavailable", vm.StatusMessage);
        Assert.Contains("at its root folder", vm.StatusMessage);
    }

    [Fact]
    public void Local_Root_Availability_Requires_A_Host_Native_Absolute_Path()
    {
        Assert.True(FilePanelViewModel.IsHostNativeAbsolutePath(Path.GetFullPath(_directory)));
        if (OperatingSystem.IsWindows())
            Assert.False(FilePanelViewModel.IsHostNativeAbsolutePath("/offline/linux-root"));
        else
        {
            Assert.False(FilePanelViewModel.IsHostNativeAbsolutePath("C:\\offline\\windows-root"));
            Assert.False(FilePanelViewModel.IsHostNativeAbsolutePath("\\\\server\\share"));
        }
    }

    [Fact]
    public void Window_Restores_Split_And_Star_And_Pixel_Column_Widths()
    {
        var store = new GuiSessionStore(Path.Combine(_directory, "layout.json"));
        store.Save(new(new(null, null, _directory), new(null, null, _directory), false, 0.7,
            new() { new("Name", 2, true), new("Size", 140, false) },
            new() { new("Name", 1.5, true), new("Size", 125, false) }));

        UiTestHost.Run(() =>
        {
            var window = new MainWindow
            {
                DataContext = new MainViewModel(),
                SessionStore = store,
                Width = 1280,
                Height = 800,
            };
            try
            {
                window.Show();
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                var split = window.GetLogicalDescendants().OfType<Grid>().Single(grid => grid.Name == "SplitGrid");
                Assert.Equal(0.7, split.ColumnDefinitions[0].Width.Value, 3);
                Assert.Equal(0.3, split.ColumnDefinitions[2].Width.Value, 3);
                var grids = window.GetLogicalDescendants().OfType<DataGrid>().ToDictionary(grid => (string)grid.Tag!);
                Assert.Equal(DataGridLengthUnitType.Star, grids["Left"].Columns.Single(column => Equals(column.Tag, "Name")).Width.UnitType);
                Assert.Equal(2, grids["Left"].Columns.Single(column => Equals(column.Tag, "Name")).Width.Value);
                Assert.Equal(DataGridLengthUnitType.Pixel, grids["Left"].Columns.Single(column => Equals(column.Tag, "Size")).Width.UnitType);
                Assert.Equal(140, grids["Left"].Columns.Single(column => Equals(column.Tag, "Size")).Width.Value);
                Assert.Equal(1.5, grids["Right"].Columns.Single(column => Equals(column.Tag, "Name")).Width.Value);
                Assert.Equal(125, grids["Right"].Columns.Single(column => Equals(column.Tag, "Size")).Width.Value);
                grids["Left"].Columns.Single(column => Equals(column.Tag, "Size")).Width = new DataGridLength(170);
            }
            finally { window.Close(); }
        });
        var saved = store.Load()!;
        Assert.Equal(_directory, saved.Left.Folder);
        Assert.False(saved.IsLeftActive);
        Assert.Equal(170, saved.LeftColumns!.Single(column => column.Key == "Size").Width);
    }

    [Fact]
    public async Task Missing_Database_Does_Not_Prevent_The_Other_Panel_From_Restoring()
    {
        string missing = Path.Combine(_directory, "missing.db");
        var vm = new MainViewModel();
        await vm.RestoreSessionAsync(new(new(missing, "first", ""), new(Inventory(), "second", "folder"), true));
        Assert.True(vm.Left.IsLive);
        Assert.True(vm.Right.IsDatabase);
        Assert.Contains("could not be restored", vm.StatusMessage);
        Assert.False(File.Exists(missing));
    }

    [Fact]
    public async Task Export_Creates_Usable_Snapshot_And_Refuses_Existing_Destination()
    {
        string original = Inventory();
        var vm = new MainViewModel();
        await vm.LoadDatabaseAsync("Left", original);
        string output = Path.Combine(_directory, "export.db");
        await vm.ExportInventoryAsync(output);
        Assert.Contains("exported", vm.StatusMessage);
        using (var exported = Database.OpenReadOnly(output, pooling: false)) Assert.Equal(2, exported.ListRoots().Count);
        byte[] bytes = File.ReadAllBytes(output);
        await vm.ExportInventoryAsync(output);
        Assert.Contains("already exists", vm.StatusMessage);
        Assert.Equal(bytes, File.ReadAllBytes(output));
        Assert.False(vm.IsBusy);
    }
}
