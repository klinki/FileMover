using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BackupNormalizer.Ui.Models;

namespace BackupNormalizer.Ui.ViewModels;

public sealed partial class MainViewModel
{
    private long _databasePlanSelectionVersion;

    /// <summary>Database planning always uses both complete selected roots, regardless of panel folder navigation.</summary>
    public bool CanCreateDatabasePlan =>
        !IsBusy
        && Left.Snapshot != null
        && Left.SelectedInventoryRoot != null
        && Right.Snapshot != null
        && Right.SelectedInventoryRoot != null;

    /// <summary>
    /// Call when either panel source or selected inventory root changes. This invalidates an in-flight
    /// plan build so it cannot return a review for a selection the user has already changed.
    /// </summary>
    public void InvalidateDatabasePlanReview()
    {
        Interlocked.Increment(ref _databasePlanSelectionVersion);
        OnPropertyChanged(nameof(CanCreateDatabasePlan));
    }

    public async Task<DatabasePlanReviewViewModel?> PreparePlanAsync(
        DatabasePlanDirection direction
    )
    {
        if (!CanCreateDatabasePlan)
        {
            StatusMessage =
                "Load an inventory on both panels and select a root on each before planning.";
            return null;
        }
        if (string.IsNullOrWhiteSpace(PlanId))
        {
            StatusMessage = "Plan ID is required.";
            return null;
        }

        var sourcePanel = direction == DatabasePlanDirection.LeftToRight ? Left : Right;
        var targetPanel = direction == DatabasePlanDirection.LeftToRight ? Right : Left;
        var sourceSnapshot = sourcePanel.Snapshot!;
        var targetSnapshot = targetPanel.Snapshot!;
        string sourceRootId = sourcePanel.SelectedInventoryRoot!.Root.Id;
        string targetRootId = targetPanel.SelectedInventoryRoot!.Root.Id;
        long selectionVersion = Interlocked.Read(ref _databasePlanSelectionVersion);
        var request = new DatabasePlanRequest(
            sourceSnapshot.DatabasePath,
            sourceRootId,
            targetSnapshot.DatabasePath,
            targetRootId,
            PlanId.Trim(),
            direction
        );

        IsBusy = true;
        OnPropertyChanged(nameof(CanCreateDatabasePlan));
        StatusMessage =
            $"Planning complete selected roots ({(direction == DatabasePlanDirection.LeftToRight ? "Left → Right" : "Right → Left")})...";
        try
        {
            var artifact = await Task.Run(() => DatabasePlanArtifact.Build(request));
            if (selectionVersion != Interlocked.Read(ref _databasePlanSelectionVersion))
            {
                StatusMessage =
                    "Plan discarded because an inventory source or root changed while planning.";
                return null;
            }

            var review = new DatabasePlanReviewViewModel(artifact, direction);
            StatusMessage = $"Plan {review.PlanId} is ready for review. No files were changed.";
            return review;
        }
        catch (Exception ex)
        {
            StatusMessage = "Database plan failed: " + ex.Message;
            return null;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanCreateDatabasePlan));
        }
    }
}
