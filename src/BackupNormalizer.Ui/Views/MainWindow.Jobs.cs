using Avalonia.Controls;
using Avalonia.Interactivity;

namespace BackupNormalizer.Ui.Views;

public partial class MainWindow
{
    private bool _waitingForJobShutdown;
    private bool _jobShutdownAllowed;

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
