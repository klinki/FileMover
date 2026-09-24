using BackupNormalizer.Ui.ViewModels;

namespace BackupNormalizer.Tests;

public sealed class PanelViewTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bn-panel-" + Guid.NewGuid().ToString("N"));
    public PanelViewTests() { Directory.CreateDirectory(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static void W(string root, string rel, string content)
    {
        var abs = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, content);
    }

    [Fact]
    public void DotDot_Is_First_And_Navigates_Up()
    {
        var sub = Path.Combine(_dir, "sub");
        Directory.CreateDirectory(sub);
        W(sub, "b.txt", "b");
        var panel = new FilePanelViewModel { CurrentPath = sub };
        panel.Refresh();
        Assert.True(panel.Entries.Count >= 2);
        var first = panel.Entries[0];
        Assert.True(first.IsParentEntry);
        Assert.Equal("..", first.Name);
        Assert.Equal("", first.Extension);
        Assert.True(panel.NavigateTo(first));
        Assert.Equal(_dir, panel.CurrentPath);
    }

    [Fact]
    public void DotDot_Hidden_At_Filesystem_Root()
    {
        string root = Path.GetPathRoot(Path.GetFullPath(_dir))!;
        var panel = new FilePanelViewModel { CurrentPath = root };
        panel.Refresh();
        Assert.DoesNotContain(panel.Entries, e => e.IsParentEntry);
    }

    [Fact]
    public void Sorting_Keeps_DotDot_First_And_Dirs_Before_Files()
    {
        var sub = Path.Combine(_dir, "sort");
        Directory.CreateDirectory(sub);
        Directory.CreateDirectory(Path.Combine(sub, "zdir"));
        Directory.CreateDirectory(Path.Combine(sub, "adir"));
        W(sub, "m.txt", "m");
        W(sub, "a.txt", "a");
        var panel = new FilePanelViewModel { CurrentPath = sub };
        panel.Refresh(); // default Name asc
        Assert.True(panel.Entries[0].IsParentEntry);
        var names = panel.Entries.Skip(1).Select(e => e.Name).ToList();
        Assert.Equal(new[] { "adir", "zdir", "a.txt", "m.txt" }, names);

        panel.ApplySort("Name"); // toggle desc
        Assert.True(panel.Entries[0].IsParentEntry);
        names = panel.Entries.Skip(1).Select(e => e.Name).ToList();
        Assert.Equal(new[] { "zdir", "adir", "m.txt", "a.txt" }, names);
    }

    [Fact]
    public void Name_And_Extension_Split()
    {
        var sub = Path.Combine(_dir, "ext");
        Directory.CreateDirectory(sub);
        W(sub, "photo.jpg", "x");
        Directory.CreateDirectory(Path.Combine(sub, "docs"));
        var panel = new FilePanelViewModel { CurrentPath = sub };
        panel.Refresh();
        var photo = panel.Entries.Single(e => e.Name == "photo.jpg");
        Assert.Equal("photo", photo.BaseName);
        Assert.Equal("jpg", photo.Extension);
        var docs = panel.Entries.Single(e => e.Name == "docs");
        Assert.Equal("docs", docs.BaseName);
        Assert.Equal("", docs.Extension);
    }

    [Fact]
    public void Sort_By_Ext_Groups_Types()
    {
        var sub = Path.Combine(_dir, "extsort");
        Directory.CreateDirectory(sub);
        W(sub, "b.txt", "b");
        W(sub, "a.jpg", "a");
        var panel = new FilePanelViewModel { CurrentPath = sub };
        panel.Refresh();
        panel.ApplySort("Ext");
        var exts = panel.Entries.Where(e => !e.IsParentEntry).Select(e => e.Extension).ToList();
        Assert.Equal(new[] { "jpg", "txt" }, exts);
    }
}
