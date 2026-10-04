using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;

namespace BackupNormalizer.Ui.ViewModels;

public sealed partial class FilePanelViewModel
{
    private readonly HashSet<FileEntryItem> _footerEntries = new();
    private int _files,
        _folders,
        _links;
    private int _selectedFiles,
        _selectedFolders,
        _selectedLinks;
    private long _bytes,
        _selectedBytes;

    public FilePanelViewModel() => Entries.CollectionChanged += OnFooterEntriesChanged;

    public string FooterSummary
    {
        get
        {
            if (
                Status.StartsWith("error:", StringComparison.OrdinalIgnoreCase)
                || Status is "path not found" or "Folder not indexed on this side."
            )
            {
                return Status;
            }

            return $"Selected: {DriveView.FormatBytes(_selectedBytes)} / {DriveView.FormatBytes(_bytes)} | Files: {_selectedFiles:N0} / {_files:N0} | Dirs: {_selectedFolders:N0} / {_folders:N0}"
                + (_links > 0 ? $" | Links: {_selectedLinks:N0} / {_links:N0}" : "");
        }
    }

    public string FooterTooltip =>
        FooterSummary
        + Environment.NewLine
        + Status
        + (DriveStatus.Length > 0 ? Environment.NewLine + DriveStatus : "");

    private void OnFooterEntriesChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (args.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var entry in _footerEntries)
            {
                entry.PropertyChanged -= OnFooterEntryChanged;
            }
            _footerEntries.Clear();
            _files = _folders = _links = 0;
            _selectedFiles = _selectedFolders = _selectedLinks = 0;
            _bytes = _selectedBytes = 0;
        }
        if (args.OldItems != null)
        {
            foreach (FileEntryItem entry in args.OldItems)
            {
                if (_footerEntries.Remove(entry))
                {
                    entry.PropertyChanged -= OnFooterEntryChanged;
                    AdjustFooterTotals(entry, -1);
                }
            }
        }
        if (args.NewItems != null)
        {
            foreach (FileEntryItem entry in args.NewItems)
            {
                if (_footerEntries.Add(entry))
                {
                    entry.PropertyChanged += OnFooterEntryChanged;
                    AdjustFooterTotals(entry, 1);
                }
            }
        }
        NotifyFooterChanged();
    }

    private void OnFooterEntryChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(FileEntryItem.IsMarked) && sender is FileEntryItem entry)
        {
            AdjustSelectedTotals(entry, entry.IsMarked ? 1 : -1);
            NotifyFooterChanged();
        }
    }

    private void AdjustFooterTotals(FileEntryItem entry, int direction)
    {
        if (entry.IsParentEntry)
        {
            return;
        }
        if (entry.IsLink)
        {
            _links += direction;
        }
        else if (entry.IsDirectory)
        {
            _folders += direction;
        }
        else
        {
            _files += direction;
            _bytes += direction * entry.Size;
        }
        if (entry.IsMarked)
        {
            AdjustSelectedTotals(entry, direction);
        }
    }

    private void AdjustSelectedTotals(FileEntryItem entry, int direction)
    {
        if (entry.IsParentEntry)
        {
            return;
        }
        if (entry.IsLink)
        {
            _selectedLinks += direction;
        }
        else if (entry.IsDirectory)
        {
            _selectedFolders += direction;
        }
        else
        {
            _selectedFiles += direction;
            _selectedBytes += direction * entry.Size;
        }
    }

    private void NotifyFooterChanged()
    {
        OnPropertyChanged(nameof(FooterSummary));
        OnPropertyChanged(nameof(FooterTooltip));
    }
}
