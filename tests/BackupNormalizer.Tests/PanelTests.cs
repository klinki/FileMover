using BackupNormalizer.Ui.ViewModels;

namespace BackupNormalizer.Tests;

public sealed class PanelViewTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bn-panel-" + Guid.NewGuid().ToString("N"));
    public PanelViewTests() { Directory.CreateDirectory(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static void W(string root, string rel, string content)
    {
        var abs = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, content);
    }

    [Fact]
    public void DotDot_Is_First_And_Navigates_Up()
    {
        var sub = Path.Combine(_dir, "sub");
        Directory.CreateDirectory(sub);
        W(sub, "b.txt", "b");
        var panel = new FilePanelViewModel { CurrentPath = sub };
        panel.Refresh();
        Assert.True(panel.Entries.Count >= 2);
        var first = panel.Entries[0];
        Assert.True(first.IsParentEntry);
        Assert.Equal("..", first.Name);
        Assert.Equal("", first.Extension);
        Assert.True(panel.NavigateTo(first));
        Assert.Equal(_dir, panel.CurrentPath);
    }

    [Fact]
    public void DotDot_Hidden_At_Filesystem_Root()
    {
        string root = Path.GetPathRoot(Path.GetFullPath(_dir))!;
        var panel = new FilePanelViewModel { CurrentPath = root };
        panel.Refresh();
        Assert.DoesNotContain(panel.Entries, e => e.IsParentEntry);
    }

    [Fact]
    public void Filesystem_Root_Lists_Entries_Without_Error()
    {
        string root = Path.GetPathRoot(Path.GetFullPath(_dir))!;
        var panel = new FilePanelViewModel { CurrentPath = root };
        panel.Refresh();
        Assert.DoesNotContain("error", panel.Status, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(panel.Entries);
        Assert.DoesNotContain(panel.Entries, e => e.IsParentEntry);
    }

    [Fact]
    public void GoUp_At_Filesystem_Root_Is_NoOp()
    {
        string root = Path.GetPathRoot(Path.GetFullPath(_dir))!;
        var panel = new FilePanelViewModel { CurrentPath = root };
        panel.Refresh();
        var ex = Record.Exception(() => panel.GoUp());
        Assert.Null(ex);
        Assert.Equal(root, panel.CurrentPath);
    }

    [Fact]
    public void Sorting_Keeps_DotDot_First_And_Dirs_Before_Files()
    {
        var sub = Path.Combine(_dir, "sort");
        Directory.CreateDirectory(sub);
        Directory.CreateDirectory(Path.Combine(sub, "zdir"));
        Directory.CreateDirectory(Path.Combine(sub, "adir"));
        W(sub, "m.txt", "m");
        W(sub, "a.txt", "a");
        var panel = new FilePanelViewModel { CurrentPath = sub };
        panel.Refresh(); // default Name asc
        Assert.True(panel.Entries[0].IsParentEntry);
        var names = panel.Entries.Skip(1).Select(e => e.Name).ToList();
        Assert.Equal(new[] { "adir", "zdir", "a.txt", "m.txt" }, names);

        panel.ApplySort("Name"); // toggle desc
        Assert.True(panel.Entries[0].IsParentEntry);
        names = panel.Entries.Skip(1).Select(e => e.Name).ToList();
        Assert.Equal(new[] { "zdir", "adir", "m.txt", "a.txt" }, names);
    }

    [Fact]
    public void Name_And_Extension_Split()
    {
        var sub = Path.Combine(_dir, "ext");
        Directory.CreateDirectory(sub);
        W(sub, "photo.jpg", "x");
        Directory.CreateDirectory(Path.Combine(sub, "docs"));
        var panel = new FilePanelViewModel { CurrentPath = sub };
        panel.Refresh();
        var photo = panel.Entries.Single(e => e.Name == "photo.jpg");
        Assert.Equal("photo", photo.BaseName);
        Assert.Equal("jpg", photo.Extension);
        var docs = panel.Entries.Single(e => e.Name == "docs");
        Assert.Equal("docs", docs.BaseName);
        Assert.Equal("", docs.Extension);
    }

    [Fact]
    public void Sort_By_Ext_Groups_Types()
    {
        var sub = Path.Combine(_dir, "extsort");
        Directory.CreateDirectory(sub);
        W(sub, "b.txt", "b");
        W(sub, "a.jpg", "a");
        var panel = new FilePanelViewModel { CurrentPath = sub };
        panel.Refresh();
        panel.ApplySort("Ext");
        var exts = panel.Entries.Where(e => !e.IsParentEntry).Select(e => e.Extension).ToList();
        Assert.Equal(new[] { "jpg", "txt" }, exts);
    }
}

public sealed class PanelSelectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bn-panelsel-" + Guid.NewGuid().ToString("N"));
    public PanelSelectionTests() { Directory.CreateDirectory(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static void W(string root, string rel, string content)
    {
        var abs = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, content);
    }

    private FilePanelViewModel CreatePanel(params string[] files)
    {
        var sub = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sub);
        foreach (var f in files) W(sub, f, "data:" + f);
        var panel = new FilePanelViewModel { CurrentPath = sub };
        panel.Refresh();
        return panel;
    }

    [Fact]
    public void RightClick_Marks_And_Moves_Cursor()
    {
        var panel = CreatePanel("a.txt", "b.txt", "c.txt");
        var b = panel.Entries.Single(e => e.Name == "b.txt");
        var a = panel.Entries.Single(e => e.Name == "a.txt");

        panel.RightClick(b);

        Assert.Equal(b, panel.SelectedEntry);
        Assert.True(b.IsMarked);
        Assert.Equal(b, panel.MarkAnchor);
        Assert.False(a.IsMarked);

        // Second right-click toggles back off.
        panel.RightClick(b);
        Assert.False(b.IsMarked);
        Assert.Equal(b, panel.SelectedEntry);
    }

    [Fact]
    public void DotDot_Can_Never_Be_Marked()
    {
        var panel = CreatePanel("a.txt", "b.txt");
        var dotdot = panel.Entries[0];
        Assert.True(dotdot.IsParentEntry);

        panel.ToggleMark(dotdot);
        Assert.False(dotdot.IsMarked);

        panel.RightClick(dotdot);
        Assert.False(dotdot.IsMarked);
        Assert.Equal(dotdot, panel.SelectedEntry);

        // Shift-click span including index 0 must still skip "..".
        var last = panel.Entries[^1];
        panel.ShiftClick(last);
        Assert.False(dotdot.IsMarked);
        Assert.Equal(last, panel.SelectedEntry);
        Assert.All(panel.Entries.Where(e => !e.IsParentEntry), e => Assert.True(e.IsMarked));
    }

    [Fact]
    public void ShiftClick_Marks_Range_Inclusive()
    {
        var panel = CreatePanel("a.txt", "b.txt", "c.txt", "d.txt");
        var a = panel.Entries.Single(e => e.Name == "a.txt");
        var b = panel.Entries.Single(e => e.Name == "b.txt");
        var c = panel.Entries.Single(e => e.Name == "c.txt");
        var d = panel.Entries.Single(e => e.Name == "d.txt");

        // Seed a mark outside the upcoming range to prove ShiftClick never unmarks.
        panel.CtrlClick(a);
        panel.RightClick(b);
        panel.ShiftClick(d);

        Assert.True(a.IsMarked);
        Assert.True(b.IsMarked);
        Assert.True(c.IsMarked);
        Assert.True(d.IsMarked);
        Assert.Equal(d, panel.SelectedEntry);

        // Reverse direction is also inclusive.
        panel.ClearMarks();
        panel.RightClick(d);
        panel.ShiftClick(b);

        Assert.False(a.IsMarked);
        Assert.True(b.IsMarked);
        Assert.True(c.IsMarked);
        Assert.True(d.IsMarked);
        Assert.Equal(b, panel.SelectedEntry);
    }

    [Fact]
    public void CtrlClick_Toggles_Single_File()
    {
        var panel = CreatePanel("a.txt", "b.txt", "c.txt");
        var b = panel.Entries.Single(e => e.Name == "b.txt");
        var c = panel.Entries.Single(e => e.Name == "c.txt");

        panel.CtrlClick(b);
        Assert.True(b.IsMarked);
        Assert.Equal(b, panel.SelectedEntry);
        Assert.Equal(b, panel.MarkAnchor);
        Assert.False(c.IsMarked);

        panel.CtrlClick(b);
        Assert.False(b.IsMarked);

        panel.CtrlClick(c);
        Assert.True(c.IsMarked);
        Assert.False(b.IsMarked);
    }

    [Fact]
    public void SpaceOnCursor_Toggles_Cursor()
    {
        var panel = CreatePanel("a.txt", "b.txt");
        var b = panel.Entries.Single(e => e.Name == "b.txt");

        panel.SelectedEntry = b;
        panel.SpaceOnCursor();
        Assert.True(b.IsMarked);
        Assert.Equal(b, panel.MarkAnchor);

        panel.SpaceOnCursor();
        Assert.False(b.IsMarked);
        Assert.Equal(b, panel.MarkAnchor);

        // Null cursor is a no-op.
        panel.SelectedEntry = null;
        panel.MarkAnchor = null;
        panel.SpaceOnCursor();
        Assert.Null(panel.MarkAnchor);
    }

    [Fact]
    public void ShiftArrow_Moves_Cursor_And_Toggles_Landed_Row()
    {
        var panel = CreatePanel("a.txt", "b.txt", "c.txt");
        var dotdot = panel.Entries[0];
        var a = panel.Entries.Single(e => e.Name == "a.txt");
        var b = panel.Entries.Single(e => e.Name == "b.txt");
        var c = panel.Entries.Single(e => e.Name == "c.txt");

        panel.SelectedEntry = a;
        Assert.Null(panel.MarkAnchor);

        panel.ShiftArrow(1);
        Assert.Equal(b, panel.SelectedEntry);
        Assert.True(b.IsMarked);
        Assert.False(a.IsMarked);
        Assert.Equal(a, panel.MarkAnchor);

        panel.ShiftArrow(1);
        Assert.Equal(c, panel.SelectedEntry);
        Assert.True(c.IsMarked);
        Assert.True(b.IsMarked);

        // Landing on ".." moves the cursor but never marks it.
        panel.ClearMarks();
        panel.SelectedEntry = a;
        panel.ShiftArrow(-1);
        Assert.Equal(dotdot, panel.SelectedEntry);
        Assert.False(dotdot.IsMarked);
        Assert.Equal(a, panel.MarkAnchor);

        // Clamped at the top: further up stays on ".." without marking.
        panel.ShiftArrow(-5);
        Assert.Equal(dotdot, panel.SelectedEntry);
        Assert.False(dotdot.IsMarked);
    }

    [Fact]
    public void StagingSet_Prefers_Marked_Over_Cursor_And_Excludes_DotDot()
    {
        var panel = CreatePanel("a.txt", "b.txt", "c.txt");
        var dotdot = panel.Entries[0];
        var a = panel.Entries.Single(e => e.Name == "a.txt");
        var b = panel.Entries.Single(e => e.Name == "b.txt");
        var c = panel.Entries.Single(e => e.Name == "c.txt");

        // Cursor only, no marks.
        panel.SelectedEntry = b;
        Assert.Equal(new[] { b }, panel.StagingSet().ToList());

        // Marked set wins over cursor, in grid order.
        panel.CtrlClick(a);
        panel.CtrlClick(c);
        panel.SelectedEntry = b;
        var set = panel.StagingSet().ToList();
        Assert.Equal(new[] { a, c }, set);
        Assert.DoesNotContain(dotdot, set);

        // Cursor on ".." with no marks -> empty, never stages "..".
        panel.ClearMarks();
        panel.SelectedEntry = dotdot;
        Assert.Empty(panel.StagingSet());

        // Null cursor with no marks -> empty.
        panel.SelectedEntry = null;
        Assert.Empty(panel.StagingSet());
    }

    [Fact]
    public void MarkAll_Marks_Everything_Except_DotDot()
    {
        var panel = CreatePanel("a.txt", "b.txt");
        panel.MarkAll();
        Assert.True(panel.Entries.Where(e => !e.IsParentEntry).All(e => e.IsMarked));
        Assert.False(panel.Entries.Single(e => e.IsParentEntry).IsMarked);
        panel.ClearMarks();
        Assert.DoesNotContain(panel.Entries, e => e.IsMarked);
    }

    [Fact]
    public void Rubber_Latches_Select_Mode_On_Unmarked_Start()
    {
        var panel = CreatePanel("a.txt", "b.txt", "c.txt");
        var a = panel.Entries.Single(e => e.Name == "a.txt");
        var b = panel.Entries.Single(e => e.Name == "b.txt");
        var c = panel.Entries.Single(e => e.Name == "c.txt");

        Assert.True(panel.BeginRubber(a)); // select mode
        Assert.True(a.IsMarked);
        Assert.Equal(a, panel.SelectedEntry);

        // Drag over marked and unmarked rows: only selects, never deselects.
        b.IsMarked = true;
        panel.RubberTo(b, true);
        panel.RubberTo(c, true);
        Assert.True(a.IsMarked);
        Assert.True(b.IsMarked);
        Assert.True(c.IsMarked);
    }

    [Fact]
    public void Rubber_Latches_Deselect_Mode_On_Marked_Start()
    {
        var panel = CreatePanel("a.txt", "b.txt", "c.txt");
        var a = panel.Entries.Single(e => e.Name == "a.txt");
        var b = panel.Entries.Single(e => e.Name == "b.txt");

        b.IsMarked = true;
        Assert.False(panel.BeginRubber(b)); // deselect mode
        Assert.False(b.IsMarked);

        // Drag over unmarked rows: only deselects, never selects.
        panel.RubberTo(a, false);
        Assert.False(a.IsMarked);
        Assert.False(b.IsMarked);
    }
}

public sealed class MainWindowStaticInitTests
{
    [Fact]
    public void MainWindow_TypeInitializer_Does_Not_Throw_Headless()
    {
        // Regression: an invalid drag-drop DataFormat identifier used to kill the app
        // in MainWindow..cctor before any window was created. This forces the static
        // constructor without needing a display or Avalonia application instance.
        var ex = Record.Exception(() =>
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
                typeof(BackupNormalizer.Ui.Views.MainWindow).TypeHandle));
        Assert.Null(ex);
    }
}

public sealed class MainViewModelPanelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bn-vmpanel-" + Guid.NewGuid().ToString("N"));
    public MainViewModelPanelTests() { Directory.CreateDirectory(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void SwapPanels_Swaps_Paths_And_Refreshes()
    {
        var a = Path.Combine(_dir, "a"); Directory.CreateDirectory(a);
        var b = Path.Combine(_dir, "b"); Directory.CreateDirectory(b);
        File.WriteAllText(Path.Combine(a, "f.txt"), "x");
        var vm = new MainViewModel { BasePath = _dir };
        vm.Left.CurrentPath = a; vm.Left.Refresh();
        vm.Right.CurrentPath = b; vm.Right.Refresh();
        vm.SwapPanels();
        Assert.Equal(b, vm.Left.CurrentPath);
        Assert.Equal(a, vm.Right.CurrentPath);
        Assert.Contains(vm.Right.Entries, e => e.Name == "f.txt");
    }

    [Fact]
    public void RefreshActive_Reloads_Active_Panel_Only()
    {
        var a = Path.Combine(_dir, "a"); Directory.CreateDirectory(a);
        var vm = new MainViewModel { BasePath = _dir };
        vm.Left.CurrentPath = a; vm.Left.Refresh();
        Assert.Empty(vm.Left.Entries.Where(e => !e.IsParentEntry));
        File.WriteAllText(Path.Combine(a, "new.txt"), "x");
        vm.RefreshActive(); // left is active by default
        Assert.Contains(vm.Left.Entries, e => e.Name == "new.txt");
    }
}

public sealed class VirtualDirTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bn-virtual-" + Guid.NewGuid().ToString("N"));
    public VirtualDirTests() { Directory.CreateDirectory(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Virtual_Child_Appears_Sorted_With_Dirs_And_Navigates()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "real"));
        var panel = new FilePanelViewModel { CurrentPath = _dir };
        panel.VirtualDirs.Add(Path.Combine(_dir, "zz-staged"));
        panel.Refresh();
        var virt = panel.Entries.Single(e => e.Name == "zz-staged");
        Assert.True(virt.IsDirectory);
        Assert.True(virt.IsVirtual);
        Assert.Contains("staged", panel.Status);
        // Navigable: enter shows empty virtual location with ".." back up.
        Assert.True(panel.NavigateTo(virt));
        Assert.Equal(Path.Combine(_dir, "zz-staged"), panel.CurrentPath);
        Assert.Contains(panel.Entries, e => e.IsParentEntry);
        Assert.True(panel.NavigateTo(panel.Entries[0]));
        Assert.Equal(_dir, panel.CurrentPath);
    }

    [Fact]
    public void Real_Directory_Wins_Over_Virtual_Same_Name()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "same"));
        var panel = new FilePanelViewModel { CurrentPath = _dir };
        panel.VirtualDirs.Add(Path.Combine(_dir, "same"));
        panel.Refresh();
        var matches = panel.Entries.Where(e => e.Name == "same").ToList();
        Assert.Single(matches);
        Assert.False(matches[0].IsVirtual);
    }

    [Fact]
    public void StageMkdirFromDialog_Publishes_Virtual_In_Both_Panels()
    {
        var sub = Path.Combine(_dir, "sub");
        Directory.CreateDirectory(sub);
        var vm = new MainViewModel { BasePath = _dir };
        vm.ApplyBase();
        vm.Left.CurrentPath = sub; vm.Left.Refresh();
        vm.Right.CurrentPath = _dir; vm.Right.Refresh();
        vm.StageMkdirFromDialog("newdir");
        Assert.Contains(vm.Staged, o => o.Type == "MKDIR");
        Assert.Contains(vm.Left.Entries, e => e.Name == "newdir" && e.IsVirtual);
        // Other panel navigates in and sees it too once refreshed there.
        vm.Right.CurrentPath = sub; vm.Right.Refresh();
        Assert.Contains(vm.Right.Entries, e => e.Name == "newdir" && e.IsVirtual);
        // Invalid + duplicate names refused without staging.
        int before = vm.Staged.Count;
        vm.StageMkdirFromDialog("");
        vm.StageMkdirFromDialog("..");
        vm.StageMkdirFromDialog("newdir");
        Assert.Equal(before, vm.Staged.Count);
    }
}

public sealed class StagedOpItemTests
{
    [Fact]
    public void HashFull_Preserves_Digest_For_Resizing()
    {
        string full = new('a', 64);
        var item = new StagedOpItem("COPY", "s", "d", 10, full);
        Assert.Equal(full, item.HashFull);
    }
}

public sealed class StagedRemovalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bn-stagedrm-" + Guid.NewGuid().ToString("N"));
    public StagedRemovalTests() { Directory.CreateDirectory(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void RemoveStaged_Removes_Item_And_Updates_Summary()
    {
        var vm = new MainViewModel { BasePath = _dir };
        vm.ApplyBase();
        vm.StageMkdirFromDialog("toremove");
        var item = Assert.Single(vm.Staged);
        vm.RemoveStaged(item);
        Assert.Empty(vm.Staged);
        Assert.Equal("No staged operations.", vm.PlanSummary);
        vm.RemoveStaged(null); // no-op, never throws
    }
}

public sealed class DriveBarTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bn-drivebar-" + Guid.NewGuid().ToString("N"));
    public DriveBarTests() { Directory.CreateDirectory(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void RefreshDrives_Lists_Ready_Drives_With_Space()
    {
        var panel = new FilePanelViewModel { CurrentPath = _dir };
        panel.RefreshDrives();
        Assert.NotEmpty(panel.Drives);
        Assert.All(panel.Drives, d =>
        {
            Assert.False(string.IsNullOrEmpty(d.Root));
            Assert.True(d.TotalBytes > 0);
            Assert.True(d.FreeBytes >= 0 && d.FreeBytes <= d.TotalBytes);
            Assert.Contains("free", d.Display);
        });
    }

    [Fact]
    public void GoToDrive_Navigates_And_Updates_Status()
    {
        string root = Path.GetPathRoot(Path.GetFullPath(_dir))!;
        var panel = new FilePanelViewModel { CurrentPath = _dir };
        panel.RefreshDrives();
        panel.GoToDrive(new DriveView(root, "", 1, 2));
        Assert.Equal(root, panel.CurrentPath);
        Assert.Contains("free of", panel.DriveStatus);
        panel.GoToDrive(null); // no-op, never throws
    }

    [Fact]
    public void FormatBytes_Uses_Largest_Fitting_Unit()
    {
        Assert.Equal("0 B", DriveView.FormatBytes(0));
        Assert.Equal("512 B", DriveView.FormatBytes(512));
        Assert.Equal("2.0 KB", DriveView.FormatBytes(2048));
        Assert.Equal("3.5 MB", DriveView.FormatBytes((long)(3.5 * 1024 * 1024)));
        Assert.Equal("120.4 GB", DriveView.FormatBytes((long)(120.4 * 1024 * 1024 * 1024)));
    }
}

public sealed class DriveComboTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bn-drivecombo-" + Guid.NewGuid().ToString("N"));
    public DriveComboTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        File.WriteAllText(Path.Combine(_dir, "sub", "f.txt"), "x");
    }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Selecting_Drive_Navigates_To_Its_Root()
    {
        var sub = Path.Combine(_dir, "sub");
        Directory.CreateDirectory(sub);
        var panel = new FilePanelViewModel { CurrentPath = _dir };
        panel.RefreshDrives();
        panel.SelectedDrive = new DriveView(sub, "", 1, 2);
        Assert.Equal(sub, panel.CurrentPath);
    }

    [Fact]
    public void Programmatic_Sync_Never_Jumps_To_Root()
    {
        var panel = new FilePanelViewModel { CurrentPath = Path.Combine(_dir, "sub") };
        panel.RefreshDrives();
        panel.Refresh();
        Assert.Equal(Path.Combine(_dir, "sub"), panel.CurrentPath);
        Assert.NotNull(panel.SelectedDrive);
    }

    [Fact]
    public void Platform_Selects_Buttons_Or_Combo()
    {
        var vm = new MainViewModel();
        Assert.Equal(OperatingSystem.IsWindows(), vm.ShowDriveButtons);
        Assert.Equal(!OperatingSystem.IsWindows(), vm.ShowDriveCombo);
    }
}

public sealed class ScanProgressTests : IDisposable
{
    private sealed class InlineProgress(Action<ScanProgress> report) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => report(value);
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bn-scanprog-" + Guid.NewGuid().ToString("N"));
    public ScanProgressTests()
    {
        Directory.CreateDirectory(_dir);
        for (int i = 0; i < 10; i++)
            File.WriteAllText(Path.Combine(_dir, $"f{i}.txt"), "x");
    }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Progress_Reports_Increasing_Counts_And_Final_Total()
    {
        string dbp = Path.Combine(Path.GetTempPath(), "bn-scanprog-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var db = new Database(dbp);
            db.UpsertRoot(new StorageRootRow("r", "r", _dir, true, "fs", "unknown", Database.UtcNow()));
            Assert.Equal(0, db.CountFiles("r"));
            var seen = new List<ScanProgress>();
            var sc = new Scanner(db);
            var (scanned, errors) = sc.ScanRoot("r", new InlineProgress(seen.Add));
            Assert.Equal(10, scanned);
            Assert.Equal(0, errors);
            Assert.NotEmpty(seen);
            Assert.Equal(10, seen.Max(p => p.Scanned));
            for (int i = 1; i < seen.Count; i++)
                Assert.True(seen[i].Scanned >= seen[i - 1].Scanned);
            Assert.Equal(10, db.CountFiles("r"));
        }
        finally { try { File.Delete(dbp); } catch { } }
    }

    [Fact]
    public void No_Progress_Callback_Changes_Nothing()
    {
        string dbp = Path.Combine(Path.GetTempPath(), "bn-scanprog2-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var db = new Database(dbp);
            db.UpsertRoot(new StorageRootRow("r", "r", _dir, true, "fs", "unknown", Database.UtcNow()));
            var (scanned, errors) = new Scanner(db).ScanRoot("r");
            Assert.Equal((10, 0), (scanned, errors));
        }
        finally { try { File.Delete(dbp); } catch { } }
    }
}
