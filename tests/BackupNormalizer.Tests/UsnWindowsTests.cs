using BackupNormalizer;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using Xunit.Abstractions;

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

public sealed class UsnWindowsTests(ITestOutputHelper output)
{
    [WindowsUsnFact]
    public void Native_Usn_Recursive_And_Mft_Inventories_And_Hashes_Agree()
    {
        string fixture = Path.Combine(AppContext.BaseDirectory, "bn-usn-parity-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(fixture, "data");
        string outside = Path.Combine(fixture, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        try
        {
            for (int i = 0; i < 1000; i++) File.WriteAllText(Path.Combine(root, $"file-{i:0000}.txt"), "before");
            File.WriteAllText(Path.Combine(outside, "untouched.txt"), "external");
            using var incremental = Open("usn");
            using var recursive = Open("recursive");
            using var mft = Open("mft");
            var usnScanner = new Scanner(incremental);
            var recursiveScanner = new Scanner(recursive, usnMode: "off");
            var mftScanner = new Scanner(mft, mftMode: "require", usnMode: "off");
            ScanAndHash(usnScanner, "USN baseline", all: false);
            Assert.NotNull(incremental.GetScanCheckpoint("r"));
            ScanAndHash(recursiveScanner, "recursive baseline", all: false);
            ScanAndHash(mftScanner, "MFT baseline", all: false);
            AssertInventoryEquals(incremental, recursive);
            AssertInventoryEquals(incremental, mft);

            string edited = Path.Combine(root, "file-0000.txt");
            DateTime timestamp = File.GetLastWriteTimeUtc(edited);
            File.WriteAllText(edited, "AFTER!");
            File.SetLastWriteTimeUtc(edited, timestamp);
            File.WriteAllText(Path.Combine(root, "added-Ω.txt"), "added");
            File.Move(Path.Combine(root, "file-0001.txt"), Path.Combine(root, "renamed.txt"));
            File.Delete(Path.Combine(root, "file-0002.txt"));
            ScanAndHash(usnScanner, "USN changed files", all: false);
            Assert.True(usnScanner.LastScanWasIncremental, usnScanner.LastScanFallbackReason);
            ScanAndHash(recursiveScanner, "recursive changed files", all: true);
            ScanAndHash(mftScanner, "MFT changed files", all: true);
            AssertInventoryEquals(incremental, recursive);
            AssertInventoryEquals(incremental, mft);

            string fileLink = Path.Combine(root, "file-link.txt");
            string directoryLink = Path.Combine(root, "directory-link");
            File.CreateSymbolicLink(fileLink, Path.Combine(outside, "untouched.txt"));
            Directory.CreateSymbolicLink(directoryLink, outside);
            try
            {
                ScanAndHash(usnScanner, "USN link fallback", all: false);
                Assert.False(usnScanner.LastScanWasIncremental);
                ScanAndHash(recursiveScanner, "recursive links", all: true);
                ScanAndHash(mftScanner, "MFT links", all: true);
                AssertInventoryEquals(incremental, recursive);
                AssertInventoryEquals(incremental, mft);
                Assert.DoesNotContain(incremental.ListFiles("r"), e => e.RelativePath.StartsWith("directory-link/"));
                Assert.Equal("external", File.ReadAllText(Path.Combine(outside, "untouched.txt")));
            }
            finally
            {
                File.Delete(fileLink);
                Directory.Delete(directoryLink);
            }

            Database Open(string name)
            {
                var db = new Database(Path.Combine(fixture, name + ".db"));
                db.UpsertRoot(new StorageRootRow("r", "fixture", root, true, "NTFS", "insensitive", Database.UtcNow()));
                return db;
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Assert.StartsWith(AppContext.BaseDirectory, Path.GetFullPath(fixture));
            Directory.Delete(fixture, true);
        }
    }

    private void ScanAndHash(Scanner scanner, string label, bool all)
    {
        var clock = Stopwatch.StartNew();
        var scan = scanner.ScanRoot("r");
        double seconds = clock.Elapsed.TotalSeconds;
        Assert.Equal(0, scan.errors);
        var hashes = scanner.HashNeeded("r", all, parallelism: 1);
        Assert.Equal(0, hashes.unstable);
        output.WriteLine($"{label}: {scan.scanned} entries, {seconds:F3}s scan, {hashes.hashed} hashed, {hashes.skipped} reused/skipped; incremental={scanner.LastScanWasIncremental}; fallback={scanner.LastScanFallbackReason}");
    }

    private static void AssertInventoryEquals(Database actual, Database expected)
    {
        Assert.Equal(Snapshot(expected), Snapshot(actual));
        static object[] Snapshot(Database db) => db.ListFiles("r").OrderBy(e => e.RelativePath, StringComparer.Ordinal)
            .Select(e => (object)new
            {
                e.RelativePath, e.Status, e.EntryKind, e.Size, e.ModifiedUtc, e.LinkTarget, e.TargetPath,
                Digest = e.Status == FileStatus.Ok && e.EntryKind == EntryKind.File ? db.GetHash(e.Id, "sha256")?.Digest : null
            }).ToArray();
    }

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
