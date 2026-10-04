using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BackupNormalizer.Ui.Models;

namespace BackupNormalizer.Ui.ViewModels;

public sealed partial class MainViewModel
{
    private InventoryCopyStaging? _inventoryStaging;
    public bool CanStageCopy => CanStage || CanCreateDatabasePlan;
    public bool CanDragStage => CanStageCopy;
    public bool CanReviewStaged => !IsBusy && _stagedCore.Count > 0;

    public async Task StageInventoryCopyAsync(
        FilePanelViewModel source,
        FilePanelViewModel target,
        IReadOnlyList<string> paths,
        string destinationDirectory
    )
    {
        if (
            !CanCreateDatabasePlan
            || source == target
            || (source != Left && source != Right)
            || (target != Left && target != Right)
        )
            return;
        var binding = new InventoryCopyStaging(
            source.Snapshot!,
            source.SelectedInventoryRoot!,
            target.Snapshot!,
            target.SelectedInventoryRoot!
        );
        if (_stagedCore.Count > 0 && _inventoryStaging != binding)
        {
            StatusMessage =
                "Clear staged operations before changing the plan's copy direction or roots.";
            return;
        }

        var existing = _stagedCore.ToArray();
        IsBusy = true;
        StatusMessage = "Preparing inventory copies...";
        try
        {
            var operations = await Task.Run(() =>
                binding.Prepare(paths, destinationDirectory, existing)
            );
            if (
                source.Snapshot != binding.SourceSnapshot
                || source.SelectedInventoryRoot != binding.Source
                || target.Snapshot != binding.TargetSnapshot
                || target.SelectedInventoryRoot != binding.Target
            )
            {
                StatusMessage = "Panel sources changed; the drop was discarded.";
                return;
            }
            if (operations.Count == 0)
            {
                StatusMessage = "Nothing staged: the selected paths are excluded or empty.";
                return;
            }
            _inventoryStaging = binding;
            AppendStaged(operations);
            StatusMessage += " Inventory copies are planned for review.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Inventory copy staging failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private PlanDoc BuildStagedDocument() =>
        _inventoryStaging == null
            ? PlanStaging.BuildPlanDoc(PlanId.Trim(), RootId.Trim(), AppliedBasePath, _stagedCore)
            : _inventoryStaging.BuildDocument(PlanId.Trim(), _stagedCore);
}
