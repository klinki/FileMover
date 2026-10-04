using System.Collections;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using BackupNormalizer;
using BackupNormalizer.Ui.ViewModels;
using BackupNormalizer.Ui.Views;

namespace BackupNormalizer.Tests;

[Collection("UI")]
public sealed class UiLocationChangesTests : IDisposable
{
    private readonly LocationChangesFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Selections_Filtering_Exports_Swap_And_Refresh_Follow_The_Report_Lifecycle()
    {
        string a = _fixture.Seed("a", ["old"]);
        string b = _fixture.Seed("b", ["new"]);
        _fixture.Seed("a", ["unchanged"], root: "other");
        using var vm = new LocationChangesViewModel();
        await vm.LoadAsync("A", a, "r");
        await vm.LoadAsync("B", b, "r");
        Assert.Equal("r", vm.SelectedA!.Input.RootId);
        Assert.Equal(2, vm.RootsA.Count);
        Assert.True(vm.CanAnalyze);
        await vm.Analyze();
        Assert.True(vm.CanExport);
        Assert.Equal("old", Assert.Single(vm.Entries).PathA);
        Assert.Equal("new", vm.Entries[0].PathB);
        Assert.Contains("Unverified files: 0", vm.Summary);
        vm.SelectedEntry = vm.Entries[0];
        vm.Filter = "Copied";
        await vm.FilterTask;
        Assert.Empty(vm.Entries);
        Assert.Null(vm.SelectedEntry);
        string output = Path.Combine(_fixture.DirectoryPath, "ui.json");
        await vm.ExportAsync(output, "json");
        Assert.Contains("\"groups\": []", File.ReadAllText(output));
        vm.Filter = "Location changes";
        await vm.FilterTask;
        vm.Swap();
        Assert.Null(vm.Report);
        Assert.False(vm.CanExport);
        await vm.Analyze();
        Assert.Equal("new", Assert.Single(vm.Entries).PathA);
        Assert.Equal("old", vm.Entries[0].PathB);
        await vm.Refresh();
        Assert.Null(vm.Report);
        Assert.Equal(b, vm.SelectedA!.Input.DatabasePath);
        Assert.Equal(a, vm.SelectedB!.Input.DatabasePath);
        Assert.False(vm.CanExport);
        await vm.Analyze();
        vm.SelectedB = vm.RootsB.Single(root => root.Input.RootId == "other");
        Assert.Null(vm.Report);
        Assert.Empty(vm.Entries);
    }

    [Fact]
    public async Task Failed_Load_Preserves_Selections_But_Clears_Report_And_Incomplete_Analysis_Disables_Export()
    {
        string a = _fixture.Seed("a", ["old"]);
        string b = _fixture.Seed("b", ["new"]);
        using var vm = new LocationChangesViewModel();
        await vm.LoadAsync("A", a);
        await vm.LoadAsync("B", b);
        await vm.Analyze();
        await vm.LoadAsync("A", _fixture.PathFor("missing"));
        Assert.Equal(a, vm.SelectedA!.Input.DatabasePath);
        Assert.False(vm.CanExport);
        Assert.Contains("Cannot load", vm.Status);
        using (var db = Database.OpenWritable(b, pooling: false))
        {
            long scan = db.BeginScan("r");
            db.FinishScan(scan, ScanStatus.Incomplete);
        }
        await vm.Analyze();
        Assert.Null(vm.Report);
        Assert.False(vm.CanExport);
        Assert.Contains("complete successful scan", vm.Status);
        Assert.False(vm.IsBusy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_And_Window_Disposal_Never_Publish_Partial_Reports(bool dispose)
    {
        string a = _fixture.Seed("a", ["old"]);
        string b = _fixture.Seed("b", ["new"]);
        using var started = new ManualResetEventSlim();
        using var vm = new LocationChangesViewModel(
            (_, _, token, _) =>
            {
                started.Set();
                Assert.True(token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)));
                token.ThrowIfCancellationRequested();
                throw new InvalidOperationException("Cancellation was expected.");
            }
        );
        await vm.LoadAsync("A", a);
        await vm.LoadAsync("B", b);
        var task = vm.Analyze();
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        Assert.True(vm.IsBusy);
        Assert.True(vm.CanCancel);
        Assert.False(vm.CanExport);
        if (dispose)
            vm.Dispose();
        else
            vm.Cancel();
        await task;
        Assert.Null(vm.Report);
        Assert.Empty(vm.Entries);
        Assert.False(vm.IsBusy);
        Assert.False(vm.CanExport);
        Assert.Contains("cancelled", vm.Status);
    }

    [Fact]
    public async Task Programmatic_Root_Changes_Cancel_An_In_Flight_Report()
    {
        string a = _fixture.Seed("a", ["old"]);
        _fixture.Seed("a", ["other"], root: "other");
        string b = _fixture.Seed("b", ["new"]);
        using var started = new ManualResetEventSlim();
        using var vm = new LocationChangesViewModel(
            (_, _, token, _) =>
            {
                started.Set();
                Assert.True(token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)));
                token.ThrowIfCancellationRequested();
                throw new InvalidOperationException("Cancellation was expected.");
            }
        );
        await vm.LoadAsync("A", a, "r");
        await vm.LoadAsync("B", b);
        var task = vm.Analyze();
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        vm.SelectedA = vm.RootsA.Single(root => root.Input.RootId == "other");
        await task;
        Assert.Null(vm.Report);
        Assert.Empty(vm.Entries);
    }

    [Fact]
    public async Task Window_With_Loaded_Roots_Preserves_Selections_And_Exports_After_Analysis()
    {
        string a = _fixture.Seed("a", ["old"]);
        string b = _fixture.Seed("b", ["new"]);
        using var vm = new LocationChangesViewModel();
        await vm.LoadAsync("A", a);
        await vm.LoadAsync("B", b);
        LocationChangesWindow? window = null;
        Task? analysis = null;
        try
        {
            UiTestHost.Run(() =>
            {
                window = new LocationChangesWindow(vm) { Width = 800, Height = 620 };
                window.Show();
                analysis = StartOnUi(vm.Analyze);
            });
            FinishOnUi(analysis!);
            UiTestHost.Run(() =>
            {
                Assert.True(vm.CanExport);
                Assert.Equal("r", vm.SelectedA!.Input.RootId);
                Assert.Equal("r", vm.SelectedB!.Input.RootId);
                Assert.Equal("old", Assert.Single(vm.Entries).PathA);
                Assert.Contains("SHA-256: 1/1", vm.SelectedA.Details);
                var grid = window!.GetLogicalDescendants().OfType<DataGrid>().Single();
                Assert.True(grid.Bounds.Width > 0);
                Assert.True(grid.Bounds.Height > 0);
                Assert.All(
                    window!
                        .GetLogicalDescendants()
                        .OfType<Button>()
                        .Where(button => button.Content?.ToString()?.StartsWith("Export") == true),
                    button => Assert.True(button.IsEnabled)
                );
            });
        }
        finally
        {
            if (analysis != null)
                FinishOnUi(analysis);
            UiTestHost.Run(() => window?.Close());
        }
    }

    [Fact]
    public void Large_Analysis_And_Filter_Projection_Run_On_Workers_And_Window_Commands_Remain_Responsive()
    {
        _fixture.Seed("a", ["old"]);
        _fixture.Seed("b", ["new"]);
        var baseReport = _fixture.Analyze();
        int uiThread = 0,
            analysisThread = 0;
        var projectionThreads = new List<int>();
        var groups = new ObservedList<LocationChangeGroup>(
            Enumerable
                .Range(0, 20000)
                .Select(i =>
                    baseReport.Groups[0] with
                    {
                        Id = i.ToString(),
                        Classification =
                            i % 2 == 0 ? FileLocationChanges.Moved : FileLocationChanges.Unchanged,
                    }
                )
                .ToArray(),
            () => projectionThreads.Add(Environment.CurrentManagedThreadId)
        );
        var report = baseReport with
        {
            Groups = groups,
            Summary = new Dictionary<string, int>
            {
                [FileLocationChanges.Moved] = 10000,
                [FileLocationChanges.Unchanged] = 10000,
            },
        };
        using var analysisStarted = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var vm = new LocationChangesViewModel(
            (_, _, token, _) =>
            {
                analysisThread = Environment.CurrentManagedThreadId;
                analysisStarted.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10), token));
                return report;
            }
        );
        vm.SelectedA = new(baseReport.A.Input, "r", "/offline", ScanStatus.Completed, null, 1, 1);
        vm.SelectedB = new(baseReport.B.Input, "r", "/offline", ScanStatus.Completed, null, 1, 1);
        LocationChangesWindow? window = null;
        Task? analysis = null;
        try
        {
            UiTestHost.Run(() =>
            {
                uiThread = Environment.CurrentManagedThreadId;
                window = new LocationChangesWindow(vm);
                window.Show();
                analysis = StartOnUi(vm.Analyze);
            });
            Assert.True(analysisStarted.Wait(TimeSpan.FromSeconds(10)));
            UiTestHost.Run(() =>
            {
                Assert.True(vm.IsBusy);
                Assert.True(vm.CancelCommand.CanExecute(null));
                Assert.False(vm.AnalyzeCommand.CanExecute(null));
                var buttons = window!.GetLogicalDescendants().OfType<Button>();
                Assert.All(
                    buttons.Where(b => b.Content?.ToString()?.StartsWith("Export") == true),
                    b => Assert.False(b.IsEnabled)
                );
                Assert.Equal(
                    5,
                    window!.GetLogicalDescendants().OfType<DataGrid>().Single().Columns.Count
                );
            });
            release.Set();
            FinishOnUi(analysis!);
            UiTestHost.Run(() =>
            {
                Assert.Equal(10000, vm.Entries.Count);
                Assert.True(vm.CanExport);
                StartOnUi(() =>
                {
                    vm.Filter = "Unchanged";
                    return vm.FilterTask;
                });
            });
            FinishOnUi(vm.FilterTask);
            UiTestHost.Run(() =>
                Assert.All(
                    vm.Entries,
                    entry => Assert.Equal(FileLocationChanges.Unchanged, entry.Classification)
                )
            );
            Assert.NotEqual(uiThread, analysisThread);
            Assert.All(projectionThreads, thread => Assert.NotEqual(uiThread, thread));
            Assert.True(projectionThreads.Count >= 2);
        }
        finally
        {
            release.Set();
            if (analysis != null)
                FinishOnUi(analysis);
            UiTestHost.Run(() => window?.Close());
        }
    }

    private static Task StartOnUi(Func<Task> action)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext());
        try
        {
            return action();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private static void FinishOnUi(Task task) =>
        UiTestHost.Run(() =>
        {
            if (!task.IsCompleted)
            {
                var frame = new DispatcherFrame();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var registration = timeout.Token.Register(() =>
                    Dispatcher.UIThread.Post(() => frame.Continue = false)
                );
                _ = task.ContinueWith(
                    _ => Dispatcher.UIThread.Post(() => frame.Continue = false),
                    TaskScheduler.Default
                );
                Dispatcher.UIThread.PushFrame(frame);
            }
            Assert.True(
                task.IsCompleted,
                "Location report did not complete while processing UI messages."
            );
            task.GetAwaiter().GetResult();
        });

    private sealed class ObservedList<T>(IReadOnlyList<T> items, Action onEnumerate)
        : IReadOnlyList<T>
    {
        public int Count => items.Count;
        public T this[int index] => items[index];

        public IEnumerator<T> GetEnumerator()
        {
            onEnumerate();
            return items.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
