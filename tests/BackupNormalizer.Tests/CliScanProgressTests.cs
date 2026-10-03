using BackupNormalizer;
using Microsoft.Data.Sqlite;

namespace BackupNormalizer.Tests;

[Collection("Console")]
public sealed class CliScanProgressTests
{
    [Fact]
    public void Full_Retry_Progress_Can_Restart_Its_Elapsed_Clock()
    {
        using var output = new StringWriter();
        var renderer = new Cli.ScanProgressRenderer("d", 100, output, () => 200);
        renderer.Report(new ScanProgress("d", 1, 0, "path", TimeSpan.FromSeconds(10), Incremental: true));
        renderer.Report(new ScanProgress("d", 64, 0, "path", TimeSpan.FromSeconds(1)));
        Assert.Contains("scan d: 64 entries", output.ToString());
    }

    [Fact]
    public void Cli_Help_Explains_Incremental_And_Full_Scan_Controls()
    {
        var original = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Assert.Equal(0, Cli.Run(["--help"]));
            Assert.Contains("--usn auto|off", output.ToString());
            Assert.Contains("--full forces full enumeration", output.ToString());
        }
        finally { Console.SetOut(original); }
    }

    [Fact]
    public void Incremental_Progress_Does_Not_Compare_Changes_With_Full_Inventory_Size()
    {
        using var output = new StringWriter();
        var renderer = new Cli.ScanProgressRenderer("d", 176440, output, () => 200);
        renderer.Report(new ScanProgress("d", 3, 0, "path", TimeSpan.FromSeconds(1), Incremental: true));
        Assert.Contains("3 entries refreshed using USN", output.ToString());
        Assert.DoesNotContain("%", output.ToString());
        Assert.DoesNotContain("176", output.ToString());
    }

    private static ScanProgress Snapshot(int count = 64, int milliseconds = 0, string path = @"D:\example\folder")
        => new("d", count, 0, path, TimeSpan.FromMilliseconds(milliseconds));

    [Theory]
    [InlineData(20)]
    [InlineData(80)]
    [InlineData(120)]
    public void Progress_Fits_The_Terminal_Without_Reaching_The_Last_Column(int columns)
    {
        using var output = new StringWriter();
        var renderer = new Cli.ScanProgressRenderer("d", 176440, output, () => columns);

        renderer.Report(Snapshot(175508, 150000, @"D:\a-long-directory-name\another-long-directory-name"));

        string text = output.ToString();
        Assert.StartsWith("\rscan d:", text);
        Assert.InRange(text.Length - 1, 1, columns - 1);
        Assert.DoesNotContain('\n', text);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Unknown_Width_Uses_A_Bounded_Fallback(int columns)
    {
        using var output = new StringWriter();
        var renderer = new Cli.ScanProgressRenderer(new string('x', 150), 0, output, () => columns);

        renderer.Report(Snapshot());

        Assert.Equal(121, output.ToString().Length);
        Assert.StartsWith("\rscan ", output.ToString());
    }

    [Fact]
    public void Labels_And_Paths_Cannot_Emit_Terminal_Control_Characters()
    {
        using var output = new StringWriter();
        var renderer = new Cli.ScanProgressRenderer("d\a\r\n\u001b]9;4;", 0, output, () => 200);

        renderer.Report(Snapshot(path: "D:\\folder\a\b\t\r\n\u0085"));

        string text = output.ToString();
        Assert.StartsWith("\rscan d", text);
        Assert.All(text[1..], c => Assert.False(char.IsControl(c)));
    }

    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(35)]
    public void Wide_Labels_Do_Not_Wrap_Or_Split_Surrogate_Pairs(int columns)
    {
        using var output = new StringWriter();
        var renderer = new Cli.ScanProgressRenderer("漢😀", 0, output, () => columns);

        renderer.Report(Snapshot());

        var runes = output.ToString()[1..].EnumerateRunes().ToArray();
        Assert.DoesNotContain(runes, r => r.Value == 0xfffd);
        int cells = runes.Sum(r => r.Value == '漢' || r.Value == 0x1f600 ? 2 : 1);
        Assert.InRange(cells, 1, columns - 1);
    }

    [Fact]
    public void Rapid_Updates_Are_Throttled_And_Finish_Shows_The_Latest_Count()
    {
        using var output = new StringWriter();
        var renderer = new Cli.ScanProgressRenderer("d", 0, output, () => 200);

        renderer.Report(Snapshot(64, 0));
        renderer.Report(Snapshot(128, 1));
        renderer.Report(Snapshot(192, 199));
        Assert.Single(output.ToString().Split('\r', StringSplitOptions.RemoveEmptyEntries));
        renderer.Report(Snapshot(256, 200));
        renderer.Report(Snapshot(320, 201));
        renderer.Finish();

        var updates = output.ToString().Replace(Environment.NewLine, "").Split('\r', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, updates.Length);
        Assert.StartsWith("scan d: 256 entries", updates[1]);
        Assert.StartsWith("scan d: 320 entries", updates[2]);
        Assert.EndsWith(Environment.NewLine, output.ToString());

        string finished = output.ToString();
        renderer.Report(Snapshot(384, 1000));
        renderer.Finish();
        Assert.Equal(finished, output.ToString());
    }

    [Fact]
    public void Shorter_Updates_Clear_The_Previous_Path()
    {
        using var output = new StringWriter();
        var renderer = new Cli.ScanProgressRenderer("d", 0, output, () => 200);

        renderer.Report(Snapshot(count: 0, path: @"D:\example\folder"));
        renderer.Report(Snapshot(count: 0, milliseconds: 200, path: ""));

        var updates = output.ToString().Split('\r', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, updates.Length);
        Assert.Equal(updates[0].Length, updates[1].Length);
        Assert.DoesNotContain("folder", updates[1]);
        Assert.EndsWith(new string(' ', @"D:\example\folder".Length), updates[1]);
    }

    [Fact]
    public void Terminal_Resize_Also_Bounds_Clearing_The_Previous_Line()
    {
        using var output = new StringWriter();
        int columns = 200;
        var renderer = new Cli.ScanProgressRenderer("d", 0, output, () => columns);

        renderer.Report(Snapshot());
        columns = 30;
        renderer.Report(Snapshot(milliseconds: 200));

        var updates = output.ToString().Split('\r', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, updates.Length);
        Assert.True(updates[0].Length > columns);
        Assert.InRange(updates[1].Length, 1, columns - 1);
    }

    private sealed class FailingOutput : StringWriter
    {
        public override void Write(string? value) => throw new IOException("Terminal is unavailable.");
        public override void WriteLine() => throw new IOException("Terminal is unavailable.");
    }

    [Fact]
    public void Output_Failures_Do_Not_Fail_The_Scan_Observer()
    {
        using var output = new FailingOutput();
        var renderer = new Cli.ScanProgressRenderer("d", 0, output, () => 80);
        renderer.Report(Snapshot());
        renderer.Finish();

        using var captured = new StringWriter();
        var unknownTerminal = new Cli.ScanProgressRenderer("d", 0, captured,
            () => throw new IOException("Terminal width is unavailable."));
        unknownTerminal.Report(Snapshot());
        unknownTerminal.Finish();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Summary_Only_Scanning_Still_Completes_And_Updates_The_Inventory(bool noProgress)
    {
        string directory = Path.Combine(Path.GetTempPath(), "bn-scan-progress-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(directory, "data");
        string dbPath = Path.Combine(directory, "inventory.db");
        Directory.CreateDirectory(root);
        var original = Console.Out;
        try
        {
            File.WriteAllText(Path.Combine(root, "file.txt"), "content");
            using (var db = new Database(dbPath))
                db.UpsertRoot(new StorageRootRow("d", "d", root, true, "fs", "unknown", Database.UtcNow()));
            using var output = new StringWriter();
            Console.SetOut(output);
            string[] args = noProgress
                ? ["scan", "d", "--db", dbPath, "--mft", "off", "--no-progress"]
                : ["scan", "d", "--db", dbPath, "--mft", "off"];

            Assert.Equal(0, Cli.Run(args));
            Assert.Equal($"scan d: 1 entries, 0 errors (complete){Environment.NewLine}", output.ToString());
            using var inventory = Database.OpenReadOnly(dbPath, pooling: false);
            Assert.Equal(ScanStatus.Completed, inventory.LatestScanStatus("d"));
            Assert.Single(inventory.ListFiles("d"));
        }
        finally
        {
            Console.SetOut(original);
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate, ForeignKeys = true
            }.ToString());
            SqliteConnection.ClearPool(connection);
            Directory.Delete(directory, true);
        }
    }
}
