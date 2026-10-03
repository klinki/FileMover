using Avalonia.Controls;
using Avalonia.Interactivity;
using BackupNormalizer.Ui.Models;

namespace BackupNormalizer.Ui.Views;

public partial class MainWindow
{
    private async void OnDatabasePlan(object? sender, RoutedEventArgs args)
    {
        if (Vm == null || sender is not Button button)
        {
            return;
        }

        var direction =
            button.Tag as string == "RightToLeft"
                ? DatabasePlanDirection.RightToLeft
                : DatabasePlanDirection.LeftToRight;
        var review = await Vm.PreparePlanAsync(direction);
        if (review != null)
        {
            var window = new DatabasePlanReviewWindow(review)
            {
                ExecutePlanAsync = ShowExecutionWindowAsync,
            };
            await window.ShowDialog(this);
        }
    }
}
