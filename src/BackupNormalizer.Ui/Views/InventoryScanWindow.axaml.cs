using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using BackupNormalizer.Ui.Models;
using BackupNormalizer.Ui.ViewModels;

namespace BackupNormalizer.Ui.Views;

public partial class InventoryScanWindow : Window
{
    public InventoryScanWindow() => InitializeComponent();

    public InventoryScanWindow(InventoryScanViewModel viewModel)
        : this() => DataContext = viewModel;

    private InventoryScanViewModel Vm => (InventoryScanViewModel)DataContext!;

    private void OnStart(object? sender, RoutedEventArgs args)
    {
        try
        {
            Close(Vm.CreateRequest());
        }
        catch (Exception ex)
        {
            Vm.Error = ex.Message;
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs args) =>
        Close((InventoryJobRequest?)null);

    private async void OnBrowseFolder(object? sender, RoutedEventArgs args)
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions
                {
                    Title = "Choose the disk or folder to scan",
                    AllowMultiple = false,
                }
            );
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            {
                Vm.RootPath = path;
            }
        }
        catch (Exception ex)
        {
            Vm.Error = "Choose folder failed: " + ex.Message;
        }
    }

    private async void OnBrowseDatabase(object? sender, RoutedEventArgs args)
    {
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    Title = "Choose a new inventory database file",
                    SuggestedFileName = Vm.RootId + ".db",
                    DefaultExtension = "db",
                    FileTypeChoices = new[]
                    {
                        new FilePickerFileType("SQLite inventory") { Patterns = new[] { "*.db" } },
                    },
                    ShowOverwritePrompt = false,
                }
            );
            if (file?.TryGetLocalPath() is { } path)
            {
                Vm.DatabasePath = path;
            }
        }
        catch (Exception ex)
        {
            Vm.Error = "Choose database failed: " + ex.Message;
        }
    }
}
