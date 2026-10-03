using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using BackupNormalizer.Ui.Models;
using BackupNormalizer.Ui.ViewModels;
using BackupNormalizer.Ui.Views;

namespace BackupNormalizer.Tests;

[Collection("UI")]
public sealed class GuiWorkflowLayoutTests
{
    [Theory]
    [InlineData(1280, 800)]
    [InlineData(860, 560)]
    public void Inventory_Controls_Keep_File_Lists_Usable(double width, double height)
    {
        UiTestHost.Run(() =>
        {
            var rootRow = new StorageRootRow(
                "r",
                "Recorded",
                "/offline",
                false,
                "unknown",
                "sensitive",
                Database.UtcNow()
            );
            var root = new InventoryRoot(rootRow, ScanStatus.Completed);
            var directory = new InventoryNode("", "", true, root.Comparer);
            var file = new InventoryNode("file.txt", "file.txt", false, root.Comparer)
            {
                Size = 1,
                Digest = "abc",
            };
            directory.Children.Add(file.Name, file);
            root.Nodes.Add("", directory);
            root.Nodes.Add(file.RelativePath, file);
            var snapshot = new InventorySnapshot("/offline/inventory.db", new[] { root });
            var vm = new MainViewModel();
            vm.Left.LoadSnapshot(snapshot);
            vm.Right.LoadSnapshot(snapshot);
            vm.Left.SelectedEntry = vm.Left.Entries.Single();
            vm.Right.SelectedEntry = vm.Right.Entries.Single();
            var window = new MainWindow
            {
                DataContext = vm,
                Width = width,
                Height = height,
            };
            try
            {
                window.Show();
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                foreach (var grid in window.GetLogicalDescendants().OfType<DataGrid>())
                {
                    Assert.True(
                        grid.Bounds.Height >= 80,
                        $"File list height {grid.Bounds.Height} at {width}x{height}."
                    );
                    Assert.True(grid.Bounds.Width >= 250);
                }
            }
            finally
            {
                window.Close();
            }
        });
    }
}
