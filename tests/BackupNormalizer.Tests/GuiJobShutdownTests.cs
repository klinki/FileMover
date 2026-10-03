using Avalonia.Threading;
using BackupNormalizer.Ui.Models;
using BackupNormalizer.Ui.ViewModels;
using BackupNormalizer.Ui.Views;

namespace BackupNormalizer.Tests;

[Collection("UI")]
public sealed class GuiJobShutdownTests
{
    [Fact]
    public async Task Closing_The_Main_Window_Cancels_And_Awaits_The_Job()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "bn-gui-job-close-" + Guid.NewGuid().ToString("N")
        );
        string root = Path.Combine(directory, "files");
        string database = Path.Combine(directory, "inventory.db");
        Directory.CreateDirectory(root);
        MainWindow? window = null;
        Task? job = null;
        MainViewModel? vm = null;
        try
        {
            File.WriteAllBytes(Path.Combine(root, "large.bin"), new byte[8 * 1024 * 1024]);
            using (var db = Database.OpenWritable(database, pooling: false))
            {
                db.UpsertRoot(
                    new StorageRootRow(
                        "r",
                        "Files",
                        root,
                        true,
                        "unknown",
                        "sensitive",
                        Database.UtcNow()
                    )
                );
                new Scanner(db, usnMode: "off").ScanRoot("r");
            }
            var snapshot = InventorySnapshot.Load(database);
            UiTestHost.Run(() =>
            {
                vm = new MainViewModel();
                vm.Left.LoadSnapshot(snapshot);
                window = new MainWindow { DataContext = vm };
                window.Show();
                job = vm.RunInventoryJobAsync(InventoryJobKind.HashNeeded);
                Assert.True(vm.Job.IsRunning);
                window.Close();
            });

            // Headless tests explicitly pump the shared UI dispatcher for await continuations.
            var timeout = DateTime.UtcNow.AddSeconds(20);
            while (!job!.IsCompleted && DateTime.UtcNow < timeout)
            {
                await Task.Delay(10);
                UiTestHost.Run(() => Dispatcher.UIThread.RunJobs());
            }
            Assert.True(job.IsCompleted, "Job did not finish during window shutdown.");
            await job;
            UiTestHost.Run(() =>
            {
                Dispatcher.UIThread.RunJobs();
                Assert.False(window!.IsVisible);
                Assert.False(vm!.IsBusy);
                Assert.Equal(InventoryJobOutcome.Canceled, vm.Job.LastResult!.Outcome);
                Assert.False(vm.Job.CancellationRequested);
            });
            using var verify = Database.OpenReadOnly(database, pooling: false);
            Assert.Null(verify.GetHash(verify.GetFileEntry("r", "large.bin")!.Id, "sha256"));
        }
        finally
        {
            if (window != null)
            {
                UiTestHost.Run(() => window.Close());
            }

            Directory.Delete(directory, true);
        }
    }
}
