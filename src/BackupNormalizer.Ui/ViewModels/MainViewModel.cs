using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BackupNormalizer.Ui.ViewModels;

public sealed partial class StagedOpItem : ObservableObject
{
    public string Type { get; }
    public string Source { get; }
    public string Dest { get; }
    public long Size { get; }
    public string SizeText => Size == 0 ? "" : Size.ToString("N0");
    public string HashShort { get; }

    public StagedOpItem(string type, string source, string dest, long size, string? hash)
    {
        Type = type;
        Source = source;
        Dest = dest;
        Size = size;
        HashShort = hash is null ? "" : hash.Length > 12 ? hash[..12] + "…" : hash;
    }
}

/// <summary>
/// Total Commander style planner: two drive-local panels, plan-only staging.
/// Nothing is executed here — Generate writes an executor-compatible plan JSON/DB.
/// </summary>
public sealed partial class MainViewModel : ViewModelBase
{
    [ObservableProperty]
    public partial string RootId { get; set; } = "disk";

    [ObservableProperty]
    public partial string BasePath { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [ObservableProperty]
    public partial string PlanId { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-dd-001");

    [ObservableProperty]
    public partial string DbPath { get; set; } = "./ui-plan.db";

    [ObservableProperty]
    public partial string JsonPath { get; set; } = "./ui-plan.json";

    [ObservableProperty]
    public partial string NewFolderName { get; set; } = "";

    [ObservableProperty]
    public partial bool IsLeftActive { get; set; } = true;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "Plan-only mode: nothing is executed from this UI.";

    [ObservableProperty]
    public partial string PlanSummary { get; set; } = "No staged operations.";

    public FilePanelViewModel Left { get; } = new();
    public FilePanelViewModel Right { get; } = new();
    public ObservableCollection<StagedOpItem> Staged { get; } = new();

    private readonly List<BackupNormalizer.PlanStaging.StagedOp> _stagedCore = new();

    public MainViewModel()
    {
        Left.CurrentPath = BasePath;
        Right.CurrentPath = BasePath;
        Left.Refresh();
        Right.Refresh();
    }

    public FilePanelViewModel Active => IsLeftActive ? Left : Right;
    public FilePanelViewModel Inactive => IsLeftActive ? Right : Left;

    [RelayCommand]
    public void ApplyBase()
    {
        try
        {
            string full = Path.GetFullPath(BasePath);
            if (!Directory.Exists(full))
            {
                StatusMessage = "Base path not found: " + full;
                return;
            }
            BasePath = full;
            Left.CurrentPath = full;
            Right.CurrentPath = full;
            Left.Refresh();
            Right.Refresh();
            StatusMessage = "Base set to " + full + " (drive-local staging enforced).";
        }
        catch (Exception ex)
        {
            StatusMessage = "error: " + ex.Message;
        }
    }

    [RelayCommand]
    public void RefreshAll()
    {
        Left.Refresh();
        Right.Refresh();
        StatusMessage = "Panels refreshed.";
    }

    [RelayCommand]
    public void SetActive(string side)
    {
        IsLeftActive = !string.Equals(side, "Right", StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    public void GoUpActive() => Active.GoUp();

    [RelayCommand]
    public void GoUpLeft() => Left.GoUp();

    [RelayCommand]
    public void GoUpRight() => Right.GoUp();

    [RelayCommand]
    public void EnterSelected()
    {
        var sel = Active.SelectedEntry;
        if (sel != null && sel.IsDirectory)
            Active.NavigateTo(sel);
    }

    private void AppendStaged(IEnumerable<BackupNormalizer.PlanStaging.StagedOp> ops)
    {
        int added = 0;
        foreach (var op in ops)
        {
            string src = op.Type == "MKDIR" ? "" : op.SourceRel;
            string dst = op.Type == "TRASH" ? "" : (op.DestRel ?? op.SourceRel);
            if (_stagedCore.Any(s => s.Type == op.Type && s.SourceRel == op.SourceRel && (s.DestRel ?? "") == (op.DestRel ?? "")))
                continue; // dedupe
            _stagedCore.Add(op);
            Staged.Add(new StagedOpItem(op.Type, src, dst, op.ExpectedSize, op.ExpectedHash));
            added++;
        }
        UpdateSummary();
        StatusMessage = added == 0 ? "Already staged (deduped)." : $"Staged {added} operation(s). Total: {_stagedCore.Count}. Nothing executed.";
    }

    private void UpdateSummary()
    {
        int mkdir = _stagedCore.Count(o => o.Type == "MKDIR");
        int move = _stagedCore.Count(o => o.Type == "MOVE");
        int copy = _stagedCore.Count(o => o.Type == "COPY");
        int trash = _stagedCore.Count(o => o.Type == "TRASH");
        long bytes = _stagedCore.Where(o => o.Type == "COPY").Sum(o => o.ExpectedSize);
        PlanSummary = _stagedCore.Count == 0
            ? "No staged operations."
            : $"Staged: MKDIR {mkdir}  MOVE {move}  COPY {copy}  TRASH {trash}  | bytes to copy: {bytes:N0}";
    }

    [RelayCommand]
    public void StageCopy()
    {
        try
        {
            var sel = Active.SelectedEntry;
            if (sel == null) { StatusMessage = "Select a file/folder in the active panel first."; return; }
            var ops = BackupNormalizer.PlanStaging.StageCopy(BasePath, sel.FullPath, Inactive.CurrentPath);
            AppendStaged(ops);
        }
        catch (Exception ex) { StatusMessage = "stage copy failed: " + ex.Message; }
    }

    [RelayCommand]
    public void StageMove()
    {
        try
        {
            var sel = Active.SelectedEntry;
            if (sel == null) { StatusMessage = "Select a file/folder in the active panel first."; return; }
            var ops = BackupNormalizer.PlanStaging.StageMove(BasePath, sel.FullPath, Inactive.CurrentPath);
            AppendStaged(ops);
        }
        catch (Exception ex) { StatusMessage = "stage move failed: " + ex.Message; }
    }

    [RelayCommand]
    public void StageMkdir()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(NewFolderName)) { StatusMessage = "Enter a new folder name first."; return; }
            string abs = Path.Combine(Active.CurrentPath, NewFolderName.Trim());
            var ops = BackupNormalizer.PlanStaging.StageMkdir(BasePath, abs);
            AppendStaged(ops);
            NewFolderName = "";
        }
        catch (Exception ex) { StatusMessage = "stage mkdir failed: " + ex.Message; }
    }

    [RelayCommand]
    public void StageTrash()
    {
        try
        {
            var sel = Active.SelectedEntry;
            if (sel == null) { StatusMessage = "Select a file/folder in the active panel first."; return; }
            var ops = BackupNormalizer.PlanStaging.StageTrash(BasePath, sel.FullPath);
            AppendStaged(ops);
        }
        catch (Exception ex) { StatusMessage = "stage delete failed: " + ex.Message; }
    }

    [RelayCommand]
    public void RemoveStaged(StagedOpItem? item)
    {
        if (item == null) return;
        int idx = Staged.IndexOf(item);
        if (idx >= 0)
        {
            Staged.RemoveAt(idx);
            _stagedCore.RemoveAt(idx);
            UpdateSummary();
            StatusMessage = "Removed staged operation.";
        }
    }

    [RelayCommand]
    public void ClearStaged()
    {
        Staged.Clear();
        _stagedCore.Clear();
        UpdateSummary();
        StatusMessage = "Staged operations cleared.";
    }

    [RelayCommand]
    public void SaveJson()
    {
        try
        {
            if (_stagedCore.Count == 0) { StatusMessage = "Nothing to save: stage operations first (F5/F6/F7/F8)."; return; }
            if (string.IsNullOrWhiteSpace(PlanId)) { StatusMessage = "Plan ID is required."; return; }
            var doc = BackupNormalizer.PlanStaging.BuildPlanDoc(PlanId.Trim(), RootId.Trim(), _stagedCore);
            File.WriteAllText(JsonPath, BackupNormalizer.PlanStaging.ToJson(doc));
            StatusMessage = $"Saved plan {doc.PlanId} ({doc.Operations.Count} ops) to {JsonPath}. Execute later with: plan import + execute.";
        }
        catch (Exception ex) { StatusMessage = "save failed: " + ex.Message; }
    }

    [RelayCommand]
    public void WriteToDb()
    {
        try
        {
            if (_stagedCore.Count == 0) { StatusMessage = "Nothing to write: stage operations first."; return; }
            var doc = BackupNormalizer.PlanStaging.BuildPlanDoc(PlanId.Trim(), RootId.Trim(), _stagedCore);
            using var db = new BackupNormalizer.Database(DbPath);
            BackupNormalizer.PlanStaging.WriteToDatabase(db, doc, RootId.Trim(), BasePath);
            StatusMessage = $"Wrote plan {doc.PlanId} ({doc.Operations.Count} ops) into {DbPath}. Run: execute {doc.PlanId} --db {DbPath}.";
        }
        catch (Exception ex) { StatusMessage = "write to DB failed: " + ex.Message; }
    }
}
