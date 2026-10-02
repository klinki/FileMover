using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using BackupNormalizer.Ui.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BackupNormalizer.Ui.ViewModels;

public sealed partial class FilePanelViewModel
{
    public string Side { get; init; } = "Left";
    public event Action? SourceChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDatabase), nameof(IsLive), nameof(SourceLabel), nameof(ShowDriveButtons), nameof(ShowDriveCombo))]
    public partial InventorySnapshot? Snapshot { get; set; }

    [ObservableProperty]
    public partial InventoryRoot? SelectedInventoryRoot { get; set; }

    [ObservableProperty]
    public partial bool CanChangeSource { get; set; } = true;

    public ObservableCollection<InventoryRoot> InventoryRoots { get; } = new();
    public string InventoryPath { get; private set; } = "";
    public bool IsDatabase => Snapshot != null;
    public bool IsLive => !IsDatabase;
    public bool ShowDriveButtons => IsLive && OperatingSystem.IsWindows();
    public bool ShowDriveCombo => IsLive && !OperatingSystem.IsWindows();
    public string SourceLabel => Snapshot?.DatabasePath ?? "Live filesystem";

    private bool _changingSource;
    private IReadOnlyDictionary<string, ComparisonState>? _comparisonStates;
    private bool _differencesOnly;

    partial void OnSelectedInventoryRootChanged(InventoryRoot? value)
    {
        if (_changingSource || value == null) return;
        _comparisonStates = null;
        _differencesOnly = false;
        NavigateInventory("");
        SourceChanged?.Invoke();
    }

    public void LoadSnapshot(InventorySnapshot snapshot, string? rootId = null, string relativePath = "")
    {
        _changingSource = true;
        try
        {
            Snapshot = snapshot;
            _comparisonStates = null;
            _differencesOnly = false;
            InventoryRoots.Clear();
            foreach (var root in snapshot.Roots) InventoryRoots.Add(root);
            SelectedInventoryRoot = InventoryRoots.FirstOrDefault(r => r.Root.Id == rootId) ?? InventoryRoots[0];
            NavigateInventory(relativePath);
        }
        finally { _changingSource = false; }
        SourceChanged?.Invoke();
    }

    public void UseFileSystem(string path)
    {
        _changingSource = true;
        try
        {
            Snapshot = null;
            SelectedInventoryRoot = null;
            InventoryRoots.Clear();
            _comparisonStates = null;
            _differencesOnly = false;
            InventoryPath = "";
            CurrentPath = path;
            RefreshDrives();
            Refresh();
        }
        finally { _changingSource = false; }
        SourceChanged?.Invoke();
    }

    public static string InventoryParent(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }

    public void NavigateInventory(string path)
    {
        if (SelectedInventoryRoot?.Nodes.TryGetValue(path, out var node) == true && node.IsLink)
        {
            Status = "Linked folders are excluded from browsing.";
            return;
        }
        InventoryPath = path;
        string root = SelectedInventoryRoot?.Root.Path ?? "";
        char separator = root.Contains('\\') || (root.Length > 1 && root[1] == ':') ? '\\' : '/';
        CurrentPath = path.Length == 0 ? root : root.TrimEnd('/', '\\') + separator + path.Replace('/', separator);
        RefreshInventory();
    }

    public void ApplyComparison(IReadOnlyDictionary<string, ComparisonState>? states, bool differencesOnly)
    {
        _comparisonStates = states;
        _differencesOnly = differencesOnly;
        if (IsDatabase) RefreshInventory();
    }

    private void RefreshInventory()
    {
        Entries.Clear();
        SelectedEntry = null;
        MarkAnchor = null;
        if (SelectedInventoryRoot is not { } root) return;
        if (InventoryPath.Length > 0)
            Entries.Add(new FileEntryItem("..", InventoryParent(InventoryPath), true, 0, DateTime.MinValue, isParent: true));
        if (!root.Nodes.TryGetValue(InventoryPath, out var directory) || !directory.IsDirectory)
        {
            Status = "Folder not indexed on this side.";
            return;
        }
        int folders = 0, files = 0, links = 0;
        foreach (var node in directory.Children.Values)
        {
            var state = _comparisonStates != null && _comparisonStates.TryGetValue(node.RelativePath, out var found)
                ? found : ComparisonState.None;
            if (_differencesOnly && state is ComparisonState.Equal or ComparisonState.Skipped) continue;
            Entries.Add(new FileEntryItem(node.Name, node.RelativePath, node.IsDirectory, node.Size, node.Modified)
                { Comparison = state, EntryKind = node.EntryKind, LinkTarget = node.LinkTarget,
                    TargetPath = node.TargetPath, LinkNote = node.LinkNote });
            if (node.IsLink) links++; else if (node.IsDirectory) folders++; else files++;
        }
        SortEntries();
        Status = $"{folders} dirs, {files} files, {links} links | Scan: {root.ScanStatus ?? "not scanned"}";
        DriveStatus = "";
    }
}
