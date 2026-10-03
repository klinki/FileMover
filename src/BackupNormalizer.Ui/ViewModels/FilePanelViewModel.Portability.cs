using System;
using System.IO;

namespace BackupNormalizer.Ui.ViewModels;

public sealed partial class FilePanelViewModel
{
    public string PathCaption => IsDatabase ? "Recorded folder:" : "Live folder:";
    public string RecordedRootLabel => SelectedInventoryRoot is { } root ? "Recorded root: " + root.Root.Path : "";

    public string LocalRootLabel
    {
        get
        {
            if (SelectedInventoryRoot is not { } root) return "";
            try
            {
                return root.LocalRootAvailable
                    ? "Local root available: " + Path.GetFullPath(root.Root.Path)
                    : "Local root unavailable. Offline browsing remains available.";
            }
            catch { return "Local root unavailable. Offline browsing remains available."; }
        }
    }

    public static bool IsHostNativeAbsolutePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
        return OperatingSystem.IsWindows() || path.StartsWith(Path.DirectorySeparatorChar);
    }

    public void NotifyPortabilityLabelsChanged()
    {
        OnPropertyChanged(nameof(RecordedRootLabel));
        OnPropertyChanged(nameof(LocalRootLabel));
        OnPropertyChanged(nameof(PathCaption));
    }
}
