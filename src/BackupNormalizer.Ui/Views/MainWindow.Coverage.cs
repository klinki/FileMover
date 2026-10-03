using Avalonia.Interactivity;
using BackupNormalizer.Ui.ViewModels;

namespace BackupNormalizer.Ui.Views;

public partial class MainWindow
{
    private async void OnBackupCoverage(object? sender, RoutedEventArgs args)
    {
        var coverage = new BackupCoverageViewModel();
        if (Vm?.Left.Snapshot is { } left)
            await coverage.AddDatabaseAsync(left.DatabasePath);
        if (Vm?.Right.Snapshot is { } right)
            await coverage.AddDatabaseAsync(right.DatabasePath);
        await new BackupCoverageWindow(coverage).ShowDialog(this);
    }
}
