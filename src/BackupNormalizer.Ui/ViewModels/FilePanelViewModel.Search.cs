using System;
using System.Collections.Generic;
using BackupNormalizer.Ui.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BackupNormalizer.Ui.ViewModels;

public sealed partial class FilePanelViewModel
{
    public IReadOnlyList<string> InventoryFilterOptions { get; } =
        new[] { "All", "Unverified", "Conflicts", "Scan errors", "Links" };

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial string SelectedInventoryFilter { get; set; } = "All";

    [ObservableProperty]
    public partial string SelectedDetailsName { get; set; } = "";

    [ObservableProperty]
    public partial string SelectedDetailsPath { get; set; } = "";

    [ObservableProperty]
    public partial string SelectedDetailsSize { get; set; } = "";

    [ObservableProperty]
    public partial string SelectedDetailsCreated { get; set; } = "";

    [ObservableProperty]
    public partial string SelectedDetailsModified { get; set; } = "";

    [ObservableProperty]
    public partial string SelectedDetailsHashAvailability { get; set; } = "";

    [ObservableProperty]
    public partial string SelectedDetailsHash { get; set; } = "";

    [ObservableProperty]
    public partial string SelectedDetailsComparison { get; set; } = "";

    [ObservableProperty]
    public partial string SelectedDetailsKind { get; set; } = "";

    [ObservableProperty]
    public partial string SelectedDetailsLinkTarget { get; set; } = "";

    [ObservableProperty]
    public partial string SelectedDetailsAbsoluteTarget { get; set; } = "";

    [ObservableProperty]
    public partial string SelectedDetailsLinkNote { get; set; } = "";

    public bool HasSelectedInventoryEntry => IsDatabase && SelectedEntry != null && !SelectedEntry.IsParentEntry;
    public bool SelectedDetailsIsLink => HasSelectedInventoryEntry && SelectedEntry!.IsLink;

    partial void OnSearchTextChanged(string value)
    {
        if (IsDatabase) RefreshInventory();
    }

    partial void OnSelectedInventoryFilterChanged(string value)
    {
        if (IsDatabase) RefreshInventory();
    }

    partial void OnSelectedEntryChanged(FileEntryItem? value)
    {
        OnPropertyChanged(nameof(HasSelectedInventoryEntry));
        OnPropertyChanged(nameof(SelectedDetailsIsLink));
        if (value == null || value.IsParentEntry || !IsDatabase || SelectedInventoryRoot is not { } root ||
            !root.Nodes.TryGetValue(value.FullPath, out var node))
        {
            ClearSelectedDetails();
            return;
        }

        SelectedDetailsName = value.Name;
        SelectedDetailsPath = RecordedPath(root.Root.Path, node.RelativePath);
        SelectedDetailsSize = node.IsDirectory ? "Folder" : $"{node.Size:N0} bytes ({DriveView.FormatBytes(node.Size)})";
        SelectedDetailsCreated = node.Created is { } created
            ? created.ToString("yyyy-MM-dd HH:mm:ss") : "Unavailable";
        SelectedDetailsModified = node.Modified == DateTime.MinValue
            ? "Unavailable" : node.Modified.ToString("yyyy-MM-dd HH:mm:ss");
        SelectedDetailsHashAvailability = node.IsDirectory || node.IsLink
            ? "Not applicable" : node.Digest == null ? "Unavailable" : "Available (SHA-256)";
        SelectedDetailsHash = node.IsDirectory || node.IsLink ? "Not applicable" : node.Digest ?? "Unavailable";
        SelectedDetailsComparison = string.IsNullOrEmpty(value.ComparisonText)
            ? "Not compared" : value.ComparisonText;
        SelectedDetailsKind = value.KindText;
        SelectedDetailsLinkTarget = node.IsLink ? node.LinkTarget ?? "Unavailable" : "Not a link";
        SelectedDetailsAbsoluteTarget = node.IsLink ? node.TargetPath ?? "Unavailable" : "Not a link";
        SelectedDetailsLinkNote = node.IsLink ? node.LinkNote ?? "None recorded" : "Not a link";
    }

    private void ClearSelectedDetails()
    {
        SelectedDetailsName = "";
        SelectedDetailsPath = "";
        SelectedDetailsSize = "";
        SelectedDetailsCreated = "";
        SelectedDetailsModified = "";
        SelectedDetailsHashAvailability = "";
        SelectedDetailsHash = "";
        SelectedDetailsComparison = "";
        SelectedDetailsKind = "";
        SelectedDetailsLinkTarget = "";
        SelectedDetailsAbsoluteTarget = "";
        SelectedDetailsLinkNote = "";
    }

    private static string RecordedPath(string recordedRoot, string relativePath)
    {
        if (relativePath.Length == 0) return recordedRoot;
        char separator = recordedRoot.Contains('\\') || (recordedRoot.Length > 1 && recordedRoot[1] == ':') ? '\\' : '/';
        return recordedRoot.TrimEnd('/', '\\') + separator + relativePath.Replace('/', separator);
    }

    private bool MatchesInventorySearch(InventoryNode node, InventoryRoot root)
    {
        string query = SearchText;
        if (query.Length == 0) return true;
        StringComparison comparison = root.Root.CaseSensitivity == "insensitive"
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return node.Name.IndexOf(query, comparison) >= 0 ||
            node.RelativePath.IndexOf(query, comparison) >= 0 ||
            RecordedPath(root.Root.Path, node.RelativePath).IndexOf(query, comparison) >= 0;
    }

    private bool MatchesInventoryFilter(InventoryNode node)
    {
        ComparisonState state = _comparisonStates != null && _comparisonStates.TryGetValue(node.RelativePath, out var found)
            ? found : ComparisonState.None;
        if (_differencesOnly && state is ComparisonState.Equal or ComparisonState.Skipped) return false;

        return SelectedInventoryFilter switch
        {
            "Unverified" => !node.IsDirectory && !node.IsLink && !node.HasScanError &&
                (node.Digest == null || state == ComparisonState.Unverified),
            "Conflicts" => state == ComparisonState.TypeConflict,
            "Scan errors" => node.HasScanError || state == ComparisonState.ScanError,
            "Links" => node.IsLink,
            _ => true,
        };
    }

    private HashSet<string> FindVisibleInventoryPaths(InventoryNode directory, InventoryRoot root)
    {
        var visible = new HashSet<string>(root.Comparer);

        bool Visit(InventoryNode node)
        {
            bool include = MatchesInventorySearch(node, root) && MatchesInventoryFilter(node);
            foreach (var child in node.Children.Values)
                include |= Visit(child);
            if (include) visible.Add(node.RelativePath);
            return include;
        }

        foreach (var child in directory.Children.Values) Visit(child);
        return visible;
    }
}
