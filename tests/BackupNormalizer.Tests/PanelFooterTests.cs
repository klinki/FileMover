using BackupNormalizer.Ui.ViewModels;

namespace BackupNormalizer.Tests;

public sealed class PanelFooterTests
{
    [Fact]
    public void Footer_Counts_Marked_Entries_And_File_Bytes_Without_Parent_Or_Link_Targets()
    {
        var panel = new FilePanelViewModel();
        var parent = new FileEntryItem("..", "", true, 0, default, isParent: true);
        var folder = new FileEntryItem("folder", "folder", true, 0, default);
        var first = new FileEntryItem("a.txt", "a.txt", false, 1024, default);
        var second = new FileEntryItem("b.txt", "b.txt", false, 512, default);
        var link = new FileEntryItem("link", "link", false, 99999, default)
        {
            EntryKind = EntryKind.FileLink,
        };
        foreach (var entry in new[] { parent, folder, first, second, link })
        {
            panel.Entries.Add(entry);
        }
        panel.SelectedEntry = first;
        Assert.Equal(
            "Selected: 0 B / 1.5 KB | Files: 0 / 2 | Dirs: 0 / 1 | Links: 0 / 1",
            panel.FooterSummary
        );

        panel.ToggleMark(first);
        panel.ToggleMark(folder);
        panel.ToggleMark(link);
        Assert.Equal(
            "Selected: 1.0 KB / 1.5 KB | Files: 1 / 2 | Dirs: 1 / 1 | Links: 1 / 1",
            panel.FooterSummary
        );
        panel.ApplySort("Size");
        Assert.Contains("Files: 1 / 2", panel.FooterSummary);
        panel.MarkAll();
        Assert.Contains("Selected: 1.5 KB / 1.5 KB", panel.FooterSummary);
        Assert.False(parent.IsMarked);
        panel.ClearMarks();
        Assert.Contains("Selected: 0 B / 1.5 KB", panel.FooterSummary);
    }

    [Fact]
    public void Footer_Notifies_When_Marks_Or_Listings_Change_And_Detaches_Removed_Entries()
    {
        var panel = new FilePanelViewModel();
        var file = new FileEntryItem("a.txt", "a.txt", false, 10, default);
        int changes = 0;
        panel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(FilePanelViewModel.FooterSummary))
            {
                changes++;
            }
        };
        panel.Entries.Add(file);
        changes = 0;
        file.IsMarked = true;
        Assert.Equal(1, changes);
        Assert.Contains("Selected: 10 B / 10 B", panel.FooterSummary);
        panel.Entries.Clear();
        Assert.Contains("Files: 0 / 0", panel.FooterSummary);
        changes = 0;
        file.IsMarked = false;
        Assert.Equal(0, changes);
    }

    [Fact]
    public void Listing_Errors_Remain_Visible_In_The_Footer()
    {
        var panel = new FilePanelViewModel { Status = "error: Access denied." };
        Assert.Equal("error: Access denied.", panel.FooterSummary);
        panel.Status = "path not found";
        Assert.Equal("path not found", panel.FooterSummary);
        panel.Status = "2 dirs, 3 files";
        panel.DriveStatus = "1.0 GB free of 2.0 GB";
        Assert.Contains("1.0 GB free of 2.0 GB", panel.FooterTooltip);
    }
}
