using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BackupNormalizer.Ui;
using BackupNormalizer.Ui.ViewModels;
using BackupNormalizer.Ui.Views;

namespace BackupNormalizer.Tests;

/// <summary>Headless reproduction: main window must list files in both panels.</summary>
public sealed class HeadlessPanelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bn-headless-" + Guid.NewGuid().ToString("N"));
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public HeadlessPanelTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "a");
        File.WriteAllText(Path.Combine(_dir, "b.jpg"), "b");
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Panels_List_Files()
    {
        var report = new List<string>();
        UiTestHost.Run(() =>
        {
            var vm = new MainViewModel { BasePath = _dir };
            vm.ApplyBase();
            report.Add($"model entries={vm.Left.Entries.Count} status='{vm.Left.Status}'");
            var window = new MainWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var grids = window.GetLogicalDescendants().OfType<DataGrid>().ToList();
            report.Add($"grids={grids.Count}");
            foreach (var g in grids)
            {
                int items = (g.ItemsSource as IList)?.Count ?? -1;
                int rows = g.GetLogicalDescendants().OfType<DataGridRow>().Count();
                report.Add($"grid tag={g.Tag} items={items} rows={rows} visible={g.IsVisible}");
            }
            window.Close();
        });
        foreach (var l in report) _out.WriteLine(l);
        Assert.DoesNotContain(report, l => l.Contains("items=0"));
    }

    [Fact]
    public void Default_Startup_Path_Lists_UserProfile()
    {
        var report = new List<string>();
        UiTestHost.Run(() =>
        {
            var vm = new MainViewModel(); // exactly what App creates at startup
            report.Add($"base='{vm.BasePath}' left='{vm.Left.CurrentPath}' entries={vm.Left.Entries.Count} status='{vm.Left.Status}'");
            var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            System.Threading.Thread.Sleep(200);
            Dispatcher.UIThread.RunJobs();
            int visibleBorders = window.GetLogicalDescendants().OfType<Border>()
                .Count(b => b.IsEffectivelyVisible && b.Child is DockPanel);
            report.Add($"visible panel borders={visibleBorders}");
            Assert.Equal(3, visibleBorders); // staged + exactly one border per side
            foreach (var g in window.GetLogicalDescendants().OfType<DataGrid>().Where(g => g.IsEffectivelyVisible))
            {
                int items = (g.ItemsSource as IList)?.Count ?? -1;
                int rows = g.GetVisualDescendants().OfType<DataGridRow>().Count();
                var texts = g.GetVisualDescendants().OfType<DataGridRow>()
                    .Take(3)
                    .Select(r => string.Join("|", r.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "")))
                    .ToList();
                report.Add($"VISIBLE grid tag={g.Tag} items={items} rows={rows} sample=[{string.Join(" ;; ", texts)}]");
            }
            window.Close();
        });
        foreach (var l in report) _out.WriteLine(l);
        Assert.DoesNotContain(report, l => l.Contains("items=0"));
    }

    [Fact]
    public void Marked_Row_Paints_All_Cells_Red()
    {
        UiTestHost.Run(() =>
        {
            var vm = new MainViewModel { BasePath = _dir };
            vm.ApplyBase();
            vm.Left.Entries.First(e => e.Name == "a.txt").IsMarked = true;
            var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
            window.Show();
            Pump4();
            var grid = window.GetLogicalDescendants().OfType<DataGrid>()
                .First(g => (g.Tag as string) == "Left" && g.IsEffectivelyVisible);
            var row = grid.GetVisualDescendants().OfType<DataGridRow>()
                .First(r => ((BackupNormalizer.Ui.ViewModels.FileEntryItem)r.DataContext!).Name == "a.txt");
            var cells = row.GetVisualDescendants().OfType<TextBlock>().ToList();
            Assert.True(cells.Count >= 4);
            foreach (var cell in cells)
            {
                var brush = cell.Foreground as Avalonia.Media.SolidColorBrush;
                Assert.NotNull(brush);
                Assert.Equal(Avalonia.Media.Color.Parse("#C62828"), brush!.Color);
            }
            window.Close();
        });
    }

    private static void Pump4()
    {
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(50);
        Dispatcher.UIThread.RunJobs();
    }
}

public sealed class DataGridThemeOverrideTests
{
    [Fact]
    public void Focus_Visuals_Are_Transparent_And_Selection_Is_Soft()
    {
        UiTestHost.Run(() =>
        {
            var window = new MainWindow { Width = 1100, Height = 700 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            SolidColorBrush Res(string key)
                => Assert.IsType<SolidColorBrush>(window.FindResource(key));
            Assert.Equal(Avalonia.Media.Colors.Transparent, Res("DataGridCellFocusVisualPrimaryBrush").Color);
            Assert.Equal(Avalonia.Media.Colors.Transparent, Res("DataGridCellFocusVisualSecondaryBrush").Color);
            Assert.Equal(Avalonia.Media.Color.Parse("#D6E9F8"), Res("DataGridRowSelectedBackgroundBrush").Color);
            Assert.Equal(Avalonia.Media.Color.Parse("#D6E9F8"), Res("DataGridRowSelectedUnfocusedBackgroundBrush").Color);
            foreach (var key in new[]
            {
                "DataGridRowSelectedBackgroundOpacity",
                "DataGridRowSelectedHoveredBackgroundOpacity",
                "DataGridRowSelectedUnfocusedBackgroundOpacity",
                "DataGridRowSelectedHoveredUnfocusedBackgroundOpacity",
            })
                Assert.Equal(1.0, Assert.IsType<double>(window.FindResource(key)));
            window.Close();
        });
    }
}
