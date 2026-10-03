using System.Diagnostics;
using System.Runtime.InteropServices;
using BackupNormalizer;
using BackupNormalizer.Ui.Models;
using BackupNormalizer.Ui.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BackupNormalizer.Tests;

public class SymlinkFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> Supported = new(() =>
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "bn-link-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var link = Path.Combine(directory, "link");
        try { File.CreateSymbolicLink(link, "missing"); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return false; }
        finally { File.Delete(link); Directory.Delete(directory); }
    });

    public SymlinkFactAttribute()
    {
        if (!Supported.Value) Skip = "This environment cannot create symbolic links.";
    }
}

public sealed class ElevatedMftFactAttribute : SymlinkFactAttribute
{
    public ElevatedMftFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || !Elevation.IsWindowsAdmin())
            Skip = "A real MFT comparison requires elevated Windows access.";
    }
}

public sealed class JunctionFactAttribute : FactAttribute
{
    public JunctionFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Junctions require Windows.";
    }
}

public sealed class SymlinkTests : IDisposable
{
    private readonly string _dir = Path.Combine(AppContext.BaseDirectory, "bn-links-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _junctions = [];
    [DllImport("kernel32.dll", EntryPoint = "RemoveDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveJunction(string path);
    public SymlinkTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var junction in _junctions)
        {
            Assert.StartsWith(_dir + Path.DirectorySeparatorChar, Path.GetFullPath(junction));
            // Remove only the directory entry. Recursive .NET deletion also tries
            // to remove the mount point and requires privileges unavailable here.
            Assert.True(RemoveJunction(junction), $"Unable to unlink test junction: Win32 {Marshal.GetLastWin32Error()}");
        }
        Directory.Delete(_dir, true);
    }

    private string Folder(string name)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private Database Open(string name, string root)
    {
        var db = new Database(Path.Combine(_dir, name + ".db"));
        db.UpsertRoot(new StorageRootRow("r", "r", root, true, "fs", "insensitive", Database.UtcNow()));
        return db;
    }

    private static void ScanHash(Database db)
    {
        var scanner = new Scanner(db);
        Assert.Equal(0, scanner.ScanRoot("r").errors);
        scanner.HashNeeded("r", true, 1);
    }

    [SymlinkFact]
    public void Scan_Retains_Raw_And_Immediate_Targets_Without_Hashing_Link_Contents()
    {
        var root = Folder("root");
        var external = Folder("external");
        File.WriteAllText(Path.Combine(root, "file.txt"), "inside");
        File.WriteAllText(Path.Combine(external, "outside.txt"), "outside");
        File.CreateSymbolicLink(Path.Combine(root, "relative.txt"), "file.txt");
        File.CreateSymbolicLink(Path.Combine(root, "absolute.txt"), Path.Combine(external, "outside.txt"));
        File.CreateSymbolicLink(Path.Combine(root, "broken.txt"), "absent.txt");
        File.CreateSymbolicLink(Path.Combine(root, "chain.txt"), "relative.txt");
        File.CreateSymbolicLink(Path.Combine(root, "cycle-a"), "cycle-b");
        File.CreateSymbolicLink(Path.Combine(root, "cycle-b"), "cycle-a");
        Directory.CreateSymbolicLink(Path.Combine(root, "linked-directory"), external);
        Directory.CreateSymbolicLink(Path.Combine(root, "directory-cycle"), root);
        Directory.CreateSymbolicLink(Path.Combine(root, "broken-directory"), Path.Combine(_dir, "absent-directory"));
        using var db = Open("inventory", root);
        var progress = new List<ScanProgress>();
        var scanner = new Scanner(db);
        Assert.Equal((10, 0), scanner.ScanRoot("r", new InlineProgress<ScanProgress>(progress.Add)));
        Assert.Equal(ScanStatus.Completed, db.LatestScanStatus("r"));
        Assert.Equal(10, progress[^1].Scanned);
        Assert.All(db.ListFiles(), f => { Assert.Equal(FileStatus.Ok, f.Status); Assert.Null(f.Error); });
        var relative = db.GetFileEntry("r", "relative.txt")!;
        Assert.Equal(EntryKind.FileLink, relative.EntryKind);
        Assert.Equal("file.txt", relative.LinkTarget);
        Assert.Equal(Path.Combine(root, "file.txt"), relative.TargetPath);
        Assert.Equal(Path.Combine(root, "relative.txt"), db.GetFileEntry("r", "chain.txt")!.TargetPath);
        Assert.Equal(Path.Combine(root, "absent.txt"), db.GetFileEntry("r", "broken.txt")!.TargetPath);
        Assert.NotNull(db.GetFileEntry("r", "broken.txt")!.LinkNote);
        Assert.Equal(EntryKind.DirectoryLink, db.GetFileEntry("r", "linked-directory")!.EntryKind);
        Assert.Equal(EntryKind.DirectoryLink, db.GetFileEntry("r", "broken-directory")!.EntryKind);
        Assert.DoesNotContain(db.ListFiles(), f => f.RelativePath.Contains('/'));
        Assert.Equal((1, 9, 0), scanner.HashNeeded("r", true, 1));
        Assert.All(db.ListFiles().Where(f => f.EntryKind != EntryKind.File), f =>
        {
            Assert.Equal(0, f.Size);
            Assert.Null(db.GetHash(f.Id, "sha256"));
        });
        Assert.Single(Matcher.LoadFromDb(db, "sha256", "r"));
    }

    [Fact]
    public void Unavailable_Mft_Link_Metadata_Is_A_Note_And_Does_Not_Block_Missing_Entries()
    {
        var root = Folder("root");
        File.WriteAllText(Path.Combine(root, "old.txt"), "old");
        using var db = Open("inventory", root);
        new Scanner(db).ScanRoot("r");
        var scanner = new Scanner(db, _ => [new FsEntry(Path.Combine(root, "unavailable"), false,
            999, DateTime.UtcNow, DateTime.UtcNow, true, true, null)]);
        Assert.Equal((1, 0), scanner.ScanRoot("r"));
        var link = db.GetFileEntry("r", "unavailable")!;
        Assert.Equal(EntryKind.ReparsePoint, link.EntryKind);
        Assert.Equal(FileStatus.Ok, link.Status);
        Assert.NotNull(link.LinkNote);
        Assert.Null(link.Error);
        Assert.Equal(0, link.Size);
        Assert.Equal(FileStatus.Missing, db.GetFileEntry("r", "old.txt")!.Status);
    }

    [SymlinkFact]
    public void File_Link_Transitions_Invalidate_All_Hash_Algorithms_And_Retargeting_Is_Recorded()
    {
        var root = Folder("root");
        var path = Path.Combine(root, "entry");
        File.WriteAllText(path, "same");
        using var db = Open("inventory", root);
        ScanHash(db);
        var before = db.GetFileEntry("r", "entry")!;
        db.UpsertHash(new FileHashRow(before.Id, "other", "digest", before.Size, before.ModifiedUtc, Database.UtcNow(), HashState.Ok));
        File.Delete(path);
        File.CreateSymbolicLink(path, "first-missing");
        new Scanner(db).ScanRoot("r");
        Assert.Equal(HashState.Stale, db.GetHash(before.Id, "sha256")!.State);
        Assert.Equal(HashState.Stale, db.GetHash(before.Id, "other")!.State);
        Assert.Null(db.ListFilesWithHashes("r", "sha256").Single().Digest);
        File.Delete(path);
        File.CreateSymbolicLink(path, "second-missing");
        new Scanner(db).ScanRoot("r");
        Assert.Equal("second-missing", db.GetFileEntry("r", "entry")!.LinkTarget);
        File.Delete(path);
        File.WriteAllText(path, "same");
        File.SetLastWriteTimeUtc(path, DateTime.Parse(before.ModifiedUtc).ToUniversalTime());
        new Scanner(db).ScanRoot("r");
        var regular = db.GetFileEntry("r", "entry")!;
        Assert.Equal(EntryKind.File, regular.EntryKind);
        Assert.Null(regular.LinkTarget);
        Assert.Null(regular.LinkNote);
        Assert.Equal((1, 0, 0), new Scanner(db).HashNeeded("r", false, 1));
        File.Delete(path);
        new Scanner(db).ScanRoot("r");
        Assert.Equal(FileStatus.Missing, db.GetFileEntry("r", "entry")!.Status);
    }

    [SymlinkFact]
    public void Hashing_Detects_Links_After_Scanning_Even_With_A_Cached_Hash()
    {
        var root = Folder("root");
        var external = Folder("external");
        File.WriteAllText(Path.Combine(root, "entry"), "original");
        File.WriteAllText(Path.Combine(external, "entry"), "external");
        using var db = Open("inventory", root);
        ScanHash(db);
        var entry = db.GetFileEntry("r", "entry")!;
        File.Delete(Path.Combine(root, "entry"));
        File.CreateSymbolicLink(Path.Combine(root, "entry"), Path.Combine(external, "entry"));
        Assert.Equal((0, 1, 0), new Scanner(db).HashNeeded("r", false, 1));
        Assert.Equal(HashState.Stale, db.GetHash(entry.Id, "sha256")!.State);
    }

    [Fact]
    public void Mft_Emits_Directory_Links_And_Omits_All_Linked_Descendants()
    {
        var volume = Path.GetPathRoot(_dir)!;
        var root = Path.Combine(volume, "inventory");
        NtfsMftEnumerator.ParsedFileRecord Entry(string name, ulong parent, bool directory = false, bool link = false) =>
            new(name, parent, 3, DateTime.UtcNow, DateTime.UtcNow, directory, link);
        (ulong Frn, NtfsMftEnumerator.ParsedFileRecord? Parsed)[] records =
        [
            (20, Entry("inventory", 5, true)), (21, Entry("linked", 20, true, true)),
            (22, Entry("descendant.txt", 21)), (23, Entry("file.txt", 20)),
            (24, Entry("file-link", 20, false, true)), (25, Entry("nested", 21, true)),
            (26, Entry("hidden.txt", 25)), (27, Entry("nested-link", 25, true, true))
        ];
        var entries = NtfsMftEnumerator.EnumerateEntries(volume, root, () => records).ToList();
        Assert.Equal(new[] { "file-link", "file.txt", "linked" }, entries.Select(e => e.Name).Order().ToArray());
        Assert.True(entries.Single(e => e.Name == "linked").IsDirectory);
    }

    [SymlinkFact]
    public void Synthetic_Mft_And_Recursive_Scans_Retain_The_Same_Link_Metadata()
    {
        var root = Folder("root");
        File.WriteAllText(Path.Combine(root, "file"), "content");
        File.CreateSymbolicLink(Path.Combine(root, "file-link"), "file");
        Directory.CreateSymbolicLink(Path.Combine(root, "dir-link"), root);
        using var recursive = Open("recursive", root);
        using var mft = Open("mft", root);
        new Scanner(recursive).ScanRoot("r");
        var synthetic = new Scanner(mft, _ => recursive.ListFiles().Select(f => new FsEntry(
            Path.Combine(root, f.RelativePath), f.EntryKind == EntryKind.DirectoryLink, f.Size,
            DateTime.Parse(f.ModifiedUtc), DateTime.UtcNow, true, f.EntryKind != EntryKind.File, null)));
        Assert.Equal((3, 0), synthetic.ScanRoot("r"));
        Assert.Equal(recursive.ListFiles().Select(f => (f.RelativePath, f.EntryKind, f.LinkTarget, f.TargetPath)),
            mft.ListFiles().Select(f => (f.RelativePath, f.EntryKind, f.LinkTarget, f.TargetPath)));
    }

    [ElevatedMftFact]
    public void Real_Mft_And_Recursive_Scans_Agree_On_Links()
    {
        var root = Folder("root");
        File.WriteAllText(Path.Combine(root, "file"), "content");
        File.CreateSymbolicLink(Path.Combine(root, "file-link"), "file");
        Directory.CreateSymbolicLink(Path.Combine(root, "dir-link"), root);
        using var recursive = Open("recursive", root);
        using var mft = Open("mft", root);
        Assert.Equal((3, 0), new Scanner(recursive).ScanRoot("r"));
        Assert.Equal((3, 0), new Scanner(mft, mftMode: "require").ScanRoot("r"));
        Assert.Equal(recursive.ListFiles().Select(f => (f.RelativePath, f.EntryKind, f.LinkTarget)),
            mft.ListFiles().Select(f => (f.RelativePath, f.EntryKind, f.LinkTarget)));
    }

    [Fact]
    public void Legacy_Read_Only_Inventories_Work_And_Writable_Upgrade_Preserves_Scan_History()
    {
        var path = Path.Combine(_dir, "legacy.db");
        var options = new DbContextOptionsBuilder<BackupNormalizerDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        using (var old = new BackupNormalizerDbContext(options))
        {
            old.GetService<IMigrator>().Migrate("20260929221047_BindExecutionRoots");
            old.Database.ExecuteSqlRaw("INSERT INTO StorageRoot (Id, Name, Path, Writable, FileSystemId, CaseSensitivity, CreatedUtc) VALUES ('r', 'r', '/offline', 0, 'fs', 'sensitive', 'before')");
            old.Database.ExecuteSqlRaw("INSERT INTO Scan (Id, StorageRootId, StartedUtc, Status) VALUES (1, 'r', 'before', 'Incomplete')");
            old.Database.ExecuteSqlRaw("INSERT INTO FileEntry (Id, StorageRootId, RelativePath, Name, Size, ModifiedUtc, LastSeenScanId, Status, Error) VALUES (1, 'r', 'link', 'link', 0, 'before', 1, 'UnsupportedEntry', 'symlink')");
            old.Database.ExecuteSqlRaw("INSERT INTO FileHash (FileEntryId, Algorithm, Digest, SizeAtHash, ModifiedUtcAtHash, CalculatedUtc, State) VALUES (1, 'sha256', 'oldhash', 0, 'before', 'before', 'Ok')");
        }
        var bytes = File.ReadAllBytes(path);
        using (var readOnly = Database.OpenReadOnly(path, false))
        {
            var entry = Assert.Single(readOnly.ListFiles());
            Assert.Equal(EntryKind.ReparsePoint, entry.EntryKind);
            Assert.Equal(FileStatus.Ok, entry.Status);
            Assert.Null(readOnly.ListFilesWithHashes("r", "sha256").Single().Digest);
            Assert.Empty(Matcher.LoadFromDb(readOnly, "sha256", "r"));
        }
        var node = InventorySnapshot.Load(path).Roots.Single().Nodes["link"];
        Assert.True(node.IsLink);
        Assert.False(node.HasScanError);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        using var upgraded = new Database(path);
        Assert.Equal(4, upgraded.AppliedMigrations().Count);
        Assert.Equal(ScanStatus.Incomplete, upgraded.LatestScanStatus("r"));
        Assert.Equal(FileStatus.Ok, upgraded.GetFileEntry("r", "link")!.Status);
        Assert.Equal(EntryKind.ReparsePoint, upgraded.GetFileEntry("r", "link")!.EntryKind);
        Assert.Equal(HashState.Stale, upgraded.GetHash(1, "sha256")!.State);
    }

    [SymlinkFact]
    public void Planning_Exports_Skips_Preserves_Source_Link_Counterparts_And_Processes_Other_Files()
    {
        var sourceRoot = Folder("source");
        var targetRoot = Folder("target");
        var external = Folder("external");
        File.WriteAllText(Path.Combine(external, "untouched"), "external");
        File.WriteAllText(Path.Combine(sourceRoot, "normal"), "content");
        File.WriteAllText(Path.Combine(sourceRoot, "blocked"), "content");
        Directory.CreateDirectory(Path.Combine(sourceRoot, "dir"));
        File.WriteAllText(Path.Combine(sourceRoot, "dir", "child"), "content");
        File.CreateSymbolicLink(Path.Combine(sourceRoot, "preserved"), "missing");
        File.WriteAllText(Path.Combine(targetRoot, "preserved"), "content");
        File.CreateSymbolicLink(Path.Combine(targetRoot, "blocked"), Path.Combine(external, "untouched"));
        Directory.CreateSymbolicLink(Path.Combine(targetRoot, "dir"), external);
        using var source = Open("source", sourceRoot);
        using var target = Open("target", targetRoot);
        ScanHash(source); ScanHash(target);
        var planner = new Planner(target);
        var result = planner.PlanFromRoots(source, "r", "r", "plan");
        Assert.Equal(1, result.Copy);
        Assert.Equal(0, result.Trash);
        Assert.Equal(5, result.SkippedLinks);
        var doc = planner.ExportPlan("plan");
        var json = Path.Combine(_dir, "plan.json");
        File.WriteAllText(json, Planner.ToJson(doc));
        var imported = PlanStaging.ImportJson(json);
        Assert.All(imported.Operations.Where(op => op.Type == OpType.SkipLink), op => Assert.NotNull(op.SkipReason));
        using var replay = Open("replay", targetRoot);
        PlanStaging.WriteToDatabase(replay, imported with { PlanId = "replay" }, "r", targetRoot);
        var summary = new Executor(replay).Execute("replay");
        Assert.Equal(0, summary.Failed);
        Assert.Equal(0, summary.Conflicts);
        Assert.Equal(5, summary.Skipped);
        Assert.Equal("content", File.ReadAllText(Path.Combine(targetRoot, "normal")));
        Assert.Equal("content", File.ReadAllText(Path.Combine(targetRoot, "preserved")));
        Assert.Equal("external", File.ReadAllText(Path.Combine(external, "untouched")));
        Assert.False(File.Exists(Path.Combine(external, "child")));
        Assert.All(replay.ListPlanOperations("replay").Where(op => op.Type == OpType.SkipLink), op =>
        { Assert.Equal(OpStatus.Skipped, op.Status); Assert.Null(op.Error); Assert.NotNull(op.SkipReason); });
    }

    [SymlinkFact]
    public void Manual_Staging_Skips_File_Links_Directory_Links_And_Linked_Destinations()
    {
        var root = Folder("root");
        var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
        var destination = Path.Combine(root, "destination"); Directory.CreateDirectory(destination);
        var external = Folder("external");
        File.WriteAllText(Path.Combine(source, "regular"), "bytes");
        File.CreateSymbolicLink(Path.Combine(source, "link"), "missing");
        Directory.CreateSymbolicLink(Path.Combine(source, "linked-dir"), external);
        var copy = PlanStaging.StageCopy(root, source, destination);
        Assert.Single(copy, op => op.Type == OpType.Copy);
        Assert.Equal(2, copy.Count(op => op.Type == OpType.SkipLink));
        Assert.Equal(OpType.SkipLink, PlanStaging.StageTrash(root, Path.Combine(source, "link")).Single().Type);
        Assert.Equal(OpType.SkipLink, PlanStaging.StageMove(root, Path.Combine(source, "linked-dir"), destination).Single().Type);
        Directory.CreateSymbolicLink(Path.Combine(root, "dest-link"), external);
        Assert.Equal(OpType.SkipLink, PlanStaging.StageCopy(root, Path.Combine(source, "regular"), Path.Combine(root, "dest-link")).Single().Type);
        Assert.Equal(OpType.SkipLink, PlanStaging.StageMkdir(root, Path.Combine(root, "dest-link", "new")).Single().Type);
        Assert.False(File.Exists(Path.Combine(external, "regular")));
    }

    [SymlinkFact]
    public void Executor_Skips_Links_Introduced_After_Planning_And_Continues()
    {
        var root = Folder("root");
        var external = Folder("external");
        File.WriteAllText(Path.Combine(root, "source"), "content");
        File.WriteAllText(Path.Combine(external, "outside"), "external");
        var staged = new[]
        {
            new PlanStaging.StagedOp(OpType.Copy, "source", "parent/blocked", 7, null),
            new PlanStaging.StagedOp(OpType.Copy, "source", "leaf-link", 7, null),
            new PlanStaging.StagedOp(OpType.Copy, "source-link", "from-link", 7, null),
            new PlanStaging.StagedOp(OpType.Mkdir, "", "parent/new-dir", 0, null),
            new PlanStaging.StagedOp(OpType.Copy, "source", "copied", 7, null),
        };
        using var db = Open("inventory", root);
        PlanStaging.WriteToDatabase(db, PlanStaging.BuildPlanDoc("plan", "r", root, staged), "r", root);
        Directory.CreateSymbolicLink(Path.Combine(root, "parent"), external);
        File.CreateSymbolicLink(Path.Combine(root, "leaf-link"), Path.Combine(external, "outside"));
        File.CreateSymbolicLink(Path.Combine(root, "source-link"), Path.Combine(external, "outside"));
        var summary = new Executor(db).Execute("plan", stopOnError: true);
        Assert.Equal(new Executor.ExecSummary(1, 0, 4, 0), summary);
        Assert.Equal("content", File.ReadAllText(Path.Combine(root, "copied")));
        Assert.Equal("external", File.ReadAllText(Path.Combine(external, "outside")));
        Assert.Equal(new[] { "outside" }, Directory.GetFiles(external).Select(Path.GetFileName));
        Assert.False(Directory.Exists(Path.Combine(external, "new-dir")));
    }

    [SymlinkFact]
    public void Linked_Survivors_Do_Not_Allow_Trash()
    {
        var root = Folder("root");
        File.WriteAllText(Path.Combine(root, "source"), "same");
        File.WriteAllText(Path.Combine(root, "survivor"), "same");
        using var db = Open("inventory", root);
        ScanHash(db);
        var source = db.GetFileEntry("r", "source")!;
        var hash = db.GetHash(source.Id, "sha256")!.Digest;
        File.Delete(Path.Combine(root, "survivor"));
        File.CreateSymbolicLink(Path.Combine(root, "survivor"), "source");
        var doc = PlanStaging.BuildPlanDoc("plan", "r", root,
            [new PlanStaging.StagedOp(OpType.Trash, "source", null, source.Size, hash)]);
        PlanStaging.WriteToDatabase(db, doc, "r", root);
        Assert.Equal(1, new Executor(db).Execute("plan").Conflicts);
        Assert.Equal("same", File.ReadAllText(Path.Combine(root, "source")));
    }

    [Fact]
    public void Inventory_Links_Are_Skipped_Displayed_And_Not_Navigable()
    {
        using var left = Open("left", Folder("left-root"));
        using var right = Open("right", Folder("right-root"));
        foreach (var db in new[] { left, right })
        {
            var scan = db.BeginScan("r");
            db.UpsertFileEntry(new FileEntryRow(0, "r", "dir-link", "dir-link", 0, Database.UtcNow(), null, null,
                scan, FileStatus.Ok, null, EntryKind.DirectoryLink, "../target", "/target", "Target unavailable."));
            db.FinishScan(scan, ScanStatus.Completed);
        }
        left.UpsertFileEntry(new FileEntryRow(0, "r", "only-link", "only-link", 0, Database.UtcNow(), null, null,
            1, FileStatus.Ok, null, EntryKind.FileLink, "missing"));
        left.UpsertFileEntry(new FileEntryRow(0, "r", "links-only/nested/link", "link", 0, Database.UtcNow(), null, null,
            1, FileStatus.Ok, null, EntryKind.FileLink, "missing"));
        left.UpsertFileEntry(new FileEntryRow(0, "r", "collision", "collision", 0, Database.UtcNow(), null, null,
            1, FileStatus.Ok, null, EntryKind.FileLink, "missing"));
        right.UpsertFileEntry(new FileEntryRow(0, "r", "collision", "collision", 3, Database.UtcNow(), null, null,
            1, FileStatus.Ok, null));
        var leftSnapshot = InventorySnapshot.Load(left.DbPath);
        var rightSnapshot = InventorySnapshot.Load(right.DbPath);
        var comparison = InventoryComparison.Compare(leftSnapshot.Roots[0], "", rightSnapshot.Roots[0], "");
        Assert.Equal(ComparisonState.Skipped, comparison.Left["dir-link"]);
        Assert.Equal(ComparisonState.Skipped, comparison.Left["only-link"]);
        Assert.Equal(ComparisonState.Skipped, comparison.Left["links-only"]);
        Assert.Equal(ComparisonState.TypeConflict, comparison.Left["collision"]);
        Assert.Contains("Scan errors: 0", comparison.Summary);
        Assert.DoesNotContain("Warning", comparison.Summary);
        var panel = new FilePanelViewModel();
        panel.LoadSnapshot(leftSnapshot);
        var directoryLink = panel.Entries.Single(e => e.Name == "dir-link");
        Assert.Equal("directory link", directoryLink.KindText);
        Assert.Contains("../target", directoryLink.Tooltip);
        Assert.Contains("Target unavailable", directoryLink.Tooltip);
        Assert.False(panel.NavigateTo(directoryLink));
        panel.NavigateInventory("dir-link");
        Assert.Equal("", panel.InventoryPath);
        panel.ApplyComparison(comparison.Left, true);
        Assert.Single(panel.Entries);
        Assert.Equal("collision", panel.Entries[0].Name);
        var diff = Inventory.Diff(left.DbPath, "r", right.DbPath, "r");
        Assert.Equal(0, diff.TargetOnly);
        Assert.Equal(1, diff.LinkConflicts);
    }

    [Fact]
    public void Older_Plan_Json_Without_SkipReason_Still_Imports()
    {
        var json = Path.Combine(_dir, "old.json");
        File.WriteAllText(json, """
            {"planId":"old","createdUtc":"before","estimatedBytesCopied":0,
             "sourceRoot":"r","sourcePath":"/root","targetRoot":"r","targetPath":"/root",
             "operations":[{"id":1,"type":"MKDIR","destinationRoot":"r","destinationPath":"new","expectedSize":0}]}
            """);
        Assert.Null(PlanStaging.ImportJson(json).Operations.Single().SkipReason);
    }

    private void Junction(string path, string target)
    {
        var script = Path.Combine(_dir, "junction.ps1");
        File.WriteAllText(script, "param([string]$LinkPath, [string]$TargetPath)\n$ErrorActionPreference = 'Stop'\nNew-Item -ItemType Junction -Path $LinkPath -Target $TargetPath | Out-Null\n");
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardError = true, RedirectStandardOutput = true,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
            "-LinkPath", path, "-TargetPath", target }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30000), "Junction creation timed out.");
        Assert.True(process.ExitCode == 0, errors);
        _junctions.Add(path);
    }

    [JunctionFact]
    public void Real_Junctions_Include_Broken_And_Cyclic_Targets_Without_Traversal()
    {
        var root = Folder("root");
        var external = Folder("external");
        var missing = Folder("will-disappear");
        File.WriteAllText(Path.Combine(root, "regular"), "inside");
        File.WriteAllText(Path.Combine(external, "outside"), "external");
        Junction(Path.Combine(root, "link"), external);
        Junction(Path.Combine(root, "cycle"), root);
        Junction(Path.Combine(root, "broken"), missing);
        Directory.Delete(missing);
        using var db = Open("inventory", root);
        var scanner = new Scanner(db);
        Assert.Equal((4, 0), scanner.ScanRoot("r"));
        Assert.Equal(ScanStatus.Completed, db.LatestScanStatus("r"));
        Assert.Equal((1, 3, 0), scanner.HashNeeded("r", true, 1));
        var link = db.GetFileEntry("r", "link")!;
        Assert.Equal(EntryKind.DirectoryLink, link.EntryKind);
        Assert.Equal(external, link.LinkTarget);
        Assert.Equal(external, link.TargetPath);
        Assert.NotNull(db.GetFileEntry("r", "broken")!.LinkNote);
        Assert.All(db.ListFiles().Where(f => f.EntryKind != EntryKind.File), f =>
        { Assert.Equal(FileStatus.Ok, f.Status); Assert.Equal(0, f.Size); Assert.Null(f.Error); });
        var synthetic = new Scanner(db, _ => db.ListFiles().Select(f => new FsEntry(Path.Combine(root, f.RelativePath),
            f.EntryKind == EntryKind.DirectoryLink, f.Size, DateTime.Parse(f.ModifiedUtc), DateTime.UtcNow, true,
            f.EntryKind != EntryKind.File, null)).ToArray());
        Assert.Equal((4, 0), synthetic.ScanRoot("r"));
        Assert.DoesNotContain(db.ListFiles(), f => f.RelativePath.Contains('/'));
        Assert.Single(Matcher.LoadFromDb(db, "sha256", "r"));
    }

    [JunctionFact]
    public void Junctions_Skip_Staging_Hashing_And_Execution_Through_Linked_Parents()
    {
        var root = Folder("root");
        var external = Folder("external");
        File.WriteAllText(Path.Combine(root, "source"), "content");
        var original = Path.Combine(root, "parent"); Directory.CreateDirectory(original);
        File.WriteAllText(Path.Combine(original, "child"), "original");
        File.WriteAllText(Path.Combine(external, "child"), "external");
        using var db = Open("inventory", root);
        ScanHash(db);
        var child = db.GetFileEntry("r", "parent/child")!;
        var staged = new[]
        {
            new PlanStaging.StagedOp(OpType.Copy, "source", "parent/new", 7, null),
            new PlanStaging.StagedOp(OpType.Move, "parent/child", "moved", 8, null),
            new PlanStaging.StagedOp(OpType.Mkdir, "", "parent/new-dir", 0, null),
            new PlanStaging.StagedOp(OpType.Copy, "source", "copied", 7, null),
        };
        PlanStaging.WriteToDatabase(db, PlanStaging.BuildPlanDoc("plan", "r", root, staged), "r", root);
        File.Delete(Path.Combine(original, "child")); Directory.Delete(original);
        Junction(original, external);
        Assert.Equal((0, 2, 0), new Scanner(db).HashNeeded("r", false, 1));
        Assert.Equal(HashState.Stale, db.GetHash(child.Id, "sha256")!.State);
        Assert.Equal(OpType.SkipLink, PlanStaging.StageCopy(root, Path.Combine(root, "source"), original).Single().Type);
        Assert.Equal(OpType.SkipLink, PlanStaging.StageTrash(root, original).Single().Type);
        Assert.Equal(new Executor.ExecSummary(1, 0, 3, 0), new Executor(db).Execute("plan", stopOnError: true));
        Assert.Equal("content", File.ReadAllText(Path.Combine(root, "copied")));
        Assert.Equal("external", File.ReadAllText(Path.Combine(external, "child")));
        Assert.False(File.Exists(Path.Combine(external, "new")));
        Assert.False(Directory.Exists(Path.Combine(external, "new-dir")));
    }

    [Fact]
    public void Link_Metadata_Alone_Produces_Exportable_Skips_And_Protects_Target_Counterparts()
    {
        var sourceRoot = Folder("source"); var targetRoot = Folder("target");
        File.WriteAllText(Path.Combine(sourceRoot, "normal"), "content");
        File.WriteAllText(Path.Combine(sourceRoot, "blocked"), "content");
        File.WriteAllText(Path.Combine(sourceRoot, "preserved"), "content");
        Directory.CreateDirectory(Path.Combine(sourceRoot, "dir"));
        File.WriteAllText(Path.Combine(sourceRoot, "dir", "child"), "content");
        File.WriteAllText(Path.Combine(targetRoot, "blocked"), "external");
        File.WriteAllText(Path.Combine(targetRoot, "preserved"), "content");
        using var source = Open("source", sourceRoot); using var target = Open("target", targetRoot);
        ScanHash(source); ScanHash(target);
        source.UpsertFileEntry(source.GetFileEntry("r", "preserved")! with { EntryKind = EntryKind.FileLink, LinkTarget = "missing", Size = 0 });
        target.UpsertFileEntry(target.GetFileEntry("r", "blocked")! with { EntryKind = EntryKind.FileLink, LinkTarget = "outside", Size = 0 });
        target.UpsertFileEntry(new FileEntryRow(0, "r", "dir", "dir", 0, Database.UtcNow(), null, null, 1,
            FileStatus.Ok, null, EntryKind.DirectoryLink, "outside"));
        var planner = new Planner(target);
        var result = planner.PlanFromRoots(source, "r", "r", "plan");
        Assert.Equal(1, result.Copy); Assert.Equal(0, result.Trash); Assert.Equal(5, result.SkippedLinks);
        var path = Path.Combine(_dir, "plan.json"); File.WriteAllText(path, Planner.ToJson(planner.ExportPlan("plan")));
        using var replay = Open("replay", targetRoot);
        PlanStaging.WriteToDatabase(replay, PlanStaging.ImportJson(path) with { PlanId = "replay" }, "r", targetRoot);
        Assert.Equal(new Executor.ExecSummary(1, 0, 5, 0), new Executor(replay).Execute("replay"));
        Assert.Equal("content", File.ReadAllText(Path.Combine(targetRoot, "normal")));
        Assert.Equal("content", File.ReadAllText(Path.Combine(targetRoot, "preserved")));
        Assert.Equal("external", File.ReadAllText(Path.Combine(targetRoot, "blocked")));
        Assert.False(Directory.Exists(Path.Combine(targetRoot, "dir")));
        Assert.All(replay.ListPlanOperations("replay").Where(op => op.Type == OpType.SkipLink), op =>
        { Assert.Equal(OpStatus.Skipped, op.Status); Assert.NotNull(op.SkipReason); Assert.Null(op.Error); });
    }

    [Fact]
    public void Entry_Kind_Transitions_Invalidate_All_Hashes_Even_When_Content_Metadata_Is_Unchanged()
    {
        var root = Folder("root"); File.WriteAllText(Path.Combine(root, "file"), "same");
        using var db = Open("inventory", root); ScanHash(db);
        var entry = db.GetFileEntry("r", "file")!;
        db.UpsertHash(new FileHashRow(entry.Id, "other", "old", entry.Size, entry.ModifiedUtc, Database.UtcNow(), HashState.Ok));
        db.UpsertFileEntry(entry with { EntryKind = EntryKind.FileLink, LinkTarget = "missing" });
        Assert.Equal((0, 1, 0), new Scanner(db).HashNeeded("r", false, 1));
        Assert.Equal(HashState.Stale, db.GetHash(entry.Id, "sha256")!.State);
        Assert.Equal(HashState.Stale, db.GetHash(entry.Id, "other")!.State);
        Assert.Empty(db.ListContentCopies("r", "other-root", "other-file", "old"));
        db.UpsertFileEntry(entry);
        Assert.Equal((1, 0, 0), new Scanner(db).HashNeeded("r", false, 1));
    }

    [JunctionFact]
    public void A_Survivor_Beneath_A_New_Junction_Cannot_Authorize_Trash()
    {
        var root = Folder("root"); var external = Folder("external");
        var parent = Path.Combine(root, "parent"); Directory.CreateDirectory(parent);
        File.WriteAllText(Path.Combine(root, "source"), "same");
        File.WriteAllText(Path.Combine(parent, "child"), "same");
        File.WriteAllText(Path.Combine(external, "child"), "same");
        using var db = Open("inventory", root); ScanHash(db);
        var source = db.GetFileEntry("r", "source")!;
        var hash = db.GetHash(source.Id, "sha256")!.Digest;
        File.Delete(Path.Combine(parent, "child")); Directory.Delete(parent);
        Junction(parent, external);
        var doc = PlanStaging.BuildPlanDoc("plan", "r", root,
            [new PlanStaging.StagedOp(OpType.Trash, "source", null, source.Size, hash)]);
        PlanStaging.WriteToDatabase(db, doc, "r", root);
        Assert.Equal(1, new Executor(db).Execute("plan").Conflicts);
        Assert.Equal("same", File.ReadAllText(Path.Combine(root, "source")));
        Assert.Equal("same", File.ReadAllText(Path.Combine(external, "child")));
    }

    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}
