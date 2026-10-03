using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using BackupNormalizer.Ui.ViewModels;

namespace BackupNormalizer.Ui.Views;

public partial class BackupCoverageWindow : Window
{
    public BackupCoverageWindow()
        : this(new BackupCoverageViewModel()) { }

    public BackupCoverageWindow(BackupCoverageViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private async void OnAddInventories(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not BackupCoverageViewModel viewModel)
            return;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = "Add inventory databases",
                    AllowMultiple = true,
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
            foreach (var file in files)
            {
                string? path = file.TryGetLocalPath();
                if (path != null)
                    await viewModel.AddDatabaseAsync(path);
            }
        }
        catch (Exception ex)
        {
            viewModel.Status = "Cannot add inventories: " + ex.Message;
        }
    }
}
