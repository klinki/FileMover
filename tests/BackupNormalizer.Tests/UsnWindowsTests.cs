using BackupNormalizer;
using Microsoft.Data.Sqlite;

namespace BackupNormalizer.Tests;

public sealed class WindowsUsnFactAttribute : FactAttribute
{
    public WindowsUsnFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || !Elevation.IsWindowsAdmin())
            Skip = "Real USN journal access requires elevated Windows permissions.";
        else
        {
            using var journal = NtfsUsnJournal.TryOpen(AppContext.BaseDirectory);
            if (journal == null) Skip = "An accessible local NTFS journal is unavailable.";
        }
    }
}

public sealed class UsnWindowsTests
{
    [WindowsUsnFact]
    public void Real_Journal_Reports_Creation_Content_Write_And_Rename_With_Resolvable_Parents()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "bn-usn-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var journal = NtfsUsnJournal.TryOpen(directory);
            Assert.NotNull(journal);
            var before = journal.Query();
            string created = Path.Combine(directory, "created.txt");
            File.WriteAllText(created, "before");
            File.AppendAllText(created, "after");
            File.Move(created, Path.Combine(directory, "renamed.txt"));
            var after = journal.Query();
            var records = journal.ReadChanges(before.NextUsn, after.NextUsn, before.JournalId)
                .Where(r => r.Name is "created.txt" or "renamed.txt").ToList();
            Assert.Contains(records, r => (r.Reason & 0x100) != 0);
            Assert.Contains(records, r => (r.Reason & 7) != 0);
            Assert.Contains(records, r => (r.Reason & 0x1000) != 0);
            Assert.Contains(records, r => (r.Reason & 0x2000) != 0);
            Assert.All(records, r => Assert.True(Paths.PathEquals(directory, journal.ResolveParent(r.ParentId))));
            Assert.Equal(1U, journal.GetLinkCount(records[^1].FileId));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Assert.StartsWith(AppContext.BaseDirectory, Path.GetFullPath(directory));
            Directory.Delete(directory, true);
        }
    }
}
