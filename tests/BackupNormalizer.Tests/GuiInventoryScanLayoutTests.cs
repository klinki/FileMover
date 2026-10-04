using Avalonia.Controls;
using Avalonia.Threading;
using BackupNormalizer.Ui.Models;
using BackupNormalizer.Ui.ViewModels;
using BackupNormalizer.Ui.Views;

namespace BackupNormalizer.Tests;

[Collection("UI")]
public sealed class GuiInventoryScanLayoutTests
{
    [Theory]
    [InlineData(680, 430)]
    [InlineData(540, 380)]
    public void Setup_Binds_Folder_Root_And_Database_Without_Creating_Files(
        double width,
        double height
    )
    {
        UiTestHost.Run(() =>
        {
            var setup = new InventoryScanViewModel(new MainViewModel());
            var window = new InventoryScanWindow(setup) { Width = width, Height = height };
            try
            {
                window.Show();
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                string destination = Path.Combine(
                    Path.GetTempPath(),
                    "bn-gui-setup-" + Guid.NewGuid().ToString("N") + ".db"
                );
                window.FindControl<TextBox>("ScanDatabasePath")!.Text = destination;
                window.FindControl<TextBox>("ScanRootPath")!.Text = Path.GetTempPath();
                window.FindControl<TextBox>("ScanRootId")!.Text = "test";
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var request = setup.CreateRequest();
                Assert.Equal(destination, request.DatabasePath);
                Assert.Equal(Path.GetTempPath(), request.RecordedRootPath);
                Assert.Equal("test", request.RootId);
                Assert.Equal(InventoryJobKind.ScanThenHash, request.Kind);
                Assert.False(File.Exists(destination));
                var button = window.FindControl<Button>("StartScanButton")!;
                Assert.True(button.Bounds.Width > 0);
                Assert.True(button.Bounds.Height > 0);
                double inputWidth = window.FindControl<TextBox>("ScanDatabasePath")!.Bounds.Width;
                Assert.True(
                    inputWidth >= 160,
                    $"Database input width {inputWidth} at {width}x{height}."
                );
            }
            finally
            {
                window.Close();
            }
        });
    }
}
