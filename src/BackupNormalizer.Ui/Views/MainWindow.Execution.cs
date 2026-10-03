using System;
using System.Threading.Tasks;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using BackupNormalizer.Ui.Models;
using BackupNormalizer.Ui.ViewModels;

namespace BackupNormalizer.Ui.Views;

public partial class MainWindow
{
    private async void OnReviewStagedExecution(object? sender, RoutedEventArgs args)
    {
        if (Vm?.ReviewStagedExecution() is { } execution)
        {
            await new PlanExecutionWindow(execution).ShowDialog(this);
        }
    }

    private async Task ShowExecutionWindowAsync(PlanDoc document)
    {
        if (Vm == null || Vm.IsBusy)
        {
            return;
        }

        var execution = new PlanExecutionViewModel(document);
        Vm.AttachExecution(execution);
        await new PlanExecutionWindow(execution).ShowDialog(this);
    }

    private async void OnOpenExecution(object? sender, RoutedEventArgs args)
    {
        if (Vm == null || Vm.IsBusy)
        {
            return;
        }

        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = "Open saved execution database",
                    AllowMultiple = false,
                    FileTypeFilter =
                    [
                        new FilePickerFileType("Execution database")
                        {
                            Patterns = ["*.db", "*.sqlite"],
                        },
                        FilePickerFileTypes.All,
                    ],
                }
            );
            string? path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
            if (path == null)
            {
                return;
            }

            Vm.IsBusy = true;
            ExecutionSession session;
            try
            {
                session = await Task.Run(() => ExecutionSession.Load(path));
            }
            finally
            {
                Vm.IsBusy = false;
            }
            var execution = PlanExecutionViewModel.FromSession(session);
            Vm.AttachExecution(execution);
            await new PlanExecutionWindow(execution).ShowDialog(this);
        }
        catch (Exception ex)
        {
            Vm.StatusMessage = "Cannot open execution: " + ex.Message;
        }
    }
}
