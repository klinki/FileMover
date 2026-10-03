using System;
using BackupNormalizer.Ui.Models;

namespace BackupNormalizer.Ui.ViewModels;

public sealed partial class MainViewModel
{
    public PlanExecutionViewModel? Execution { get; private set; }

    public PlanExecutionViewModel? ReviewStagedExecution()
    {
        if (IsBusy || _stagedCore.Count == 0)
        {
            StatusMessage = "Stage operations before reviewing an execution.";
            return null;
        }
        try
        {
            var document = PlanStaging.BuildPlanDoc(
                PlanId.Trim(),
                RootId.Trim(),
                AppliedBasePath,
                _stagedCore
            );
            var execution = new PlanExecutionViewModel(document);
            AttachExecution(execution);
            return execution;
        }
        catch (Exception ex)
        {
            StatusMessage = "Cannot review execution: " + ex.Message;
            return null;
        }
    }

    public void AttachExecution(PlanExecutionViewModel execution)
    {
        if (IsBusy)
        {
            throw new InvalidOperationException(
                "Wait for the active job before opening another execution."
            );
        }

        if (Execution != null)
        {
            Execution.RunningChanged -= OnExecutionRunningChanged;
        }

        Execution = execution;
        Execution.RunningChanged += OnExecutionRunningChanged;
    }

    private void OnExecutionRunningChanged(bool running)
    {
        IsBusy = running;
        if (!running)
        {
            if (Left.IsLive)
            {
                Left.Refresh();
            }

            if (Right.IsLive)
            {
                Right.Refresh();
            }

            StatusMessage = Execution?.Status ?? "Execution finished.";
        }
    }
}
