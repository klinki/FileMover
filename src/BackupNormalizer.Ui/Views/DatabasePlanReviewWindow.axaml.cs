using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using BackupNormalizer.Ui.ViewModels;

namespace BackupNormalizer.Ui.Views;

public partial class DatabasePlanReviewWindow : Window
{
    public DatabasePlanReviewWindow(DatabasePlanReviewViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private async void OnExportJson(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DatabasePlanReviewViewModel viewModel)
        {
            return;
        }

        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    Title = "Export executor-compatible plan JSON",
                    SuggestedFileName = viewModel.SuggestedJsonName,
                    DefaultExtension = ".json",
                    ShowOverwritePrompt = true,
                    FileTypeChoices = new[]
                    {
                        new FilePickerFileType("JSON plan") { Patterns = new[] { "*.json" } },
                        FilePickerFileTypes.All,
                    },
                }
            );
            if (file == null)
            {
                return;
            }

            string? localPath = file.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(localPath))
            {
                viewModel.ReportExportError(
                    "The selected location does not expose a local file path."
                );
                return;
            }
            viewModel.ExportJson(localPath);
        }
        catch (System.Exception ex)
        {
            viewModel.ReportExportError("Plan JSON export failed: " + ex.Message);
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
