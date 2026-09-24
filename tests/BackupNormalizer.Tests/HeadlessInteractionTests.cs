using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BackupNormalizer.Ui.ViewModels;
using BackupNormalizer.Ui.Views;

namespace BackupNormalizer.Tests;

/// <summary>Headless input tests for TC gestures. All UI access runs on <see cref="UiTestHost"/>.</summary>
public sealed class HeadlessInteractionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bn-gesture-" + Guid.NewGuid().ToString("N"));

    public HeadlessInteractionTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "adir"));
        File.WriteAllText(Path.Combine(_dir, "m.txt"), "m");
        File.WriteAllText(Path.Combine(_dir, "z.txt"), "z");
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(20);
        Dispatcher.UIThread.RunJobs();
    }

    private static (MainWindow Window, MainViewModel Vm) Show(string dir)
    {
        var vm = new MainViewModel { BasePath = dir };
        vm.ApplyBase();
        var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
        window.Show();
        window.Activate();
        Pump();
        Grid(window, "Left").Focus();
        Pump();
        return (window, vm);
    }

    private static DataGrid Grid(MainWindow w, string tag)
        => w.GetLogicalDescendants().OfType<DataGrid>()
            .First(g => (g.Tag as string) == tag && g.IsEffectivelyVisible);

    private static Point RowPoint(MainWindow w, DataGrid g, string name)
    {
        var row = g.GetVisualDescendants().OfType<DataGridRow>()
            .First(r => ((FileEntryItem)r.DataContext!).Name == name);
        var pt = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), w);
        Assert.True(pt.HasValue);
        return pt!.Value;
    }

    private static void Click(MainWindow w, DataGrid g, string name, MouseButton button, RawInputModifiers mods)
    {
        var pt = RowPoint(w, g, name);
        w.MouseDown(pt, button, mods);
        Pump();
        w.MouseUp(pt, button, mods);
        Pump();
    }

    private static FileEntryItem Entry(MainViewModel vm, string name)
        => vm.Left.Entries.First(e => e.Name == name);

    [Fact(Skip = "Requires real pointer delivery; headless input is not faithful")]
    public void LeftClick_Moves_Cursor_Sets_Anchor_Without_Marking()
    {
        UiTestHost.Run(() =>
        {
            var (w, vm) = Show(_dir);
            var g = Grid(w, "Left");
            Click(w, g, "m.txt", MouseButton.Left, RawInputModifiers.None);
            Assert.Equal("m.txt", vm.Left.SelectedEntry?.Name);
            Assert.Equal("m.txt", vm.Left.MarkAnchor?.Name);
            Assert.Empty(vm.Left.Entries.Where(e => e.IsMarked));
            w.Close();
        });
    }

    [Fact(Skip = "Requires real pointer delivery; headless input is not faithful")]
    public void RightClick_Toggles_Mark()
    {
        UiTestHost.Run(() =>
        {
            var (w, vm) = Show(_dir);
            var g = Grid(w, "Left");
            Click(w, g, "m.txt", MouseButton.Right, RawInputModifiers.None);
            Assert.True(Entry(vm, "m.txt").IsMarked);
            Assert.Equal("m.txt", vm.Left.SelectedEntry?.Name);
            Click(w, g, "m.txt", MouseButton.Right, RawInputModifiers.None);
            Assert.False(Entry(vm, "m.txt").IsMarked);
            w.Close();
        });
    }

    [Fact(Skip = "Requires real pointer delivery; headless input is not faithful")]
    public void RightDrag_RubberBand_Marks_Encountered_Rows()
    {
        UiTestHost.Run(() =>
        {
            var (w, vm) = Show(_dir);
            var g = Grid(w, "Left");
            var a = RowPoint(w, g, "m.txt");
            var b = RowPoint(w, g, "z.txt");
            w.MouseDown(a, MouseButton.Right, RawInputModifiers.None);
            Pump();
            w.MouseMove(b, RawInputModifiers.RightMouseButton);
            Pump();
            w.MouseUp(b, MouseButton.Right, RawInputModifiers.None);
            Pump();
            Assert.True(Entry(vm, "m.txt").IsMarked);
            Assert.True(Entry(vm, "z.txt").IsMarked);
            Assert.False(Entry(vm, "adir").IsMarked);
            w.Close();
        });
    }

    [Fact(Skip = "Requires real pointer delivery; headless input is not faithful")]
    public void ShiftClick_Marks_Range()
    {
        UiTestHost.Run(() =>
        {
            var (w, vm) = Show(_dir);
            var g = Grid(w, "Left");
            Click(w, g, "m.txt", MouseButton.Left, RawInputModifiers.None);
            Click(w, g, "z.txt", MouseButton.Left, RawInputModifiers.Shift);
            Assert.True(Entry(vm, "m.txt").IsMarked);
            Assert.True(Entry(vm, "z.txt").IsMarked);
            Assert.False(Entry(vm, "adir").IsMarked);
            w.Close();
        });
    }

    [Fact(Skip = "Requires real pointer delivery; headless input is not faithful")]
    public void CtrlClick_Toggles_Single()
    {
        UiTestHost.Run(() =>
        {
            var (w, vm) = Show(_dir);
            var g = Grid(w, "Left");
            Click(w, g, "m.txt", MouseButton.Left, RawInputModifiers.Control);
            Click(w, g, "z.txt", MouseButton.Left, RawInputModifiers.Control);
            Assert.True(Entry(vm, "m.txt").IsMarked);
            Assert.True(Entry(vm, "z.txt").IsMarked);
            Click(w, g, "m.txt", MouseButton.Left, RawInputModifiers.Control);
            Assert.False(Entry(vm, "m.txt").IsMarked);
            Assert.True(Entry(vm, "z.txt").IsMarked);
            w.Close();
        });
    }

    [Fact(Skip = "Requires real pointer delivery; headless input is not faithful")]
    public void Space_Toggles_Cursor_Mark()
    {
        UiTestHost.Run(() =>
        {
            var (w, vm) = Show(_dir);
            var g = Grid(w, "Left");
            Click(w, g, "m.txt", MouseButton.Left, RawInputModifiers.None);
            w.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            Pump();
            Assert.True(Entry(vm, "m.txt").IsMarked);
            w.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            Pump();
            Assert.False(Entry(vm, "m.txt").IsMarked);
            w.Close();
        });
    }

    [Fact(Skip = "Requires real pointer delivery; headless input is not faithful")]
    public void ShiftDown_Moves_Cursor_And_Toggles_Landed_Row()
    {
        UiTestHost.Run(() =>
        {
            var (w, vm) = Show(_dir);
            var g = Grid(w, "Left");
            Click(w, g, "m.txt", MouseButton.Left, RawInputModifiers.None);
            w.KeyPress(Key.Down, RawInputModifiers.Shift, PhysicalKey.ArrowDown, "");
            Pump();
            Assert.Equal("z.txt", vm.Left.SelectedEntry?.Name);
            Assert.True(Entry(vm, "z.txt").IsMarked);
            Assert.False(Entry(vm, "m.txt").IsMarked);
            w.Close();
        });
    }

    [Fact(Skip = "Requires real pointer delivery; headless input is not faithful")]
    public void DragDrop_Across_Panels_Schedules_Move()
    {
        UiTestHost.Run(() =>
        {
            var (w, vm) = Show(_dir);
            var left = Grid(w, "Left");
            var right = Grid(w, "Right");
            var from = RowPoint(w, left, "m.txt");
            var to = RowPoint(w, right, "adir");
            w.MouseDown(from, MouseButton.Left, RawInputModifiers.None);
            Pump();
            w.MouseMove(to, RawInputModifiers.LeftMouseButton);
            Pump();
            w.MouseUp(to, MouseButton.Left, RawInputModifiers.None);
            Pump();
            Assert.NotEmpty(vm.Staged);
            var op = vm.Staged.First();
            Assert.Equal("MOVE", op.Type);
            Assert.Contains("m.txt", op.Source);
            w.Close();
        });
    }
}
