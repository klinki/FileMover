using System;
using System.Linq;
using System.Threading.Tasks;
using BackupNormalizer.Ui.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BackupNormalizer.Ui.ViewModels;

public sealed partial class MainViewModel
{
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool IsComparisonEnabled { get; set; }

    [ObservableProperty]
    public partial bool DifferencesOnly { get; set; }

    [ObservableProperty]
    public partial bool LinkedBrowsing { get; set; } = true;

    [ObservableProperty]
    public partial string ComparisonSummary { get; set; } = "";

    public bool CanStage => Left.IsLive && Right.IsLive && !IsBusy;
    public bool CanChangePanelSource => _stagedCore.Count == 0 && !IsBusy;
    public bool CanCompare => Left.IsDatabase && Right.IsDatabase && !IsBusy;
    public bool HasDatabasePanels => Left.IsDatabase || Right.IsDatabase;
    private InventoryComparison? _comparison;

    partial void OnIsBusyChanged(bool value) => UpdateSourceCommands();

    partial void OnIsLeftActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(Active));
        OnPropertyChanged(nameof(Inactive));
        UpdateSourceCommands();
    }

    partial void OnDifferencesOnlyChanged(bool value) => ApplyComparisonToPanels();

    private void UpdateSourceCommands()
    {
        OnPropertyChanged(nameof(CanStage));
        OnPropertyChanged(nameof(CanStageCopy));
        OnPropertyChanged(nameof(CanDragStage));
        OnPropertyChanged(nameof(CanReviewStaged));
        OnPropertyChanged(nameof(CanCompare));
        OnPropertyChanged(nameof(CanChangeBase));
        OnPropertyChanged(nameof(CanChangePanelSource));
        OnPropertyChanged(nameof(HasDatabasePanels));
        OnPropertyChanged(nameof(CanCreateDatabasePlan));
        OnPropertyChanged(nameof(CanExportInventory));
        Left.CanChangeSource = Right.CanChangeSource = CanChangePanelSource;
        ApplyBaseCommand.NotifyCanExecuteChanged();
        StageCopyCommand.NotifyCanExecuteChanged();
        StageMoveCommand.NotifyCanExecuteChanged();
        StageTrashCommand.NotifyCanExecuteChanged();
        CompareFoldersCommand.NotifyCanExecuteChanged();
        UseLivePanelCommand.NotifyCanExecuteChanged();
        NotifyInventoryJobCommands();
    }

    private void OnPanelSourceChanged()
    {
        InvalidateDatabasePlanReview();
        Left.NotifyPortabilityLabelsChanged();
        Right.NotifyPortabilityLabelsChanged();
        ClearComparison();
        UpdateSourceCommands();
    }

    private bool EnsureLiveStaging()
    {
        if (CanStage)
        {
            return true;
        }

        StatusMessage =
            "Database snapshots are read-only. Switch both panels to live folders to stage operations.";
        return false;
    }

    public async Task LoadDatabaseAsync(string side, string path)
    {
        if (!CanChangePanelSource)
        {
            StatusMessage = "Clear staged operations before changing panel sources.";
            return;
        }
        var panel = side == "Right" ? Right : Left;
        IsBusy = true;
        StatusMessage = "Loading database...";
        try
        {
            var snapshot = await Task.Run(() => InventorySnapshot.Load(path));
            panel.LoadSnapshot(snapshot);
            StatusMessage =
                $"Loaded {snapshot.DatabasePath} into {side.ToLowerInvariant()} panel. {snapshot.Roots.Count} root(s).";
        }
        catch (Exception ex)
        {
            StatusMessage = "Load database failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanChangePanelSource))]
    public void UseLivePanel(string side)
    {
        if (!CanChangePanelSource)
        {
            return;
        }

        (side == "Right" ? Right : Left).UseFileSystem(AppliedBasePath);
        StatusMessage = "Panel switched to live filesystem.";
    }

    private async Task ReloadSnapshots(bool activeOnly)
    {
        var panels = activeOnly ? new[] { Active } : new[] { Left, Right };
        IsBusy = true;
        StatusMessage = "Refreshing inventories...";
        try
        {
            // Load every replacement before changing the displayed snapshots.
            var replacements = await Task.Run(() =>
                panels
                    .Select(panel =>
                        panel.Snapshot == null
                            ? null
                            : InventorySnapshot.Load(panel.Snapshot.DatabasePath)
                    )
                    .ToArray()
            );
            ClearComparison();
            for (int i = 0; i < panels.Length; i++)
            {
                var panel = panels[i];
                if (replacements[i] is { } replacement)
                {
                    panel.LoadSnapshot(
                        replacement,
                        panel.SelectedInventoryRoot?.Root.Id,
                        panel.InventoryPath
                    );
                }
                else
                {
                    panel.Refresh();
                }
            }
            StatusMessage = "Panels refreshed. Compare again to update differences.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Refresh failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCompare))]
    public async Task CompareFolders()
    {
        if (!CanCompare)
        {
            return;
        }

        var leftRoot = Left.SelectedInventoryRoot!;
        var rightRoot = Right.SelectedInventoryRoot!;
        string leftPath = Left.InventoryPath,
            rightPath = Right.InventoryPath;
        IsBusy = true;
        StatusMessage = "Comparing folders...";
        try
        {
            var comparison = await Task.Run(() =>
                InventoryComparison.Compare(leftRoot, leftPath, rightRoot, rightPath)
            );
            _comparison = comparison;
            IsComparisonEnabled = true;
            ComparisonSummary = comparison.Summary;
            ApplyComparisonToPanels();
            StatusMessage = "Folder comparison ready.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Compare failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public void ClearComparison()
    {
        _comparison = null;
        IsComparisonEnabled = false;
        ComparisonSummary = "";
        ApplyComparisonToPanels();
    }

    private void ApplyComparisonToPanels()
    {
        Left.ApplyComparison(_comparison?.Left, IsComparisonEnabled && DifferencesOnly);
        Right.ApplyComparison(_comparison?.Right, IsComparisonEnabled && DifferencesOnly);
    }

    private void GoUpPanel(FilePanelViewModel panel)
    {
        if (IsBusy)
        {
            return;
        }

        if (
            _comparison != null
            && panel.InventoryPath == (panel == Left ? _comparison.LeftBase : _comparison.RightBase)
        )
        {
            ClearComparison();
        }

        panel.GoUp();
        FollowInventoryNavigation(panel);
    }

    private void FollowInventoryNavigation(FilePanelViewModel panel)
    {
        if (!LinkedBrowsing || _comparison == null || !panel.IsDatabase)
        {
            return;
        }

        string sourceBase = panel == Left ? _comparison.LeftBase : _comparison.RightBase;
        string targetBase = panel == Left ? _comparison.RightBase : _comparison.LeftBase;
        var other = panel == Left ? Right : Left;
        var comparer = panel.SelectedInventoryRoot!.Comparer;
        string path = panel.InventoryPath;
        string suffix;
        if (comparer.Equals(path, sourceBase))
        {
            suffix = "";
        }
        else if (sourceBase.Length == 0)
        {
            suffix = path;
        }
        else if (
            path.Length > sourceBase.Length
            && path[sourceBase.Length] == '/'
            && comparer.Equals(path[..sourceBase.Length], sourceBase)
        )
        {
            suffix = path[(sourceBase.Length + 1)..];
        }
        else
        {
            ClearComparison();
            return;
        }
        other.NavigateInventory(
            targetBase.Length == 0 ? suffix
            : suffix.Length == 0 ? targetBase
            : targetBase + "/" + suffix
        );
    }

    private void SwapSources()
    {
        var leftSnapshot = Left.Snapshot;
        var rightSnapshot = Right.Snapshot;
        string? leftRoot = Left.SelectedInventoryRoot?.Root.Id,
            rightRoot = Right.SelectedInventoryRoot?.Root.Id;
        string leftPath = Left.IsDatabase ? Left.InventoryPath : Left.CurrentPath;
        string rightPath = Right.IsDatabase ? Right.InventoryPath : Right.CurrentPath;
        ClearComparison();
        if (rightSnapshot != null)
        {
            Left.LoadSnapshot(rightSnapshot, rightRoot, rightPath);
        }
        else
        {
            Left.UseFileSystem(rightPath);
        }

        if (leftSnapshot != null)
        {
            Right.LoadSnapshot(leftSnapshot, leftRoot, leftPath);
        }
        else
        {
            Right.UseFileSystem(leftPath);
        }

        StatusMessage = "Panel sources swapped. Compare again to update differences.";
    }
}
