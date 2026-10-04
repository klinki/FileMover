using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using BackupNormalizer.Ui.ViewModels;

namespace BackupNormalizer.Ui.Views;

public partial class LocationChangesWindow : Window
{
    public LocationChangesWindow()
        : this(new LocationChangesViewModel()) { }

    public LocationChangesWindow(LocationChangesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Closed += (_, _) => viewModel.Dispose();
    }

    private async void OnSelectInventory(object? sender, RoutedEventArgs args)
    {
        if (
            DataContext is not LocationChangesViewModel vm
            || !vm.CanEdit
            || sender is not Button { Tag: string side }
        )
            return;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = $"Select inventory {side}",
                    AllowMultiple = false,
                    FileTypeFilter =
                    [
                        new FilePickerFileType("SQLite inventories")
                        {
                            Patterns = ["*.db", "*.sqlite", "*.sqlite3"],
                        },
                        FilePickerFileTypes.All,
                    ],
                }
            );
            if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
                await vm.LoadAsync(side, path);
        }
        catch (Exception ex)
        {
            vm.Status = "Cannot select inventory: " + ex.Message;
        }
    }

    private async void OnExport(object? sender, RoutedEventArgs args)
    {
        if (
            DataContext is not LocationChangesViewModel vm
            || !vm.CanExport
            || sender is not Button { Tag: string format }
        )
            return;
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    Title = "Export location changes",
                    SuggestedFileName = "location-changes." + format,
                    DefaultExtension = format,
                    FileTypeChoices =
                    [
                        new FilePickerFileType(format.ToUpperInvariant() + " report")
                        {
                            Patterns = ["*." + format],
                        },
                    ],
                }
            );
            if (file?.TryGetLocalPath() is { } path)
                await vm.ExportAsync(path, format);
        }
        catch (Exception ex)
        {
            vm.Status = "Cannot export report: " + ex.Message;
        }
    }
}
