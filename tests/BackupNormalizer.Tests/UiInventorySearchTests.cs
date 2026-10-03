using BackupNormalizer.Ui.Models;
using BackupNormalizer.Ui.ViewModels;

namespace BackupNormalizer.Tests;

public sealed class UiInventorySearchTests : IDisposable
{
    private const string Modified = "2026-09-30T12:00:00.0000000Z";
    private const string Created = "2026-09-20T08:30:00.0000000Z";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bn-inventory-search-" + Guid.NewGuid().ToString("N"));

    public UiInventorySearchTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        Directory.Delete(_directory, true);
    }

    [Theory]
    [InlineData("insensitive", "mixedname", true)]
    [InlineData("sensitive", "mixedname", false)]
    public void Search_Uses_Recorded_Case_Rules_And_Keeps_Matching_Parents_Navigable(
        string sensitivity, string query, bool expectedMatch)
    {
        string database = CreateDatabase("search", sensitivity);
        Add(database, "Photos/MixedName.txt");
        var panel = Load(database);

        panel.SearchText = query;
        Assert.Equal(expectedMatch, panel.Entries.Any(e => e.Name == "Photos"));
        if (!expectedMatch) return;

        Assert.True(panel.NavigateTo(panel.Entries.Single(e => e.Name == "Photos")));
        Assert.Contains(panel.Entries, e => e.IsParentEntry);
        Assert.Equal("MixedName.txt", Assert.Single(panel.Entries, e => !e.IsParentEntry).Name);

        panel.SearchText = "Photos/MixedName";
        Assert.Equal("MixedName.txt", Assert.Single(panel.Entries, e => !e.IsParentEntry).Name);
    }

    [Fact]
    public void Focused_Filters_Find_Descendants_Keep_Ancestors_And_Combine_With_DifferencesOnly()
    {
        string database = CreateDatabase("filters");
        Add(database, "group/unverified.txt", digest: null);
        Add(database, "group/conflict.txt");
        Add(database, "group/broken.txt", status: FileStatus.ScanError, digest: null);
        Add(database, "group/shortcut", entryKind: EntryKind.FileLink,
            linkTarget: "../target", targetPath: "/offline/filters/target", linkNote: "Target unavailable");
        byte[] original = File.ReadAllBytes(database);
        var panel = Load(database);
        var states = new Dictionary<string, ComparisonState>
        {
            ["group/unverified.txt"] = ComparisonState.Unverified,
            ["group/conflict.txt"] = ComparisonState.TypeConflict,
            ["group/broken.txt"] = ComparisonState.ScanError,
            ["group/shortcut"] = ComparisonState.Skipped,
        };
        panel.ApplyComparison(states, differencesOnly: false);

        foreach (var (filter, expected) in new[]
        {
            ("Unverified", "unverified.txt"),
            ("Conflicts", "conflict.txt"),
            ("Scan errors", "broken.txt"),
            ("Links", "shortcut"),
        })
        {
            panel.SelectedInventoryFilter = filter;
            Assert.Equal("group", Assert.Single(panel.Entries).Name);
            Assert.True(panel.NavigateTo(panel.Entries.Single()));
            Assert.Contains(panel.Entries, e => e.IsParentEntry);
            Assert.Equal(expected, Assert.Single(panel.Entries, e => !e.IsParentEntry).Name);
            panel.NavigateInventory("");
        }

        panel.ApplyComparison(states, differencesOnly: true);
        panel.SelectedInventoryFilter = "All";
        Assert.Equal("group", Assert.Single(panel.Entries).Name);
        panel.NavigateTo(panel.Entries.Single());
        Assert.DoesNotContain(panel.Entries, e => e.Name == "shortcut");
        Assert.Contains(panel.Entries, e => e.Name == "unverified.txt");
        Assert.Equal(original, File.ReadAllBytes(database));
    }

    [Fact]
    public void Selected_Details_Show_Recorded_Metadata_And_Clear_When_Search_Removes_Selection()
    {
        string database = CreateDatabase("details");
        Add(database, "folder/shortcut", entryKind: EntryKind.FileLink,
            linkTarget: "../ExactTarget", targetPath: "/offline/details/ExactTarget", linkNote: "Recorded note");
        Add(database, "folder/verified.txt", digest: "deadbeef");
        var panel = Load(database);
        panel.NavigateTo(panel.Entries.Single(e => e.Name == "folder"));
        panel.SelectedEntry = panel.Entries.Single(e => e.IsParentEntry);
        Assert.False(panel.HasSelectedInventoryEntry);
        var link = panel.Entries.Single(e => e.Name == "shortcut");
        panel.SelectedEntry = link;

        Assert.True(panel.HasSelectedInventoryEntry);
        Assert.Equal("shortcut", panel.SelectedDetailsName);
        Assert.Equal("/offline/details/folder/shortcut", panel.SelectedDetailsPath);
        Assert.Equal(DateTimeOffset.Parse(Created).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"), panel.SelectedDetailsCreated);
        Assert.Equal(DateTimeOffset.Parse(Modified).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"), panel.SelectedDetailsModified);
        Assert.Equal("file link", panel.SelectedDetailsKind);
        Assert.Equal("../ExactTarget", panel.SelectedDetailsLinkTarget);
        Assert.Equal("/offline/details/ExactTarget", panel.SelectedDetailsAbsoluteTarget);
        Assert.Equal("Recorded note", panel.SelectedDetailsLinkNote);

        panel.SelectedEntry = panel.Entries.Single(e => e.Name == "verified.txt");
        Assert.Equal("Available (SHA-256)", panel.SelectedDetailsHashAvailability);
        Assert.Equal("deadbeef", panel.SelectedDetailsHash);

        panel.SearchText = "missing-entry";
        Assert.Null(panel.SelectedEntry);
        Assert.False(panel.HasSelectedInventoryEntry);
        Assert.Equal("", panel.SelectedDetailsPath);
    }

    private string CreateDatabase(string name, string sensitivity = "sensitive")
    {
        string path = Path.Combine(_directory, name + ".db");
        using var db = Database.OpenWritable(path, pooling: false);
        db.UpsertRoot(new StorageRootRow("disk", name, "/offline/" + name, false, "unknown", sensitivity, Modified));
        long scan = db.BeginScan("disk");
        db.FinishScan(scan, ScanStatus.Completed);
        return path;
    }

    private static void Add(string database, string relativePath, string? digest = "abc",
        string status = FileStatus.Ok, string entryKind = EntryKind.File, string? linkTarget = null,
        string? targetPath = null, string? linkNote = null)
    {
        using var db = Database.OpenWritable(database, pooling: false);
        long id = db.UpsertFileEntry(new FileEntryRow(0, "disk", relativePath, relativePath.Split('/')[^1], 42,
            Modified, Created, null, 1, status, status == FileStatus.Ok ? null : "scan failed",
            entryKind, linkTarget, targetPath, linkNote));
        if (digest != null && status == FileStatus.Ok)
            db.UpsertHash(new FileHashRow(id, "sha256", digest, 42, Modified, Modified, HashState.Ok));
    }

    private static FilePanelViewModel Load(string database)
    {
        var panel = new FilePanelViewModel();
        panel.LoadSnapshot(InventorySnapshot.Load(database));
        return panel;
    }
}
