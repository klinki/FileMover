using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using BackupNormalizer.Ui.Models;
using BackupNormalizer.Ui.ViewModels;

namespace BackupNormalizer.Ui.Views;

public partial class MainWindow
{
    private bool _waitingForJobShutdown;
    private bool _jobShutdownAllowed;

    private async void OnCreateInventory(object? sender, RoutedEventArgs args)
    {
        if (Vm?.CanChangePanelSource != true)
        {
            return;
        }
        try
        {
            var request = await new InventoryScanWindow(
                new InventoryScanViewModel(Vm)
            ).ShowDialog<InventoryJobRequest?>(this);
            if (request == null)
            {
                return;
            }
            var task = Vm.RunNewInventoryJobAsync(request);
            await new InventoryJobsWindow(Vm).ShowDialog(this);
            await task;
        }
        catch (Exception ex)
        {
            Vm.StatusMessage = "Create inventory failed: " + ex.Message;
        }
    }

    private async void OnLoadConfiguration(object? sender, RoutedEventArgs args)
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
                    Title = "Load inventory JSON configuration",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("JSON configuration")
                        {
                            Patterns = new[] { "*.json" },
                        },
                    },
                }
            );
            if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            {
                Vm.LoadConfiguration(path);
            }
        }
        catch (Exception ex)
        {
            Vm.StatusMessage = "Load configuration failed: " + ex.Message;
        }
    }

    private async void OnShowInventoryJobs(object? sender, RoutedEventArgs args)
    {
        if (Vm != null)
        {
            await new InventoryJobsWindow(Vm).ShowDialog(this);
        }
    }

    private async void OnClosingWithInventoryJob(object? sender, WindowClosingEventArgs args)
    {
        if (_jobShutdownAllowed || (Vm?.Job.IsRunning != true && Vm?.Execution?.IsRunning != true))
        {
            return;
        }

        args.Cancel = true;
        if (_waitingForJobShutdown)
        {
            return;
        }

        _waitingForJobShutdown = true;
        await Vm.CancelAndWaitForJobAsync();
        if (Vm.Execution != null)
        {
            await Vm.Execution.CancelAndWaitAsync();
        }

        _jobShutdownAllowed = true;
        Close();
    }
}
