using System.Security.Cryptography;
using BackupNormalizer;
using Microsoft.Data.Sqlite;

namespace BackupNormalizer.Tests;

[Collection("Console")]
public sealed class HashProgressTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "bn-hash-progress-" + Guid.NewGuid().ToString("N")
    );
    private readonly string _root;
    private readonly string _dbPath;

    public HashProgressTests()
    {
        _root = Path.Combine(_dir, "data");
        _dbPath = Path.Combine(_dir, "inventory.db");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                ForeignKeys = true,
            }.ToString()
        );
        SqliteConnection.ClearPool(connection);
        Directory.Delete(_dir, true);
    }

    private sealed class InlineProgress(Action<HashProgress> report) : IProgress<HashProgress>
    {
        public void Report(HashProgress value) => report(value);
    }

    private Database Scan()
    {
        var db = new Database(_dbPath);
        db.UpsertRoot(
            new StorageRootRow("r", "r", _root, true, "fs", "unknown", Database.UtcNow())
        );
        Assert.Equal(0, new Scanner(db).ScanRoot("r").errors);
        return db;
    }

    [Fact]
    public void Stream_Reports_Chunks_Without_Changing_The_Digest()
    {
        var bytes = new byte[9 * 1024 * 1024 + 7];
        Random.Shared.NextBytes(bytes);
        using var stream = new MemoryStream(bytes);
        var chunks = new List<long>();

        string digest = new Sha256Hasher().HashStream(
            stream,
            count =>
            {
                chunks.Add(count);
                Assert.InRange(stream.Position, 1, stream.Length);
            }
        );

        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), digest);
        Assert.True(chunks.Count > 1);
        Assert.Equal(bytes.LongLength, chunks.Sum());
        Assert.All(chunks, count => Assert.InRange(count, 1, 4 * 1024 * 1024));
    }

    [Fact]
    public void Parallel_Hashing_Reports_Mid_File_Updates_And_Ordered_Totals()
    {
        var bytes = new byte[8 * 1024 * 1024 + 17];
        Random.Shared.NextBytes(bytes);
        for (int i = 0; i < 3; i++)
        {
            File.WriteAllBytes(Path.Combine(_root, $"{i}.bin"), bytes);
        }

        using var db = Scan();
        var snapshots = new List<HashProgress>();

        var result = new Scanner(db).HashNeeded("r", true, 3, new InlineProgress(snapshots.Add));

        Assert.Equal((3, 0, 0), result);
        Assert.Equal(0, snapshots[0].Processed);
        Assert.Equal(0, snapshots[0].BytesRead);
        Assert.Contains(
            snapshots,
            p => p.BytesRead > 0 && p.Processed == 0 && p.CurrentPath.EndsWith(".bin")
        );
        Assert.All(snapshots, p => Assert.Equal(3, p.TotalFiles));
        for (int i = 1; i < snapshots.Count; i++)
        {
            Assert.True(snapshots[i].Processed >= snapshots[i - 1].Processed);
            Assert.True(snapshots[i].BytesRead >= snapshots[i - 1].BytesRead);
            Assert.True(snapshots[i].Elapsed >= snapshots[i - 1].Elapsed);
        }
        Assert.Equal(3, snapshots[^1].Processed);
        Assert.Equal(3 * bytes.LongLength, snapshots[^1].BytesRead);
        string expected = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Assert.All(db.ListFiles(), f => Assert.Equal(expected, db.GetHash(f.Id, "sha256")!.Digest));
    }

    [Fact]
    public void Cached_Missing_And_Unreadable_Files_All_Complete_Progress()
    {
        string cached = Path.Combine(_root, "cached.txt");
        string missing = Path.Combine(_root, "missing.txt");
        string locked = Path.Combine(_root, "locked.txt");
        File.WriteAllText(cached, "cached");
        File.WriteAllText(missing, "missing");
        File.WriteAllText(locked, "locked");
        using var db = Scan();
        var scanner = new Scanner(db);
        Assert.Equal((3, 0, 0), scanner.HashNeeded("r", true, 1));
        File.Delete(missing);
        // Make its cached hash stale so the scanner attempts to read the locked file.
        File.AppendAllText(locked, "changed");
        scanner.ScanRoot("r");
        using var held = new FileStream(
            locked,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None
        );
        var snapshots = new List<HashProgress>();

        var result = scanner.HashNeeded(null, false, 2, new InlineProgress(snapshots.Add));

        Assert.Equal((0, 3, 0), result);
        Assert.Equal(3, snapshots[^1].Processed);
        Assert.Equal(3, snapshots[^1].Skipped);
        Assert.Equal(0, snapshots[^1].BytesRead);
        Assert.Contains(snapshots, p => p.CurrentPath.EndsWith("cached.txt"));
        Assert.Contains(snapshots, p => p.CurrentPath.EndsWith("missing.txt"));
        Assert.Contains(snapshots, p => p.CurrentPath.EndsWith("locked.txt"));
    }

    [Fact]
    public void Empty_Inventory_Reports_Completion()
    {
        using var db = Scan();
        var snapshots = new List<HashProgress>();

        Assert.Equal(
            (0, 0, 0),
            new Scanner(db).HashNeeded("r", progress: new InlineProgress(snapshots.Add))
        );

        Assert.NotEmpty(snapshots);
        Assert.All(
            snapshots,
            p =>
            {
                Assert.Equal(0, p.TotalFiles);
                Assert.Equal(0, p.Processed);
                Assert.Equal(0, p.BytesRead);
            }
        );
    }

    [Fact]
    public void Failing_Observer_Does_Not_Change_Hash_Results()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "content");
        using var db = Scan();

        var result = new Scanner(db).HashNeeded(
            "r",
            true,
            1,
            new InlineProgress(_ => throw new InvalidOperationException("observer failure"))
        );

        Assert.Equal((1, 0, 0), result);
        Assert.Equal(HashState.Ok, db.GetHash(db.ListFiles()[0].Id, "sha256")!.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void No_Progress_Flag_Keeps_Only_The_Cli_Summary(bool needed)
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "content");
        using (Scan()) { }
        var original = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            string[] args = needed
                ? ["hash", "--needed", "--db", _dbPath, "--no-progress"]
                : ["hash", "r", "--all", "--db", _dbPath, "--no-progress"];
            Assert.Equal(0, Cli.Run(args));
        }
        finally
        {
            Console.SetOut(original);
        }

        string label = needed ? "--needed" : "r";
        string skipped = needed ? "reused" : "skipped";
        Assert.Equal(
            $"hash {label}: 1 hashed, 0 {skipped}, 0 unstable{Environment.NewLine}",
            output.ToString()
        );
    }
}
