using System.Collections;
using System.Collections.Concurrent;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using BackupNormalizer.Ui.Models;
using BackupNormalizer.Ui.ViewModels;
using BackupNormalizer.Ui.Views;

namespace BackupNormalizer.Tests;

[Collection("UI")]
public sealed class UiBackupCoverageTests
{
    [Fact]
    public void Large_Results_Are_Prepared_On_A_Worker_And_Published_Once_On_The_UI_Thread()
    {
        using var analysisStarted = new ManualResetEventSlim();
        using var releaseAnalysis = new ManualResetEventSlim();
        using var rowsStarted = new ManualResetEventSlim();
        using var releaseRows = new ManualResetEventSlim();
        var rowThreads = new ConcurrentBag<int>();
        var content = new ObservedList<CoverageContent>(
            Enumerable
                .Range(0, 50000)
                .Select(i => new CoverageContent(
                    i,
                    i.ToString("x64"),
                    i % 2 == 0 ? ["D"] : ["D", "G"],
                    [
                        new CoverageLocation(
                            "D",
                            Path.GetFullPath("coverage-test.db"),
                            "r",
                            $"folder/{i}.txt"
                        ),
                    ]
                ))
                .ToArray(),
            () =>
            {
                rowThreads.Add(Environment.CurrentManagedThreadId);
                rowsStarted.Set();
                Assert.True(releaseRows.Wait(TimeSpan.FromSeconds(10)));
            }
        );
        var report = new CoverageReport([], ["D", "G"], content, []);
        int analysisThread = 0;
        int uiThread = 0;
        int publications = 0;
        var publicationThreads = new List<int>();
        var vm = new BackupCoverageViewModel(_ =>
        {
            analysisThread = Environment.CurrentManagedThreadId;
            analysisStarted.Set();
            Assert.True(releaseAnalysis.Wait(TimeSpan.FromSeconds(10)));
            return report;
        });
        vm.Sources.Add(Source());
        BackupCoverageWindow? window = null;
        Task? analysis = null;
        try
        {
            UiTestHost.Run(() =>
            {
                uiThread = Environment.CurrentManagedThreadId;
                window = new BackupCoverageWindow(vm);
                window.Show();
                vm.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(vm.Entries))
                    {
                        publications++;
                        publicationThreads.Add(Environment.CurrentManagedThreadId);
                    }
                };
                analysis = StartOnUi(vm.Analyze);
            });
            Assert.True(analysisStarted.Wait(TimeSpan.FromSeconds(10)));
            Assert.NotEqual(uiThread, analysisThread);
            UiTestHost.Run(() =>
            {
                Assert.True(vm.IsBusy);
                Assert.False(window!.GetLogicalDescendants().OfType<ComboBox>().Single().IsEnabled);
                Assert.False(vm.AnalyzeCommand.CanExecute(null));
            });
            releaseAnalysis.Set();
            Assert.True(rowsStarted.Wait(TimeSpan.FromSeconds(10)));
            Assert.All(rowThreads, thread => Assert.NotEqual(uiThread, thread));
            UiTestHost.Run(() => Assert.True(vm.IsBusy));
            releaseRows.Set();
            FinishOnUi(analysis!);
            UiTestHost.Run(() =>
            {
                Assert.False(vm.IsBusy);
                Assert.Equal(25000, vm.Entries.Count);
                Assert.Equal(1, publications);
                Assert.All(publicationThreads, thread => Assert.Equal(uiThread, thread));
                int enumerations = rowThreads.Count;
                Assert.Contains(50000.ToString("N0"), vm.Summary);
                Assert.Equal(enumerations, rowThreads.Count);
                StartOnUi(() =>
                {
                    vm.Filter = "Every device";
                    return vm.FilterTask;
                });
            });
            FinishOnUi(vm.FilterTask);
            UiTestHost.Run(() =>
            {
                Assert.Equal(25000, vm.Entries.Count);
                Assert.All(vm.Entries, entry => Assert.Equal("2/2 devices", entry.Coverage));
                Assert.Equal(2, publications);
                Assert.All(rowThreads, thread => Assert.NotEqual(uiThread, thread));
                Assert.All(publicationThreads, thread => Assert.Equal(uiThread, thread));
            });
        }
        finally
        {
            releaseAnalysis.Set();
            releaseRows.Set();
            if (analysis != null)
                FinishOnUi(analysis);
            UiTestHost.Run(() => window?.Close());
        }
    }

    [Fact]
    public async Task Filter_Changes_During_Analysis_Use_The_Latest_Filter_And_Clear_Selection()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var location = new CoverageLocation(
            "D",
            Path.GetFullPath("coverage-test.db"),
            "r",
            "file.txt"
        );
        var report = new CoverageReport(
            [],
            ["D", "G"],
            [new CoverageContent(1, new string('a', 64), ["D"], [location])],
            [
                new UnverifiedCoverageFile(
                    location with
                    {
                        RelativePath = "unknown.txt",
                    },
                    2,
                    "No hash."
                ),
            ]
        );
        var vm = new BackupCoverageViewModel(_ =>
        {
            started.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return report;
        });
        vm.Sources.Add(Source());
        var analysis = vm.Analyze();
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
            vm.Filter = "Unverified entries";
        }
        finally
        {
            release.Set();
        }
        await analysis;
        Assert.True(Assert.Single(vm.Entries).IsUnverified);
        vm.SelectedEntry = vm.Entries[0];
        vm.Filter = "All verified content";
        vm.Filter = "Every device";
        await vm.FilterTask;
        Assert.Empty(vm.Entries);
        Assert.Null(vm.SelectedEntry);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Failed_Analysis_Clears_Results_And_Reenables_The_Dialog()
    {
        var vm = new BackupCoverageViewModel(_ =>
            throw new InvalidOperationException("Cannot read inventory.")
        );
        vm.Sources.Add(Source());
        await vm.Analyze();
        Assert.Null(vm.Report);
        Assert.Empty(vm.Entries);
        Assert.False(vm.IsBusy);
        Assert.True(vm.CanAnalyze);
        Assert.Contains("Cannot read inventory", vm.Status);
    }

    private static CoverageSourceItem Source() =>
        new(
            Path.GetFullPath("coverage-test.db"),
            new InventoryRoot(
                new StorageRootRow(
                    "r",
                    "Files",
                    "/offline",
                    false,
                    "D",
                    "sensitive",
                    Database.UtcNow()
                ),
                ScanStatus.Completed
            )
        );

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
            Assert.True(task.IsCompleted, "Coverage did not complete while pumping UI messages.");
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

    [Fact]
    public async Task Coverage_Window_Filters_Results_And_Label_Changes_Invalidate_The_Report()
    {
        string directory = Path.Combine(
            AppContext.BaseDirectory,
            "bn-ui-coverage-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "inventory.db");
            using (var db = Database.OpenWritable(path, pooling: false))
            {
                db.UpsertRoot(
                    new StorageRootRow(
                        "r",
                        "Files",
                        directory,
                        true,
                        "Drive",
                        "sensitive",
                        Database.UtcNow()
                    )
                );
                File.WriteAllText(Path.Combine(directory, "a.txt"), "same");
                new Scanner(db, usnMode: "off").ScanRoot("r");
                new Scanner(db).HashNeeded("r");
            }
            var vm = new BackupCoverageViewModel();
            await vm.AddDatabaseAsync(path);
            await vm.Analyze();
            Assert.Single(vm.Entries);
            vm.Filter = "Every device";
            await vm.FilterTask;
            Assert.Single(vm.Entries);
            vm.Filter = "Unverified entries";
            await vm.FilterTask;
            Assert.Empty(vm.Entries);
            UiTestHost.Run(() =>
            {
                var window = new BackupCoverageWindow(vm);
                window.Show();
                Assert.Equal(vm, window.DataContext);
                window.Close();
            });
            vm.Sources[0].DeviceId = "Other label";
            Assert.Null(vm.Report);
            Assert.Empty(vm.Entries);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
