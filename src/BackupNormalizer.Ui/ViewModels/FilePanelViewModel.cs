using System;
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
    public long Size { get; }
    public DateTime Modified { get; }
    public string SizeText => IsDirectory ? "<DIR>" : Size.ToString("N0");
    public string ModifiedText => Modified.ToString("yyyy-MM-dd HH:mm");
    public string KindText => IsDirectory ? "dir" : "file";

    public FileEntryItem(string name, string fullPath, bool isDir, long size, DateTime modified)
    {
        Name = name;
        FullPath = fullPath;
        IsDirectory = isDir;
        Size = size;
        Modified = modified;
    }
}

public sealed partial class FilePanelViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string CurrentPath { get; set; } = "";

    public ObservableCollection<FileEntryItem> Entries { get; } = new();

    [ObservableProperty]
    public partial FileEntryItem? SelectedEntry { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "";

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
            Status = $"{dirs} dirs, {files} files";
        }
        catch (Exception ex)
        {
            Status = "error: " + ex.Message;
        }
    }

    public bool NavigateTo(FileEntryItem entry)
    {
        if (!entry.IsDirectory) return false;
        CurrentPath = entry.FullPath;
        Refresh();
        return true;
    }

    public void GoUp()
    {
        var parent = Directory.GetParent(CurrentPath.TrimEnd(Path.DirectorySeparatorChar));
        if (parent != null)
        {
            CurrentPath = parent.FullName;
            Refresh();
        }
    }
}
