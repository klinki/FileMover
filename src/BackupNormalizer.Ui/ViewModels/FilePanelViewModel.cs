using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BackupNormalizer.Ui.ViewModels;

public sealed partial class FileEntryItem : ObservableObject
{
    public string Name { get; }
    public string FullPath { get; }
    public bool IsDirectory { get; }
    public bool IsParentEntry { get; }
    public long Size { get; }
    public DateTime Modified { get; }
    public string BaseName => IsDirectory ? Name : Path.GetFileNameWithoutExtension(Name);
    public string Extension => IsDirectory ? "" : Path.GetExtension(Name).TrimStart('.');
    public string SizeText => IsParentEntry ? "" : IsDirectory ? "<DIR>" : Size.ToString("N0");
    public string ModifiedText => IsParentEntry ? "" : Modified.ToString("yyyy-MM-dd HH:mm");
    public string KindText => IsDirectory ? "dir" : "file";

    private static readonly HashSet<string> ArchiveExts = new(StringComparer.OrdinalIgnoreCase)
        { ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".cab", ".iso", ".jar", ".war" };
    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".svg", ".ico", ".heic", ".heif", ".dng", ".cr2", ".nef" };
    private static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg", ".ts", ".mts", ".3gp" };
    private static readonly HashSet<string> TextExts = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".md", ".markdown", ".log", ".csv", ".tsv", ".json", ".xml", ".yml", ".yaml", ".ini", ".cfg", ".toml",
          ".cs", ".csproj", ".sln", ".xaml", ".axaml", ".py", ".js", ".ts", ".html", ".css", ".sh", ".ps1", ".sql",
          ".java", ".c", ".h", ".cpp", ".go", ".rs" };

    private string DottedExt => IsDirectory ? "" : Path.GetExtension(Name);
    public bool IsFolder => IsDirectory && !IsParentEntry;
    public bool IsArchive => !IsDirectory && ArchiveExts.Contains(DottedExt);
    public bool IsImage => !IsDirectory && ImageExts.Contains(DottedExt);
    public bool IsVideo => !IsDirectory && VideoExts.Contains(DottedExt);
    public bool IsText => !IsDirectory && TextExts.Contains(DottedExt);
    public bool IsGenericFile => !IsDirectory && !IsArchive && !IsImage && !IsVideo && !IsText;

    /// <summary>TC-style mark (persistent, shown red). Independent from the grid cursor (SelectedEntry).</summary>
    [ObservableProperty]
    public partial bool IsMarked { get; set; }

    public FileEntryItem(string name, string fullPath, bool isDir, long size, DateTime modified, bool isParent = false)
    {
        Name = name;
        FullPath = fullPath;
        IsDirectory = isDir;
        Size = size;
        Modified = modified;
        IsParentEntry = isParent;
    }
}

public sealed partial class FilePanelViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string CurrentPath { get; set; } = "";

    public ObservableCollection<FileEntryItem> Entries { get; } = new();

    [ObservableProperty]
    public partial FileEntryItem? SelectedEntry { get; set; }

    public FileEntryItem? MarkAnchor { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    public string SortColumn { get; private set; } = "Name";
    public bool SortAscending { get; private set; } = true;

    /// <summary>Parent directory, or null at a filesystem root ("/", "C:\").</summary>
    public static DirectoryInfo? ParentOf(string path)
    {
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrEmpty(trimmed))
            return null; // Unix root "/" trims to ""
        // Windows drive root ("C:\") trims to "C:", which has no parent.
        if (trimmed.Length == 2 && trimmed[1] == ':' && OperatingSystem.IsWindows())
            return null;
        return Directory.GetParent(trimmed);
    }

    public void Refresh()
    {
        Entries.Clear();
        SelectedEntry = null;
        try
        {
            if (!Directory.Exists(CurrentPath))
            {
                Status = "path not found";
                return;
            }
            // ".." up-row: always first, hidden at filesystem root.
            if (ParentOf(CurrentPath) is { } parent)
                Entries.Add(new FileEntryItem("..", parent.FullName, true, 0, DateTime.MinValue, isParent: true));
            int dirs = 0, files = 0;
            foreach (var d in Directory.GetDirectories(CurrentPath).OrderBy(x => x))
            {
                try
                {
                    var di = new DirectoryInfo(d);
                    if (di.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue; // like scanner: no follow
                    Entries.Add(new FileEntryItem(di.Name, di.FullName, true, 0, di.LastWriteTime));
                    dirs++;
                }
                catch { }
            }
            foreach (var f in Directory.GetFiles(CurrentPath).OrderBy(x => x))
            {
                try
                {
                    var fi = new FileInfo(f);
                    if (fi.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    Entries.Add(new FileEntryItem(fi.Name, fi.FullName, false, fi.Length, fi.LastWriteTime));
                    files++;
                }
                catch { }
            }
            SortEntries();
            Status = $"{dirs} dirs, {files} files";
        }
        catch (Exception ex)
        {
            Status = "error: " + ex.Message;
        }
    }

    /// <summary>Sorts entries keeping ".." pinned first and directories before files (TC default).</summary>
    public void ApplySort(string column)
    {
        if (string.Equals(SortColumn, column, StringComparison.OrdinalIgnoreCase))
            SortAscending = !SortAscending;
        else
        {
            SortColumn = column;
            SortAscending = true;
        }
        SortEntries();
    }

    private void SortEntries()
    {
        // Marks survive sorting because the same FileEntryItem instances are reused (only reordered).
        var pin = Entries.FirstOrDefault(e => e.IsParentEntry);
        var rest = Entries.Where(e => !e.IsParentEntry).ToList();
        Func<FileEntryItem, string> strKey = SortColumn switch
        {
            "Ext" => e => e.Extension,
            _ => e => e.BaseName,
        };
        IOrderedEnumerable<FileEntryItem> ordered = SortColumn switch
        {
            "Size" => SortAscending
                ? rest.OrderBy(e => e.IsDirectory ? 0 : 1).ThenBy(e => e.Size).ThenBy(e => e.BaseName, StringComparer.OrdinalIgnoreCase)
                : rest.OrderBy(e => e.IsDirectory ? 0 : 1).ThenByDescending(e => e.Size).ThenBy(e => e.BaseName, StringComparer.OrdinalIgnoreCase),
            "Modified" => SortAscending
                ? rest.OrderBy(e => e.IsDirectory ? 0 : 1).ThenBy(e => e.Modified).ThenBy(e => e.BaseName, StringComparer.OrdinalIgnoreCase)
                : rest.OrderBy(e => e.IsDirectory ? 0 : 1).ThenByDescending(e => e.Modified).ThenBy(e => e.BaseName, StringComparer.OrdinalIgnoreCase),
            _ => SortAscending
                ? rest.OrderBy(e => e.IsDirectory ? 0 : 1).ThenBy(strKey, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Extension, StringComparer.OrdinalIgnoreCase)
                : rest.OrderBy(e => e.IsDirectory ? 0 : 1).ThenByDescending(strKey, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Extension, StringComparer.OrdinalIgnoreCase),
        };
        var sorted = ordered.ToList();
        Entries.Clear();
        if (pin != null) Entries.Add(pin);
        foreach (var e in sorted) Entries.Add(e);
    }

    public bool NavigateTo(FileEntryItem entry)
    {
        if (entry.IsParentEntry) { GoUp(); return true; }
        if (!entry.IsDirectory) return false;
        CurrentPath = entry.FullPath;
        Refresh();
        return true;
    }

    public void GoUp()
    {
        if (ParentOf(CurrentPath) is { } parent)
        {
            CurrentPath = parent.FullName;
            Refresh();
        }
    }

    public void ToggleMark(FileEntryItem item)
    {
        if (item.IsParentEntry) return;
        item.IsMarked = !item.IsMarked;
    }

    /// <summary>
    /// TC rubber-band start: the mode is latched from the starting row.
    /// Unmarked row -> select mode (gesture only marks); marked row -> deselect mode.
    /// Returns the latched mode. Caller must skip parent entries.
    /// </summary>
    public bool BeginRubber(FileEntryItem item)
    {
        SelectedEntry = item;
        MarkAnchor = item;
        bool select = !item.IsMarked;
        item.IsMarked = select;
        return select;
    }

    /// <summary>Applies the latched rubber-band mode to an encountered row.</summary>
    public void RubberTo(FileEntryItem item, bool select)
    {
        if (item.IsParentEntry) return;
        item.IsMarked = select;
    }

    public void RightClick(FileEntryItem item)
    {
        SelectedEntry = item;
        ToggleMark(item);
        MarkAnchor = item;
    }

    public void CtrlClick(FileEntryItem item)
    {
        SelectedEntry = item;
        ToggleMark(item);
        MarkAnchor = item;
    }

    public void ShiftClick(FileEntryItem item)
    {
        int itemIdx = Entries.IndexOf(item);
        if (itemIdx < 0)
        {
            SelectedEntry = item;
            return;
        }
        FileEntryItem? anchor = MarkAnchor ?? SelectedEntry ?? Entries.FirstOrDefault(e => !e.IsParentEntry);
        int anchorIdx = anchor != null ? Entries.IndexOf(anchor) : -1;
        if (anchorIdx < 0) anchorIdx = itemIdx;
        int lo = Math.Min(anchorIdx, itemIdx);
        int hi = Math.Max(anchorIdx, itemIdx);
        for (int i = lo; i <= hi; i++)
        {
            if (!Entries[i].IsParentEntry)
                Entries[i].IsMarked = true;
        }
        SelectedEntry = item;
    }

    public void SpaceOnCursor()
    {
        if (SelectedEntry != null)
        {
            ToggleMark(SelectedEntry);
            MarkAnchor = SelectedEntry;
        }
    }

    public void ShiftArrow(int delta)
    {
        if (Entries.Count == 0) return;
        var previous = SelectedEntry;
        int curIdx = previous != null ? Entries.IndexOf(previous) : (delta > 0 ? -1 : Entries.Count);
        if (curIdx < 0 || curIdx >= Entries.Count)
            curIdx = delta > 0 ? -1 : Entries.Count;
        // Clamp relative to the position before moving so a large delta lands on the edge.
        int newIdx = Math.Clamp(curIdx + delta, 0, Entries.Count - 1);
        if (MarkAnchor == null)
            MarkAnchor = previous;
        SelectedEntry = Entries[newIdx];
        if (!SelectedEntry.IsParentEntry)
            ToggleMark(SelectedEntry);
    }

    public IReadOnlyList<FileEntryItem> StagingSet()
    {
        var marked = Entries.Where(e => e.IsMarked && !e.IsParentEntry).ToList();
        if (marked.Count > 0) return marked;
        if (SelectedEntry != null && !SelectedEntry.IsParentEntry)
            return new List<FileEntryItem> { SelectedEntry };
        return Array.Empty<FileEntryItem>();
    }

    public void ClearMarks()
    {
        foreach (var e in Entries)
            e.IsMarked = false;
        MarkAnchor = null;
    }

    /// <summary>Ctrl/Cmd+A: mark everything except "..".</summary>
    public void MarkAll()
    {
        foreach (var e in Entries)
            if (!e.IsParentEntry)
                e.IsMarked = true;
    }
}
