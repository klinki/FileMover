using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BackupNormalizer.Ui.ViewModels;

namespace BackupNormalizer.Ui.Views;

/// <summary>
/// Total Commander style input: cursor (grid selection) is independent from marks.
/// Left = cursor, Right/Space = toggle mark, Shift/Ctrl+Left = range/toggle,
/// Shift+Up/Down = move cursor + toggle, right-drag = rubber-band toggle,
/// left-drag across panels = schedule MOVE (plan-only).
/// </summary>
public partial class MainWindow : Window
{
    // Note: CreateStringApplicationFormat takes the bare subtype ("application/" is added internally).
    private const string DropFormatName = "x-bn-staged";
    private static readonly DataFormat<string> DropFormat = DataFormat.CreateStringApplicationFormat(DropFormatName);

    private FilePanelViewModel? _rubberPanel;
    private string? _rubberSide;
    private bool _rubberSelect = true;

    // Edge auto-scroll while rubber-banding in large directories.
    private DispatcherTimer? _autoScrollTimer;
    private DataGrid? _autoScrollGrid;
    private Point _autoScrollPos;
    private int _autoScrollDir;
    private FilePanelViewModel? _dragPanel;
    private string? _dragSide;
    private Point _dragStart;
    private PointerPressedEventArgs? _dragPress;
    private bool _dragging;

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(InputElement.PointerPressedEvent, OnPreviewPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(InputElement.KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        // handledEventsToo: DataGrid marks presses handled for its own selection;
        // our TC cursor/mark layer must still observe them.
        AddHandler(InputElement.PointerPressedEvent, OnPressBubble, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(InputElement.PointerMovedEvent, OnPointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, OnGridDrop);
        AddHandler(DragDrop.DragOverEvent, OnGridDragOver);
        foreach (var grid in this.GetLogicalDescendants().OfType<DataGrid>())
        {
            grid.Sorting += OnGridSorting;
            grid.SetValue(DragDrop.AllowDropProperty, true);
        }
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private static DataGrid? GridOf(object? source)
        => (source as Control)?.GetLogicalAncestors().OfType<DataGrid>().FirstOrDefault()
           ?? source as DataGrid;

    private static FileEntryItem? RowOf(object? source)
        => (source as Control)?.GetLogicalAncestors().OfType<DataGridRow>().FirstOrDefault()?.DataContext as FileEntryItem;

    private FilePanelViewModel? PanelOf(DataGrid grid)
    {
        if (Vm == null) return null;
        return (grid.Tag as string) == "Right" ? Vm.Right : Vm.Left;
    }

    private void Activate(DataGrid grid)
    {
        if (Vm != null) Vm.IsLeftActive = (grid.Tag as string) != "Right";
    }

    // --- Tunnel: Shift/Ctrl+Left and Space/Shift+Arrows (run before grid native handling) ---

    private bool IsLeftPress(PointerPressedEventArgs e)
        => e.GetCurrentPoint(this).Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed;

    private static bool IsRightPress(PointerPressedEventArgs e, Visual relativeTo)
        => e.GetCurrentPoint(relativeTo).Properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed;

    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm == null || !IsLeftPress(e)) return;
        var grid = GridOf(e.Source);
        if (grid == null) return;
        var row = RowOf(e.Source);
        if (row == null) return;
        var panel = PanelOf(grid);
        if (panel == null) return;
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (shift)
        {
            Activate(grid);
            panel.ShiftClick(row);
            e.Handled = true;
        }
        else if (ctrl)
        {
            Activate(grid);
            panel.CtrlClick(row);
            e.Handled = true;
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm == null) return;
        var grid = GridOf(e.Source);
        FilePanelViewModel? panel = grid != null ? PanelOf(grid) : null;
        // Outside grids (toolbar, status, splitter): shortcuts fall back to the
        // active panel — but never hijack text input, and Space/Tab/arrows stay
        // grid-only so buttons, menus and focus traversal keep working.
        bool inTextInput = e.Source is TextBox;
        bool inMenu = e.Source is MenuItem or Menu;
        if (panel == null && !inTextInput)
            panel = Vm.Active;
        if (panel == null) return;
        bool inGrid = grid != null;
        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None && inGrid)
        {
            panel.SpaceOnCursor();
            e.Handled = true;
        }
        else if ((e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)) && e.Key == Key.A)
        {
            // Ctrl+A on Windows/Linux, Cmd+A on macOS.
            panel.MarkAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Tab && e.KeyModifiers == KeyModifiers.None && inGrid)
        {
            // TC panel switch; grid-scoped so Tab still moves through textboxes.
            Vm.IsLeftActive = !Vm.IsLeftActive;
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && !inTextInput && !inMenu)
        {
            panel.ClearMarks();
            e.Handled = true;
        }
        else if ((e.Key == Key.Up || e.Key == Key.Down) && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && inGrid)
        {
            panel.ShiftArrow(e.Key == Key.Down ? 1 : -1);
            e.Handled = true;
        }
    }

    // --- Bubble press: plain-left anchor + drag candidate, right = mark + rubber start ---

    private void OnPressBubble(object? sender, PointerPressedEventArgs e)
    {
        if (Vm == null) return;
        var grid = GridOf(e.Source);
        if (grid == null) return;
        Activate(grid);
        grid.Focus(); // cursor, focus and marks move together; keys route into the grid
        var panel = PanelOf(grid);
        if (panel == null) return;
        var row = RowOf(e.Source);
        if (row == null) return;
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (IsLeftPress(e) && !shift && !ctrl)
        {
            panel.MarkAnchor = row;
            _dragPanel = panel;
            _dragSide = (grid.Tag as string) ?? "Left";
            _dragStart = e.GetPosition(grid);
            _dragPress = e;
        }
        else if (IsRightPress(e, this))
        {
            if (row.IsParentEntry) return;
            // TC latch: mode comes from the pre-press state of the starting row.
            _rubberSelect = panel.BeginRubber(row);
            _rubberPanel = panel;
            _rubberSide = (grid.Tag as string) ?? "Left";
        }
    }

    // --- Move: rubber-band (right) and drag-start (left) ---

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (Vm == null) return;
        var point = e.GetCurrentPoint(this);
        if (_rubberPanel != null && _rubberSide != null && point.Properties.IsRightButtonPressed)
        {
            // Geometric hit-test: pointer capture retargets event sources during drag,
            // so the row under the pointer must be resolved by position, not e.Source.
            var grid = FindGrid(_rubberSide);
            if (grid == null) return;
            var pos = e.GetPosition(grid);
            var row = HitRow(grid, pos);
            if (row != null && !row.IsParentEntry)
            {
                _rubberPanel.RubberTo(row, _rubberSelect);
                _rubberPanel.SelectedEntry = row; // TC cursor follows the pointer
            }
            UpdateAutoScroll(grid, pos);
            return;
        }
        if (_dragPanel != null && !_dragging && point.Properties.IsLeftButtonPressed)
        {
            var grid = GridOf(e.Source) ?? FindGrid(_dragSide);
            if (grid == null) return;
            var pos = e.GetPosition(grid);
            if (Math.Abs(pos.X - _dragStart.X) < 8 && Math.Abs(pos.Y - _dragStart.Y) < 8) return;
            var set = _dragPanel.StagingSet();
            if (set.Count == 0 || _dragPress == null) { _dragPanel = null; _dragPress = null; return; }
            _dragging = true;
            var vm = Vm;
            vm.StatusMessage = $"Dragging {set.Count} item(s) — release over the other panel to stage MOVE.";
            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.Create(DropFormat, string.Join("\n", set.Select(s => s.FullPath))));
            var press = _dragPress;
            _dragPanel = null;
            _dragPress = null;
            try
            {
                _ = DragDrop.DoDragDropAsync(press, transfer, DragDropEffects.Move).ContinueWith(t =>
                {
                    _dragging = false;
                    if (t.Exception != null)
                        Dispatcher.UIThread.Post(() => vm.StatusMessage = "Drag failed: " +
                            (t.Exception.InnerException?.Message ?? t.Exception.Message));
                }, System.Threading.Tasks.TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                _dragging = false;
                vm.StatusMessage = "Drag failed: " + ex.Message;
            }
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        StopAutoScroll();
        _rubberPanel = null;
        _rubberSide = null;
        _dragPanel = null;
        _dragSide = null;
        _dragPress = null;
        _dragging = false;
    }

    private void UpdateAutoScroll(DataGrid grid, Point pos)
    {
        const double edge = 28;
        int dir = pos.Y < edge ? -1 : pos.Y > grid.Bounds.Height - edge ? 1 : 0;
        if (dir == 0)
        {
            StopAutoScroll();
            return;
        }
        _autoScrollGrid = grid;
        _autoScrollPos = pos;
        _autoScrollDir = dir;
        if (_autoScrollTimer == null)
        {
            _autoScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            _autoScrollTimer.Tick += (_, _) => AutoScrollTick();
        }
        if (!_autoScrollTimer.IsEnabled)
            _autoScrollTimer.Start();
    }

    private void StopAutoScroll()
    {
        _autoScrollTimer?.Stop();
        _autoScrollDir = 0;
        _autoScrollGrid = null;
    }

    private void AutoScrollTick()
    {
        var grid = _autoScrollGrid;
        if (grid == null || _autoScrollDir == 0 || _rubberPanel == null)
        {
            StopAutoScroll();
            return;
        }
        var scroller = grid.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (scroller == null)
        {
            StopAutoScroll();
            return;
        }
        double rowHeight = grid.GetVisualDescendants().OfType<DataGridRow>().FirstOrDefault()?.Bounds.Height ?? 33;
        if (rowHeight <= 0) rowHeight = 33;
        scroller.Offset = new Vector(scroller.Offset.X, scroller.Offset.Y + _autoScrollDir * rowHeight * 2);
        // Pointer is stationary; content moved under it — apply the latched mode.
        var row = HitRow(grid, _autoScrollPos);
        if (row == null || row.IsParentEntry) return;
        _rubberPanel.RubberTo(row, _rubberSelect);
        _rubberPanel.SelectedEntry = row; // TC cursor follows the pointer
    }

    private static FileEntryItem? HitRow(DataGrid grid, Point pos)
    {
        if (pos.X < 0 || pos.Y < 0 || pos.X > grid.Bounds.Width || pos.Y > grid.Bounds.Height) return null;
        var v = grid.InputHitTest(pos) as Visual;
        while (v != null && v is not DataGridRow) v = v.GetVisualParent();
        return (v as DataGridRow)?.DataContext as FileEntryItem;
    }

    private DataGrid? FindGrid(string? side)
        => this.GetLogicalDescendants().OfType<DataGrid>()
            .FirstOrDefault(g => ((g.Tag as string) ?? "Left") == (side ?? "Left") && g.IsEffectivelyVisible);

    private StagedOperationsWindow? _stagedWindow;

    private void OnShowStaged(object? sender, RoutedEventArgs e)
    {
        if (_stagedWindow == null)
        {
            _stagedWindow = new StagedOperationsWindow { DataContext = DataContext };
            _stagedWindow.Closed += (_, _) => _stagedWindow = null;
            _stagedWindow.Show();
        }
        else
        {
            _stagedWindow.Activate();
        }
    }

    // --- Drop: schedule MOVE into the target panel directory (plan-only) ---

    private void OnGridDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(DropFormat))
            e.DragEffects = DragDropEffects.Move;
    }

    private void OnGridDrop(object? sender, DragEventArgs e)
    {
        if (Vm == null) return;
        var grid = GridOf(e.Source);
        if (grid == null) return;
        if (!e.DataTransfer.Contains(DropFormat)) return;
        var text = e.DataTransfer.TryGetValue(DropFormat);
        if (string.IsNullOrWhiteSpace(text)) return;
        var panel = PanelOf(grid);
        if (panel == null) return;
        Activate(grid);
        Vm.StageMovePaths(text!.Split('\n'), panel.CurrentPath);
    }

    private static readonly Dictionary<string, string> ColumnTitles = new()
    {
        ["Name"] = "Name",
        ["Ext"] = "Ext",
        ["Size"] = "Size",
        ["Modified"] = "Modified",
    };

    private void OnGridSorting(object? sender, DataGridColumnEventArgs e)
    {
        if (Vm == null) return;
        var grid = sender as DataGrid;
        if (grid == null || e.Column == null) return;
        // Columns carry no SortMemberPath, so the grid performs no built-in sort:
        // we sort the source collection here, keeping ".." pinned first.
        string side = (grid.Tag as string) ?? (Vm.IsLeftActive ? "Left" : "Right");
        string column = e.Column.Tag as string ?? "Name";
        var panel = side == "Right" ? Vm.Right : Vm.Left;
        panel.ApplySort(column);
        foreach (var col in grid.Columns)
        {
            string key = col.Tag as string ?? "";
            string title = ColumnTitles.TryGetValue(key, out var t) ? t : key;
            col.Header = key == panel.SortColumn
                ? title + (panel.SortAscending ? " ▲" : " ▼")
                : title;
        }
    }

    // --- F7: TC-style mkdir dialog; confirmed name becomes a staged virtual dir ---

    private async void OnMkdir(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var dialog = new MkdirDialog();
        string? name = await dialog.ShowDialog<string?>(this);
        if (!string.IsNullOrWhiteSpace(name))
            Vm.StageMkdirFromDialog(name);
    }

    // --- Double-click navigates (Left=activate left, etc.) ---
    private void OnLeftDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm == null) return;
        Vm.IsLeftActive = true;
        Vm.EnterSelected();
    }

    private void OnRightDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm == null) return;
        Vm.IsLeftActive = false;
        Vm.EnterSelected();
    }
}
