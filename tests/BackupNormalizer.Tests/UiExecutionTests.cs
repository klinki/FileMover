using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using BackupNormalizer.Ui.Models;
using BackupNormalizer.Ui.ViewModels;
using BackupNormalizer.Ui.Views;

namespace BackupNormalizer.Tests;

[Collection("UI")]
public sealed class UiExecutionTests
{
    [Fact]
    public async Task Execution_Window_Binds_Progress_And_Reopens_With_Locked_Roots()
    {
        string directory = Path.Combine(
            AppContext.BaseDirectory,
            "bn-ui-execution-" + Guid.NewGuid().ToString("N")
        );
        string root = Path.Combine(directory, "files");
        string journal = Path.Combine(directory, "execution.db");
        Directory.CreateDirectory(root);
        PlanExecutionWindow? window = null;
        Task? run = null;
        PlanExecutionViewModel? vm = null;
        try
        {
            File.WriteAllText(Path.Combine(root, "file.txt"), "copy me");
            var document = PlanStaging.BuildPlanDoc(
                "desktop-run",
                "disk",
                root,
                PlanStaging.StageCopy(
                    root,
                    Path.Combine(root, "file.txt"),
                    Path.Combine(root, "copies")
                )
            );
            UiTestHost.Run(() =>
            {
                vm = new PlanExecutionViewModel(document, journal);
                window = new PlanExecutionWindow(vm) { Width = 800, Height = 660 };
                window.Show();
                window.UpdateLayout();
                Assert.Same(vm, window.DataContext);
                Assert.True(vm.CanEditRoots);
                Assert.False(File.Exists(journal));
                Assert.False(Directory.Exists(Path.Combine(root, "copies")));
                var grid = Assert.Single(window.GetLogicalDescendants().OfType<DataGrid>());
                Assert.True(
                    grid.Bounds.Height >= 80,
                    $"Operation list height was {grid.Bounds.Height}."
                );
                run = vm.RunAsync();
                Assert.True(vm.IsRunning);
                Assert.False(vm.CanEditRoots);
                Assert.False(vm.CanEditJournal);
            });
            await PumpUntilComplete(run!);
            UiTestHost.Run(() =>
            {
                Assert.Equal("Completed", vm!.LastResult!.Outcome);
                Assert.True(vm.HasJournal);
                Assert.False(vm.CanEditRoots);
                Assert.True(vm.CanVerify);
                Assert.All(vm.Operations, op => Assert.Equal(OpStatus.Completed, op.Status));
                Assert.Contains("Verified", vm.Operations.Select(op => op.Verification));
                Assert.Equal(100, vm.ProgressPercent);
                Assert.NotEmpty(vm.TransferText);
                Assert.Contains("Copied 7 bytes", vm.TransferText);
                Assert.NotEmpty(vm.Activity);
                window!.Close();
                var reopened = PlanExecutionViewModel.FromSession(ExecutionSession.Load(journal));
                Assert.False(reopened.CanEditRoots);
                Assert.False(reopened.CanEditJournal);
                Assert.True(reopened.CanVerify);
                Assert.Contains("Resume", reopened.RunLabel);
                Assert.All(reopened.Operations, op => Assert.Equal(OpStatus.Completed, op.Status));
            });
        }
        finally
        {
            if (window != null)
            {
                UiTestHost.Run(window.Close);
            }

            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Closing_The_Execution_Window_Cancels_And_Waits_For_The_Job()
    {
        string directory = Path.Combine(
            AppContext.BaseDirectory,
            "bn-ui-execution-close-" + Guid.NewGuid().ToString("N")
        );
        string root = Path.Combine(directory, "files");
        Directory.CreateDirectory(root);
        PlanExecutionWindow? window = null;
        Task? run = null;
        MainViewModel? main = null;
        PlanExecutionViewModel? execution = null;
        try
        {
            var document = PlanStaging.BuildPlanDoc(
                "cancel-desktop",
                "disk",
                root,
                [new PlanStaging.StagedOp(OpType.Mkdir, "", "new-directory", 0, null)]
            );
            UiTestHost.Run(() =>
            {
                main = new MainViewModel();
                execution = new PlanExecutionViewModel(
                    document,
                    Path.Combine(directory, "execution.db")
                );
                main.AttachExecution(execution);
                window = new PlanExecutionWindow(execution);
                window.Show();
                run = execution.RunAsync();
                Assert.True(main.IsBusy);
                window.Close();
                Assert.True(execution.CancellationRequested);
            });
            await PumpUntilComplete(run!);
            UiTestHost.Run(() =>
            {
                Assert.False(window!.IsVisible);
                Assert.False(main!.IsBusy);
                Assert.False(execution!.IsRunning);
                Assert.Equal("Canceled", execution.LastResult!.Outcome);
            });
        }
        finally
        {
            if (window != null)
            {
                UiTestHost.Run(window.Close);
            }

            Directory.Delete(directory, true);
        }
    }

    private static async Task PumpUntilComplete(Task task)
    {
        var timeout = DateTime.UtcNow.AddSeconds(20);
        while (!task.IsCompleted && DateTime.UtcNow < timeout)
        {
            await Task.Delay(10);
            UiTestHost.Run(() => Dispatcher.UIThread.RunJobs());
        }
        Assert.True(task.IsCompleted, "Execution did not finish before the timeout.");
        await task;
        UiTestHost.Run(() => Dispatcher.UIThread.RunJobs());
    }
}
