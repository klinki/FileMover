using Avalonia.Interactivity;
using BackupNormalizer.Ui.ViewModels;

namespace BackupNormalizer.Ui.Views;

public partial class MainWindow
{
    private async void OnLocationChanges(object? sender, RoutedEventArgs args)
    {
        var report = new LocationChangesViewModel();
        var dialog = new LocationChangesWindow(report).ShowDialog(this);
        if (Vm?.Left.Snapshot is { } left)
            await report.LoadAsync("A", left.DatabasePath, Vm.Left.SelectedInventoryRoot?.Root.Id);
        if (Vm?.Right.Snapshot is { } right)
            await report.LoadAsync(
                "B",
                right.DatabasePath,
                Vm.Right.SelectedInventoryRoot?.Root.Id
            );
        await dialog;
    }
}
