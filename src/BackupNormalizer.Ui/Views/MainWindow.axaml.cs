using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using BackupNormalizer.Ui.ViewModels;

namespace BackupNormalizer.Ui.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnLeftPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm != null) Vm.IsLeftActive = true;
    }

    private void OnRightPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm != null) Vm.IsLeftActive = false;
    }

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

    private static readonly Dictionary<string, string> ColumnTitles = new()
    {
        ["Name"] = "Name",
        ["Ext"] = "Ext",
        ["Size"] = "Size",
        ["Modified"] = "Modified",
    };

    private void OnGridSorting(object? sender, DataGridColumnEventArgs e)
    {
        if (Vm == null || sender is not Control control || e.Column == null) return;
        // Columns carry no SortMemberPath, so the grid performs no built-in sort:
        // we sort the source collection here, keeping ".." pinned first.
        string side = control.Tag as string ?? (Vm.IsLeftActive ? "Left" : "Right");
        string column = e.Column.Tag as string ?? "Name";
        var panel = side == "Right" ? Vm.Right : Vm.Left;
        panel.ApplySort(column);
        if (sender is DataGrid grid)
        {
            foreach (var col in grid.Columns)
            {
                string key = col.Tag as string ?? "";
                string title = ColumnTitles.TryGetValue(key, out var t) ? t : key;
                col.Header = key == panel.SortColumn
                    ? title + (panel.SortAscending ? " ▲" : " ▼")
                    : title;
            }
        }
    }
}
