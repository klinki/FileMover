using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using BackupNormalizer.Ui.Models;

namespace BackupNormalizer.Ui.Views;

public partial class MainWindow
{
    // App opts in. Tests and auxiliary windows never read or write real user preferences.
    public GuiSessionStore? SessionStore { get; set; }
    private bool _restoringBrowsingSession;

    private async void RestoreBrowsingSession(object? sender, EventArgs args)
    {
        if (SessionStore?.Load() is not { } session || Vm == null) return;
        _restoringBrowsingSession = true;
        try
        {
            await Vm.RestoreSessionAsync(session);
            double fraction = double.IsFinite(session.LeftFraction) ? Math.Clamp(session.LeftFraction, 0.15, 0.85) : 0.5;
            SplitGrid.ColumnDefinitions[0].Width = new GridLength(fraction, GridUnitType.Star);
            SplitGrid.ColumnDefinitions[2].Width = new GridLength(1 - fraction, GridUnitType.Star);
            RestoreColumns(LeftList, session.LeftColumns);
            RestoreColumns(RightList, session.RightColumns);
        }
        finally { _restoringBrowsingSession = false; }
    }

    private static void RestoreColumns(DataGrid grid, List<ColumnSession>? saved)
    {
        if (saved == null) return;
        foreach (var column in grid.Columns)
        {
            string? key = column.Tag as string ?? column.Header as string;
            var setting = saved.FirstOrDefault(s => s.Key == key);
            if (setting == null || !double.IsFinite(setting.Width) || setting.Width <= 0) continue;
            column.Width = new DataGridLength(Math.Clamp(setting.Width, 0.1, 2000),
                setting.IsStar ? DataGridLengthUnitType.Star : DataGridLengthUnitType.Pixel);
        }
    }

    private static List<ColumnSession> CaptureColumns(DataGrid grid) => grid.Columns
        .Where(column => column.CanUserResize && (column.Tag is string || column.Header is string))
        .Select(column => new ColumnSession((column.Tag as string ?? column.Header as string)!,
            column.Width.Value, column.Width.IsStar)).ToList();

    private void SaveBrowsingSession()
    {
        if (_restoringBrowsingSession || SessionStore == null || Vm == null) return;
        try
        {
            double total = SplitGrid.ColumnDefinitions[0].ActualWidth + SplitGrid.ColumnDefinitions[2].ActualWidth;
            double fraction = total > 0 ? SplitGrid.ColumnDefinitions[0].ActualWidth / total : 0.5;
            SessionStore.Save(Vm.CaptureSession(fraction, CaptureColumns(LeftList), CaptureColumns(RightList)));
        }
        catch (Exception ex) { Log.Error("Cannot save GUI session: " + ex.Message); }
    }

    private async void OnExportInventory(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (Vm?.CanExportInventory != true) return;
        try
        {
            var destination = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export inventory snapshot to a new file", SuggestedFileName = "inventory-portable.db",
                DefaultExtension = "db", FileTypeChoices = new[] { new FilePickerFileType("SQLite inventory") { Patterns = new[] { "*.db" } } },
                ShowOverwritePrompt = false
            });
            if (destination == null) return;
            if (destination.TryGetLocalPath() is { } path) await Vm.ExportInventoryAsync(path);
            else Vm.StatusMessage = "Choose a local file for the inventory export.";
        }
        catch (Exception ex) { Vm.StatusMessage = "Inventory export failed: " + ex.Message; }
    }
}
