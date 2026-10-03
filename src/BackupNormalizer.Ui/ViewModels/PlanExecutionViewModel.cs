using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BackupNormalizer.Ui.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BackupNormalizer.Ui.ViewModels;

public sealed partial class ExecutionOperationItem(PlanOpDoc operation) : ObservableObject
{
    public int Id => operation.Id;
    public string Type => operation.Type;
    public string Source => operation.SourcePath ?? "";
    public string Destination => operation.DestinationPath ?? "";
    public string SizeText => operation.ExpectedSize.ToString("N0");

    [ObservableProperty]
    public partial string Status { get; set; } = OpStatus.Planned;

    [ObservableProperty]
    public partial string Note { get; set; } = operation.SkipReason ?? "";

    [ObservableProperty]
    public partial string Verification { get; set; } = "";
}

public sealed partial class PlanExecutionViewModel : ObservableObject
{
    private readonly ExecutionJobRunner _runner = new();
    private CancellationTokenSource? _cancellation;
    private Task? _task;
    public event Action<bool>? RunningChanged;
    public PlanDoc Document { get; }
    public ObservableCollection<ExecutionOperationItem> Operations { get; } = new();
    public ObservableCollection<string> Activity { get; } = new();
    public ExecutionRunResult? LastResult { get; private set; }

    [ObservableProperty]
    public partial string SourcePath { get; set; }

    [ObservableProperty]
    public partial string TargetPath { get; set; }

    [ObservableProperty]
    public partial string DatabasePath { get; set; }

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial bool HasJournal { get; set; }

    [ObservableProperty]
    public partial bool RootsBound { get; set; }

    [ObservableProperty]
    public partial bool CancellationRequested { get; set; }

    [ObservableProperty]
    public partial bool StopOnError { get; set; } = true;

    [ObservableProperty]
    public partial bool VerifyAfterExecution { get; set; } = true;

    [ObservableProperty]
    public partial string Status { get; set; } =
        "Review operations and local root paths before starting.";

    [ObservableProperty]
    public partial string ProgressText { get; set; } = "Execution has not started.";

    [ObservableProperty]
    public partial double ProgressPercent { get; set; }

    [ObservableProperty]
    public partial string CurrentPath { get; set; } = "";

    [ObservableProperty]
    public partial string TransferText { get; set; } = "";
    public bool CanRun => !IsRunning;
    public bool CanVerify => !IsRunning && HasJournal;
    public bool CanEditRoots => !IsRunning && !RootsBound;
    public bool CanEditJournal => !IsRunning && !HasJournal;
    public string RunLabel => HasJournal ? "Resume execution..." : "Execute reviewed plan...";
    public string Summary =>
        $"Plan {Document.PlanId}: {Document.Operations.Count:N0} operations, {Document.EstimatedBytesCopied:N0} copy bytes. Files change only after confirmation.";

    public PlanExecutionViewModel(PlanDoc document, string? databasePath = null)
    {
        Document = document;
        SourcePath = document.SourcePath ?? "";
        TargetPath = document.TargetPath;
        DatabasePath =
            databasePath
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BackupNormalizer",
                "executions",
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")
                    + "-"
                    + Guid.NewGuid().ToString("N")
                    + ".db"
            );
        foreach (var operation in document.Operations.OrderBy(op => op.Id))
        {
            Operations.Add(new ExecutionOperationItem(operation));
        }
    }

    public static PlanExecutionViewModel FromSession(ExecutionSession session)
    {
        var vm = new PlanExecutionViewModel(session.Document, session.DatabasePath)
        {
            SourcePath = session.SourcePath,
            TargetPath = session.TargetPath,
            HasJournal = true,
            RootsBound = session.RootsBound,
            Status =
                "Existing execution loaded. Review saved statuses before resuming or verifying.",
        };
        vm.ApplySession(session);
        return vm;
    }

    public Task RunAsync(bool verifyOnly = false)
    {
        if (IsRunning)
        {
            return _task ?? Task.CompletedTask;
        }

        if (verifyOnly && !HasJournal)
        {
            return Task.CompletedTask;
        }

        var request = new ExecutionRunRequest(
            Document,
            DatabasePath,
            SourcePath,
            TargetPath,
            HasJournal,
            verifyOnly,
            StopOnError,
            VerifyAfterExecution
        );
        _cancellation = new CancellationTokenSource();
        CancellationRequested = false;
        ProgressPercent = 0;
        foreach (var operation in Operations)
        {
            operation.Verification = "";
        }

        LastResult = null;
        Status = verifyOnly
            ? "Verifying recorded file destinations..."
            : "Executing reviewed plan...";
        IsRunning = true;
        _task = RunCoreAsync(request, _cancellation);
        return _task;
    }

    [RelayCommand]
    public void Cancel()
    {
        if (_cancellation == null)
        {
            return;
        }

        CancellationRequested = true;
        Status = "Cancellation requested. The current operation will finish before stopping.";
        _cancellation.Cancel();
    }

    public async Task CancelAndWaitAsync()
    {
        if (!IsRunning || _task == null)
        {
            return;
        }

        Cancel();
        await _task;
    }

    private async Task RunCoreAsync(
        ExecutionRunRequest request,
        CancellationTokenSource cancellation
    )
    {
        try
        {
            long copied = 0,
                executionBytesRead = 0;
            TimeSpan executionElapsed = TimeSpan.Zero;
            var progress = new Progress<ExecutionProgress>(value =>
            {
                if (!ReferenceEquals(_cancellation, cancellation) || !IsRunning)
                {
                    return;
                }

                CurrentPath = value.Path;
                int processed = value.Status is "Started" or "Verifying"
                    ? value.Position - 1
                    : value.Completed + value.Failed + value.Skipped + value.Conflicts;
                ProgressPercent =
                    value.Total == 0
                        ? 0
                        : 100.0 * Math.Clamp(processed, 0, value.Total) / value.Total;
                ProgressText =
                    $"{value.Status}: {processed:N0}/{value.Total:N0} operations | completed {value.Completed:N0}, failed {value.Failed:N0}, skipped {value.Skipped:N0}, conflicts {value.Conflicts:N0}";
                bool verifying = value.Status.StartsWith("Verif", StringComparison.Ordinal);
                if (!verifying)
                {
                    copied = value.BytesCopied;
                    executionBytesRead = value.BytesRead;
                    executionElapsed = value.Elapsed;
                }
                long bytesRead = verifying ? executionBytesRead + value.BytesRead : value.BytesRead;
                var elapsed = verifying ? executionElapsed + value.Elapsed : value.Elapsed;
                double rate =
                    elapsed.TotalSeconds > 0
                        ? (copied + bytesRead) / elapsed.TotalSeconds / (1024 * 1024)
                        : 0;
                TransferText =
                    $"Copied {copied:N0} bytes | hashed {bytesRead:N0} bytes | I/O {rate:F1} MiB/s | elapsed {elapsed:hh\\:mm\\:ss}";
                if (value.Position > 0 && value.Position <= Operations.Count)
                {
                    var row = Operations[value.Position - 1];
                    if (
                        value.Status
                        is "Verifying"
                            or "Verified"
                            or "Verification failed"
                            or "Verification skipped"
                    )
                    {
                        row.Verification = value.Status;
                    }
                    else if (
                        value.Status
                        is OpStatus.Started
                            or OpStatus.Completed
                            or OpStatus.Skipped
                            or OpStatus.Conflict
                            or OpStatus.Failed
                    )
                    {
                        row.Status = value.Status;
                    }

                    if (value.Message != null)
                    {
                        row.Note = value.Message;
                    }
                }
            });
            var result = await _runner.RunAsync(request, progress, cancellation.Token);
            LastResult = result;
            if (result.Session != null)
            {
                HasJournal = true;
                RootsBound = result.Session.RootsBound;
                ApplySession(result.Session);
            }
            if (result.Verification != null)
            {
                foreach (var issue in result.Verification.Issues)
                {
                    var row = Operations.FirstOrDefault(op => op.Id == issue.OperationId);
                    if (row != null)
                    {
                        row.Verification = "Verification failed";
                        row.Note = issue.Message;
                    }
                    Activity.Add($"VERIFY {issue.Path}: {issue.Message}");
                }
            }

            Status = $"{result.Outcome}: {result.Message}";
            Activity.Add(Status);
        }
        catch (Exception ex)
        {
            Status = "Execution failed: " + ex.Message;
        }
        finally
        {
            _cancellation = null;
            cancellation.Dispose();
            CancellationRequested = false;
            IsRunning = false;
        }
    }

    private void ApplySession(ExecutionSession session)
    {
        foreach (var operation in session.Operations)
        {
            var row = Operations.FirstOrDefault(item => item.Id == operation.Sequence);
            if (row != null)
            {
                row.Status = operation.Status;
                row.Note = operation.Error ?? operation.SkipReason ?? "";
            }
        }
        Activity.Clear();
        foreach (var log in session.Logs.TakeLast(1000))
        {
            Activity.Add($"{log.TimestampUtc} {log.Level}: {log.Message}");
        }
    }

    partial void OnIsRunningChanged(bool value)
    {
        NotifyCommands();
        RunningChanged?.Invoke(value);
    }

    partial void OnHasJournalChanged(bool value) => NotifyCommands();

    partial void OnRootsBoundChanged(bool value) => NotifyCommands();

    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(CanVerify));
        OnPropertyChanged(nameof(CanEditRoots));
        OnPropertyChanged(nameof(CanEditJournal));
        OnPropertyChanged(nameof(RunLabel));
    }
}
