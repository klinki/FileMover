using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BackupNormalizer.Ui.Models;
using CommunityToolkit.Mvvm.Input;

namespace BackupNormalizer.Ui.ViewModels;

public sealed partial class MainViewModel
{
    private readonly InventoryJobRunner _inventoryJobRunner = new();
    private CancellationTokenSource? _inventoryJobCancellation;
    private Task? _inventoryJobTask;

    public InventoryJobViewModel Job { get; } = new();

    public bool CanRunInventoryJob
    {
        get
        {
            var panel = Active;
            var snapshot = panel.Snapshot;
            var root = panel.SelectedInventoryRoot;
            return !IsBusy && CanChangePanelSource && snapshot != null && root?.LocalRootAvailable == true;
        }
    }

    public string InventoryJobAvailability
    {
        get
        {
            if (IsBusy) return "Wait for the current operation to finish.";
            if (_stagedCore.Count != 0) return "Clear staged operations before starting a background job.";
            var panel = Active;
            if (panel.Snapshot == null || panel.SelectedInventoryRoot == null)
                return "Select a loaded inventory database root.";
            if (!Path.IsPathFullyQualified(panel.SelectedInventoryRoot.Root.Path))
                return "The recorded root path is not a fully qualified path on this computer.";
            if (!panel.SelectedInventoryRoot.LocalRootAvailable)
                return "The recorded root is unavailable on this computer. Refresh after reconnecting the drive.";
            return "Ready to run against the recorded local root path.";
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunInventoryJob))]
    public Task RunScanJob() => RunInventoryJobAsync(InventoryJobKind.Scan);

    [RelayCommand(CanExecute = nameof(CanRunInventoryJob))]
    public Task RunHashNeededJob() => RunInventoryJobAsync(InventoryJobKind.HashNeeded);

    [RelayCommand(CanExecute = nameof(CanRunInventoryJob))]
    public Task RunScanThenHashJob() => RunInventoryJobAsync(InventoryJobKind.ScanThenHash);

    [RelayCommand(CanExecute = nameof(CanCancelInventoryJob))]
    public void CancelInventoryJob()
    {
        if (_inventoryJobCancellation == null) return;
        Job.CancellationRequested = true;
        _inventoryJobCancellation.Cancel();
        CancelInventoryJobCommand.NotifyCanExecuteChanged();
    }

    private bool CanCancelInventoryJob => Job.IsRunning && _inventoryJobCancellation is { IsCancellationRequested: false };

    public Task RunInventoryJobAsync(InventoryJobKind kind)
    {
        if (_inventoryJobTask is { IsCompleted: false }) return _inventoryJobTask;
        if (!CanRunInventoryJob)
        {
            StatusMessage = InventoryJobAvailability;
            return Task.CompletedTask;
        }

        var panel = Active;
        var root = panel.SelectedInventoryRoot!;
        var request = new InventoryJobRequest(panel.Snapshot!.DatabasePath, root.Root.Id,
            root.Root.Path, kind, Job.FullScan, Parallelism: 2);
        _inventoryJobCancellation = new CancellationTokenSource();
        Job.Start(kind);
        Job.Context = $"{root.Display} | {request.DatabasePath}";
        IsBusy = true;
        StatusMessage = $"Running {kind} for {root.Root.Name}...";
        NotifyInventoryJobCommands();
        _inventoryJobTask = RunInventoryJobCoreAsync(request, _inventoryJobCancellation);
        return _inventoryJobTask;
    }

    public async Task CancelAndWaitForJobAsync()
    {
        var task = _inventoryJobTask;
        if (task == null || task.IsCompleted) return;
        CancelInventoryJob();
        try { await task; }
        catch { }
    }

    private async Task RunInventoryJobCoreAsync(InventoryJobRequest request, CancellationTokenSource cancellation)
    {
        InventoryJobResult result;
        try
        {
            var progress = new Progress<InventoryJobProgress>(value =>
            {
                if (ReferenceEquals(_inventoryJobCancellation, cancellation) && Job.IsRunning)
                    Job.Progress = value;
            });
            result = await _inventoryJobRunner.RunAsync(request, progress, cancellation.Token);
        }
        catch (Exception ex)
        {
            result = new InventoryJobResult(request.Kind, InventoryJobOutcome.Failed, ex.Message, TimeSpan.Zero);
        }

        try
        {
            await ReloadAffectedJobSnapshotsAsync(request.DatabasePath);
        }
        catch (Exception ex)
        {
            result = result with { Message = result.Message + " Inventory refresh failed: " + ex.Message };
        }

        Job.Complete(result);
        StatusMessage = result.Message;
        _inventoryJobCancellation = null;
        cancellation.Dispose();
        IsBusy = false;
        NotifyInventoryJobCommands();
    }

    private async Task ReloadAffectedJobSnapshotsAsync(string databasePath)
    {
        var panels = new[] { Left, Right }
            .Where(panel => panel.Snapshot != null && Paths.PathEquals(panel.Snapshot.DatabasePath, databasePath))
            .Select(panel => (Panel: panel, RootId: panel.SelectedInventoryRoot?.Root.Id, Path: panel.InventoryPath,
                DatabasePath: panel.Snapshot!.DatabasePath))
            .ToArray();
        if (panels.Length == 0) return;

        var replacements = await Task.Run(() => panels
            .Select(panel => InventorySnapshot.Load(panel.DatabasePath)).ToArray());
        for (int i = 0; i < panels.Length; i++)
            panels[i].Panel.LoadSnapshot(replacements[i], panels[i].RootId, panels[i].Path);
    }

    private void NotifyInventoryJobCommands()
    {
        OnPropertyChanged(nameof(CanRunInventoryJob));
        OnPropertyChanged(nameof(InventoryJobAvailability));
        RunScanJobCommand.NotifyCanExecuteChanged();
        RunHashNeededJobCommand.NotifyCanExecuteChanged();
        RunScanThenHashJobCommand.NotifyCanExecuteChanged();
        CancelInventoryJobCommand.NotifyCanExecuteChanged();
    }
}
