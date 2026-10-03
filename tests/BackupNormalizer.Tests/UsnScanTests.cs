using BackupNormalizer;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BackupNormalizer.Tests;

public sealed class UsnScanTests : IDisposable
{
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "bn-usn-" + Guid.NewGuid().ToString("N"));
    private readonly FakeJournal _journal = new();
    private readonly string _root;
    private readonly string _databasePath;
    private int _fullScans;
    private Func<IEnumerable<FsEntry>>? _enumerate;
    private readonly List<string> _junctions = [];

    [DllImport("kernel32.dll", EntryPoint = "RemoveDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveJunction(string path);

    public UsnScanTests()
    {
        _root = Path.Combine(_directory, "data");
        _databasePath = Path.Combine(_directory, "inventory.db");
        Directory.CreateDirectory(_root);
        _journal.Parents[1] = _root;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Assert.StartsWith(AppContext.BaseDirectory, Path.GetFullPath(_directory));
        foreach (string link in _junctions)
        {
            Assert.StartsWith(_directory + Path.DirectorySeparatorChar, Path.GetFullPath(link));
            Assert.True(RemoveJunction(link), $"Cannot unlink test junction: {Marshal.GetLastWin32Error()}");
        }
        Directory.Delete(_directory, true);
    }

    private Database Open()
    {
        var db = new Database(_databasePath);
        db.UpsertRoot(new StorageRootRow("r", "r", _root, true, "fs", "insensitive", Database.UtcNow()));
        return db;
    }

    private Scanner Scanner(Database db) => new(db, _ =>
    {
        _fullScans++;
        return _enumerate?.Invoke() ?? BackupNormalizer.Scanner.EnumerateRecursive(_root);
    }, _ => _journal);

    private string Write(string name, string content = "before")
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private void Changes(params UsnRecord[] records)
    {
        _journal.State = _journal.State with { NextUsn = 200 };
        _journal.Records = records;
    }

    private static UsnRecord Change(string name, long usn = 110, uint reason = 1 | UsnReplay.Close,
        FileAttributes attributes = FileAttributes.Normal, ulong parent = 1, ulong file = 20)
        => new(file, parent, usn, reason, attributes, name);

    private void Junction(string path, string target)
    {
        var script = Path.Combine(_directory, "junction.ps1");
        File.WriteAllText(script, "param([string]$LinkPath, [string]$TargetPath)\n$ErrorActionPreference = 'Stop'\nNew-Item -ItemType Junction -Path $LinkPath -Target $TargetPath | Out-Null\n");
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
            "-File", script, "-LinkPath", path, "-TargetPath", target }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        string error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30000), "Junction creation timed out.");
        Assert.True(process.ExitCode == 0, error);
        _junctions.Add(path);
    }

    [JunctionFact]
    public void Linked_Parents_Force_Full_Scanning_Without_Refreshing_Target_Contents()
    {
        string original = Write("parent/child", "original");
        using var db = Open();
        var scanner = Scanner(db);
        scanner.ScanRoot("r");
        scanner.HashNeeded("r", parallelism: 1);
        string external = Path.Combine(_directory, "external");
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "child"), "external contents");
        string parent = Path.GetDirectoryName(original)!;
        Directory.Move(parent, Path.Combine(_directory, "old-parent"));
        Junction(parent, external);
        _journal.Parents[2] = parent;
        Changes(Change("child", parent: 2));

        Assert.Equal((1, 0), scanner.ScanRoot("r"));

        Assert.False(scanner.LastScanWasIncremental);
        Assert.Equal(EntryKind.DirectoryLink, db.GetFileEntry("r", "parent")!.EntryKind);
        Assert.Equal(external, db.GetFileEntry("r", "parent")!.TargetPath);
        Assert.Equal(FileStatus.Missing, db.GetFileEntry("r", "parent/child")!.Status);
        Assert.Equal("external contents", File.ReadAllText(Path.Combine(external, "child")));
        Assert.Equal((0, 2, 0), scanner.HashNeeded("r", parallelism: 1));
        Assert.Equal(2, _fullScans);
    }

    [Fact]
    public void Database_Exclusion_Retires_Historical_Rows_With_Case_Insensitive_Paths()
    {
        using var db = Open();
        db.UpsertRoot(new StorageRootRow("all", "all", _directory, true, "fs", "insensitive", Database.UtcNow()));
        long scan = db.BeginScan("all");
        string name = OperatingSystem.IsWindows() ? "INVENTORY.DB" : "inventory.db";
        db.UpsertFileEntry(new FileEntryRow(0, "all", name, name, 1, Database.UtcNow(), null, null, scan, FileStatus.Ok, null));
        db.FinishScan(scan, ScanStatus.Completed);
        new Scanner(db, usnMode: "off").ScanRoot("all");
        Assert.Equal(FileStatus.Missing, db.GetFileEntry("all", name)!.Status);
    }

    [Fact]
    public void Baseline_Then_No_Changes_Avoids_Enumeration_And_Preserves_Inventory_And_Hashes()
    {
        Write("keep.txt");
        using var db = Open();
        var scanner = Scanner(db);
        Assert.Equal((1, 0), scanner.ScanRoot("r"));
        scanner.HashNeeded("r", parallelism: 1);
        var entry = db.GetFileEntry("r", "keep.txt")!;
        var hash = db.GetHash(entry.Id, "sha256");
        Assert.Equal(100, db.GetScanCheckpoint("r")!.NextUsn);

        Assert.Equal((0, 0), scanner.ScanRoot("r"));

        Assert.True(scanner.LastScanWasIncremental);
        Assert.Equal(1, _fullScans);
        Assert.Equal(entry, db.GetFileEntry("r", "keep.txt"));
        Assert.Equal(hash, db.GetHash(entry.Id, "sha256"));
        Assert.Equal(ScanStatus.Completed, db.LatestScanStatus("r"));
        Assert.Equal(db.LatestScan("r")!.Id, db.GetScanCheckpoint("r")!.ScanId);
        Assert.Equal((0, 1, 0), scanner.HashNeeded("r", parallelism: 1));
    }

    [Fact]
    public void Create_Delete_Rename_And_Same_Size_Write_Refresh_Only_Affected_Paths()
    {
        string changed = Write("changed.txt", "before");
        string deleted = Write("deleted.txt");
        string renamed = Write("old.txt");
        Write("keep.txt");
        using var db = Open();
        var scanner = Scanner(db);
        scanner.ScanRoot("r");
        scanner.HashNeeded("r", parallelism: 1);
        var before = db.GetFileEntry("r", "changed.txt")!;
        var unchanged = db.GetFileEntry("r", "keep.txt")!;
        db.UpsertHash(new FileHashRow(before.Id, "other", "old", before.Size, before.ModifiedUtc, Database.UtcNow(), HashState.Ok));
        File.WriteAllText(changed, "after!");
        File.SetLastWriteTimeUtc(changed, DateTime.Parse(before.ModifiedUtc).ToUniversalTime());
        File.Delete(deleted);
        File.Move(renamed, Path.Combine(_root, "new.txt"));
        Write("created.txt");
        Changes(Change("changed.txt"), Change("deleted.txt", 120, UsnReplay.Delete | UsnReplay.Close, file: 21),
            Change("old.txt", 130, 0x1000, file: 22), Change("new.txt", 140, 0x2000 | UsnReplay.Close, file: 22),
            Change("created.txt", 150, 0x100 | UsnReplay.Close, file: 23));

        Assert.Equal((5, 0), scanner.ScanRoot("r"));

        Assert.True(scanner.LastScanWasIncremental);
        Assert.Equal(1, _fullScans);
        Assert.Equal(unchanged, db.GetFileEntry("r", "keep.txt"));
        Assert.Equal(FileStatus.Missing, db.GetFileEntry("r", "deleted.txt")!.Status);
        Assert.Equal(FileStatus.Missing, db.GetFileEntry("r", "old.txt")!.Status);
        Assert.Equal(FileStatus.Ok, db.GetFileEntry("r", "new.txt")!.Status);
        Assert.Equal(FileStatus.Ok, db.GetFileEntry("r", "created.txt")!.Status);
        Assert.Equal(before.ModifiedUtc, db.GetFileEntry("r", "changed.txt")!.ModifiedUtc);
        Assert.Equal(HashState.Stale, db.GetHash(before.Id, "sha256")!.State);
        Assert.Equal(HashState.Stale, db.GetHash(before.Id, "other")!.State);
        Assert.Equal(HashState.Ok, db.GetHash(unchanged.Id, "sha256")!.State);
        Assert.Equal(200, db.GetScanCheckpoint("r")!.NextUsn);
        Assert.Equal((3, 3, 0), scanner.HashNeeded("r", parallelism: 1));
    }

    [Theory]
    [InlineData("journal")]
    [InlineData("history")]
    [InlineData("volume")]
    [InlineData("root")]
    [InlineData("incomplete")]
    [InlineData("started")]
    public void Invalid_Checkpoints_Force_Full_Scans_And_Invalidate_Cached_Hashes(string invalidation)
    {
        Write("keep.txt");
        using var db = Open();
        var scanner = Scanner(db);
        scanner.ScanRoot("r");
        scanner.HashNeeded("r", parallelism: 1);
        _journal.State = invalidation switch
        {
            "journal" => _journal.State with { JournalId = "new" },
            "history" => _journal.State with { FirstUsn = 101, LowestValidUsn = 101, NextUsn = 200 },
            "volume" => _journal.State with { VolumeIdentity = "new" },
            "root" => _journal.State with { RootIdentity = "new" },
            _ => _journal.State
        };
        if (invalidation is "incomplete" or "started")
        {
            long scan = db.BeginScan("r");
            if (invalidation == "incomplete") db.FinishScan(scan, ScanStatus.Incomplete);
        }

        Assert.Equal((1, 0), scanner.ScanRoot("r"));

        Assert.False(scanner.LastScanWasIncremental);
        Assert.NotNull(scanner.LastScanFallbackReason);
        Assert.Equal(2, _fullScans);
        Assert.Equal(HashState.Stale, db.GetHash(db.GetFileEntry("r", "keep.txt")!.Id, "sha256")!.State);
        Assert.Equal(ScanStatus.Completed, db.LatestScanStatus("r"));
        Assert.Equal(_journal.State.JournalId, db.GetScanCheckpoint("r")!.JournalId);
    }

    [Theory]
    [InlineData(0x100, true)]
    [InlineData(0x200, true)]
    [InlineData(0x1000, true)]
    [InlineData(0x100000, true)]
    [InlineData(0x800, true)]
    [InlineData(0x10000, false)]
    public void Directory_And_Hard_Link_Changes_Use_Full_Scans(uint reason, bool directory)
    {
        Write("keep.txt");
        using var db = Open();
        var scanner = Scanner(db);
        scanner.ScanRoot("r");
        Changes(Change("changed", reason: reason | UsnReplay.Close,
            attributes: directory ? FileAttributes.Directory : FileAttributes.Normal));

        Assert.Equal((1, 0), scanner.ScanRoot("r"));

        Assert.False(scanner.LastScanWasIncremental);
        Assert.Equal(2, _fullScans);
        Assert.Equal(ScanStatus.Completed, db.LatestScanStatus("r"));
    }

    [Fact]
    public void Content_Writes_To_Existing_Hard_Links_Also_Force_Full_Scanning()
    {
        Write("keep.txt");
        using var db = Open();
        var scanner = Scanner(db);
        scanner.ScanRoot("r");
        _journal.LinkCounts[20] = 2;
        Changes(Change("keep.txt"));
        scanner.ScanRoot("r");
        Assert.False(scanner.LastScanWasIncremental);
        Assert.Equal(2, _fullScans);
    }

    [Fact]
    public void A_Hard_Link_Change_Recorded_Outside_The_Root_Still_Invalidates_Inventory_Hashes()
    {
        Write("inside.txt");
        using var db = Open();
        var scanner = Scanner(db);
        scanner.ScanRoot("r");
        scanner.HashNeeded("r", parallelism: 1);
        _journal.Parents[2] = _directory;
        _journal.LinkCounts[20] = 2;
        Changes(Change("outside.txt", parent: 2));

        scanner.ScanRoot("r");

        Assert.False(scanner.LastScanWasIncremental);
        Assert.Equal(HashState.Stale, db.GetHash(db.GetFileEntry("r", "inside.txt")!.Id, "sha256")!.State);
        Assert.Equal(2, _fullScans);
    }

    [Fact]
    public void Case_Only_Renames_Are_Reconciled_By_A_Full_Scan()
    {
        string original = Write("old.txt");
        using var db = Open();
        var scanner = Scanner(db);
        scanner.ScanRoot("r");
        string temporary = Path.Combine(_directory, "temporary");
        File.Move(original, temporary);
        File.Move(temporary, Path.Combine(_root, "OLD.txt"));
        Changes(Change("old.txt", reason: 0x1000), Change("OLD.txt", 120, 0x2000 | UsnReplay.Close));

        scanner.ScanRoot("r");

        Assert.False(scanner.LastScanWasIncremental);
        Assert.Equal(FileStatus.Missing, db.GetFileEntry("r", "old.txt")!.Status);
        Assert.Equal(FileStatus.Ok, db.GetFileEntry("r", "OLD.txt")!.Status);
        Assert.Equal(2, _fullScans);
    }

    [Fact]
    public void Outside_Changes_And_Database_Companions_Do_Not_Refresh_Inventory()
    {
        Write("keep.txt");
        using var db = Open();
        // A second root covers the active database and its companions.
        db.UpsertRoot(new StorageRootRow("all", "all", _directory, true, "fs", "insensitive", Database.UtcNow()));
        _journal.Parents[2] = _directory;
        var scanner = new Scanner(db, root => BackupNormalizer.Scanner.EnumerateRecursive(root), _ => _journal);
        scanner.ScanRoot("all");
        Changes(Change("inventory.db", parent: 2), Change("inventory.db-wal", 120, parent: 2),
            Change("inventory.db-shm", 130, parent: 2), Change("outside", 140, parent: 3));
        _journal.Parents[3] = Path.GetTempPath();

        Assert.Equal((0, 0), scanner.ScanRoot("all"));

        Assert.True(scanner.LastScanWasIncremental);
        Assert.All(db.ListFiles("all"), f => Assert.StartsWith("data/", f.RelativePath));
        Assert.Equal(200, db.GetScanCheckpoint("all")!.NextUsn);
    }

    [Fact]
    public void Unresolved_Parents_And_Read_Failures_Fall_Back_Without_Losing_Unseen_Entries()
    {
        Write("keep.txt");
        using var db = Open();
        var scanner = Scanner(db);
        scanner.ScanRoot("r");
        Changes(Change("gone.txt", parent: 99));
        Assert.Equal((1, 0), scanner.ScanRoot("r"));
        Assert.False(scanner.LastScanWasIncremental);
        _journal.ReadFailure = new IOException("Journal history disappeared.");
        _journal.State = _journal.State with { NextUsn = 300 };
        _enumerate = () => [new FsEntry(_root, true, 0, default, default, false, false, "Access denied.")];

        Assert.Equal((0, 1), scanner.ScanRoot("r"));

        Assert.Equal(FileStatus.Ok, db.GetFileEntry("r", "keep.txt")!.Status);
        Assert.Null(db.GetScanCheckpoint("r"));
        Assert.Equal(ScanStatus.Incomplete, db.LatestScanStatus("r"));
    }

    [Fact]
    public void Full_Enumeration_Checkpoint_Precedes_Changes_During_The_Scan()
    {
        Write("keep.txt");
        using var db = Open();
        _enumerate = () =>
        {
            _journal.State = _journal.State with { NextUsn = 200 };
            _journal.Records = [Change("keep.txt")];
            return BackupNormalizer.Scanner.EnumerateRecursive(_root);
        };
        var scanner = Scanner(db);
        scanner.ScanRoot("r");
        Assert.Equal(100, db.GetScanCheckpoint("r")!.NextUsn);

        Assert.Equal((1, 0), scanner.ScanRoot("r"));

        Assert.True(scanner.LastScanWasIncremental);
        Assert.Equal(1, _fullScans);
        Assert.Equal(200, db.GetScanCheckpoint("r")!.NextUsn);
    }

    [Fact]
    public void Still_Open_Changes_Are_Replayed_Until_Their_Close_Record()
    {
        Write("open.txt");
        using var db = Open();
        var scanner = Scanner(db);
        scanner.ScanRoot("r");
        Changes(Change("open.txt", reason: 1));
        scanner.ScanRoot("r");
        Assert.Equal(110, db.GetScanCheckpoint("r")!.NextUsn);
        scanner.ScanRoot("r");
        Assert.Equal(110, db.GetScanCheckpoint("r")!.NextUsn);
        _journal.Records = [Change("open.txt", reason: 1), Change("open.txt", 120)];

        Assert.Equal((1, 0), scanner.ScanRoot("r"));

        Assert.Equal(200, db.GetScanCheckpoint("r")!.NextUsn);
        Assert.Equal(1, _fullScans);
    }

    [Fact]
    public void Full_Control_Enumerates_And_Still_Invalidates_Known_Content_Changes()
    {
        Write("keep.txt");
        using var db = Open();
        var scanner = Scanner(db);
        scanner.ScanRoot("r");
        scanner.HashNeeded("r", parallelism: 1);
        Changes(Change("keep.txt"));

        Assert.Equal((1, 0), scanner.ScanRoot("r", full: true));

        Assert.False(scanner.LastScanWasIncremental);
        Assert.Equal(2, _fullScans);
        Assert.Equal(HashState.Stale, db.GetHash(db.GetFileEntry("r", "keep.txt")!.Id, "sha256")!.State);
    }

    [Fact]
    public void Off_Control_Discards_Checkpoints_And_Root_Moves_Invalidate_Them()
    {
        Write("keep.txt");
        using var db = Open();
        Scanner(db).ScanRoot("r");
        Assert.NotNull(db.GetScanCheckpoint("r"));
        var off = new Scanner(db, usnMode: "off");
        Assert.Equal((1, 0), off.ScanRoot("r"));
        Assert.Null(db.GetScanCheckpoint("r"));
        Scanner(db).ScanRoot("r");
        db.UpsertRoot(db.GetRoot("r")! with { Path = _directory });
        Assert.Null(db.GetScanCheckpoint("r"));
        Assert.Equal(ScanStatus.Invalidated, db.LatestScanStatus("r"));
        Assert.Throws<ArgumentException>(() => new Scanner(db, usnMode: "invalid"));
    }

    [Fact]
    public void Journal_Reset_During_Delta_Rolls_Back_Changes_Before_Full_Retry()
    {
        Write("keep.txt");
        using var db = Open();
        var scanner = Scanner(db);
        scanner.ScanRoot("r");
        var original = db.GetFileEntry("r", "keep.txt");
        Write("keep.txt", "changed size");
        Changes(Change("keep.txt"));
        _journal.OnQuery = query =>
        {
            if (query != 4) return;
            // Observe the provisional update, then fail journal validation.
            Assert.Equal(12, db.GetFileEntry("r", "keep.txt")!.Size);
            _enumerate = () => throw new IOException("Full retry cannot enumerate the root.");
            throw new IOException("Journal reset during refresh.");
        };

        Assert.Throws<IOException>(() => scanner.ScanRoot("r"));

        Assert.Equal(original, db.GetFileEntry("r", "keep.txt"));
        Assert.Null(db.GetScanCheckpoint("r"));
        Assert.Equal(ScanStatus.Failed, db.LatestScanStatus("r"));
        Assert.Equal(2, _fullScans);
    }

    [Fact]
    public void Legacy_Read_Only_Inventories_Load_Without_Checkpoints_Or_Migration()
    {
        var options = new DbContextOptionsBuilder<BackupNormalizerDbContext>().UseSqlite($"Data Source={_databasePath}").Options;
        using (var context = new BackupNormalizerDbContext(options))
            context.GetService<IMigrator>().Migrate("20261002075521_RecordLinks");
        using (var readOnly = Database.OpenReadOnly(_databasePath, pooling: false))
        {
            Assert.Null(readOnly.GetScanCheckpoint("r"));
            Assert.Empty(readOnly.ListFiles());
            Assert.Equal(3, readOnly.AppliedMigrations().Count);
        }
        using var upgraded = Open();
        Assert.Equal(4, upgraded.AppliedMigrations().Count);
        Assert.False(upgraded.Context.Database.HasPendingModelChanges());
        Scanner(upgraded).ScanRoot("r");
        Assert.NotNull(upgraded.GetScanCheckpoint("r"));
    }

    [SymlinkFact]
    public void File_Link_Transitions_And_Retargeting_Stay_Nonfatal_And_Invalidate_Hashes()
    {
        string path = Write("entry");
        using var db = Open();
        var scanner = Scanner(db);
        scanner.ScanRoot("r");
        scanner.HashNeeded("r", parallelism: 1);
        long id = db.GetFileEntry("r", "entry")!.Id;
        File.Delete(path);
        File.CreateSymbolicLink(path, "missing");
        Changes(Change("entry", reason: UsnReplay.ReparseChange | UsnReplay.Close, attributes: FileAttributes.ReparsePoint));
        Assert.Equal((1, 0), scanner.ScanRoot("r"));
        Assert.Equal(EntryKind.FileLink, db.GetFileEntry("r", "entry")!.EntryKind);
        Assert.Equal("missing", db.GetFileEntry("r", "entry")!.LinkTarget);
        Assert.Equal(HashState.Stale, db.GetHash(id, "sha256")!.State);
        File.Delete(path);
        File.CreateSymbolicLink(path, "different");
        _journal.State = _journal.State with { NextUsn = 300 };
        _journal.Records = [Change("entry", 210, UsnReplay.ReparseChange | UsnReplay.Close, FileAttributes.ReparsePoint)];
        Assert.Equal((1, 0), scanner.ScanRoot("r"));
        Assert.Equal("different", db.GetFileEntry("r", "entry")!.LinkTarget);
        File.Delete(path);
        Write("entry");
        _journal.State = _journal.State with { NextUsn = 400 };
        _journal.Records = [Change("entry", 310)];
        Assert.Equal((1, 0), scanner.ScanRoot("r"));
        Assert.Equal(EntryKind.File, db.GetFileEntry("r", "entry")!.EntryKind);
        Assert.Equal(1, _fullScans);
    }

    internal sealed class FakeJournal : IUsnJournal
    {
        internal UsnState State = new("volume", "root", "1234", 0, 100, 0);
        internal IReadOnlyList<UsnRecord> Records = [];
        internal readonly Dictionary<ulong, string> Parents = [];
        internal readonly Dictionary<ulong, uint> LinkCounts = [];
        internal IOException? ReadFailure;
        internal Action<int>? OnQuery;
        private int _queries;
        public UsnState Query() { _queries++; OnQuery?.Invoke(_queries); return State; }
        public IEnumerable<UsnRecord> ReadChanges(long startUsn, long endUsn, string journalId)
            => ReadFailure != null ? throw ReadFailure : Records.Where(r => r.Usn >= startUsn && r.Usn < endUsn);
        public string ResolveParent(ulong fileId)
            => Parents.TryGetValue(fileId, out var path) ? path : throw new IOException("Parent directory disappeared.");
        public uint GetLinkCount(ulong fileId) => LinkCounts.GetValueOrDefault(fileId, 1U);
        public void Dispose() { }
    }
}
