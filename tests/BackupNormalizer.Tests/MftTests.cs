using System.Text;
using BackupNormalizer;

namespace BackupNormalizer.Tests;

/// <summary>Synthetic MFT record tests. Run everywhere; volume I/O needs Windows.</summary>
public sealed class MftParserTests
{
    private static readonly DateTime SampleTime = new(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private static byte[] BuildRecord(string name, int nameSpace, ulong parent, long size,
        uint nameFlags = 0, ushort headerFlags = 0x01, ushort usn = 0x1234)
    {
        const int recordSize = 1024;
        const int sector = 512;
        var buf = new byte[recordSize];
        void U16(int off, ushort v) { buf[off] = (byte)v; buf[off + 1] = (byte)(v >> 8); }
        void U32(int off, uint v) { BitConverter.GetBytes(v).CopyTo(buf, off); }
        void U64(int off, ulong v) { BitConverter.GetBytes(v).CopyTo(buf, off); }
        void I64(int off, long v) { BitConverter.GetBytes(v).CopyTo(buf, off); }

        U32(0, 0x454C4946); // "FILE"
        U16(4, 42); // USA offset
        U16(6, 3); // USA count: 2 sectors + 1
        U16(0x14, 48); // first attribute
        U16(0x16, headerFlags);
        byte[] nameBytes = Encoding.Unicode.GetBytes(name);
        int valueLen = 66 + nameBytes.Length;
        int attrLen = 24 + valueLen;
        U32(48, 0x30); // $FILE_NAME
        U32(52, (uint)attrLen);
        buf[56] = 0; // resident
        U32(64, (uint)valueLen);
        U16(68, 24); // value offset
        int v = 72;
        U64(v, parent);
        I64(v + 8, SampleTime.ToFileTimeUtc());
        I64(v + 16, SampleTime.ToFileTimeUtc());
        I64(v + 24, SampleTime.ToFileTimeUtc());
        I64(v + 32, SampleTime.ToFileTimeUtc());
        I64(v + 40, size);
        I64(v + 48, size);
        U32(v + 56, nameFlags);
        buf[v + 64] = (byte)name.Length;
        buf[v + 65] = (byte)nameSpace;
        nameBytes.CopyTo(buf, v + 66);
        U32(48 + attrLen, 0xFFFFFFFF); // end marker
        // USA fixup: trailers hold USN, array holds original bytes
        var repl0 = new[] { buf[510], buf[511] };
        var repl1 = new[] { buf[1022], buf[1023] };
        U16(42, usn);
        U16(44, (ushort)(repl0[0] | (repl0[1] << 8)));
        U16(46, (ushort)(repl1[0] | (repl1[1] << 8)));
        U16(510, usn);
        U16(1022, usn);
        _ = sector;
        return buf;
    }

    [Fact]
    public void Parses_File_Record()
    {
        var buf = BuildRecord("photo.jpg", 1, 5, 123456);
        Assert.True(NtfsMftEnumerator.TryParseFileRecord(buf, buf.Length, 512, out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal("photo.jpg", parsed!.Name);
        Assert.Equal(5UL, parsed.ParentRecord);
        Assert.Equal(123456L, parsed.Size);
        Assert.Equal(SampleTime, parsed.ModifiedUtc);
        Assert.False(parsed.IsDirectory);
        Assert.False(parsed.IsReparse);
    }

    [Fact]
    public void Parses_Directory_Record()
    {
        var buf = BuildRecord("Photos", 1, 5, 0, headerFlags: 0x03);
        Assert.True(NtfsMftEnumerator.TryParseFileRecord(buf, buf.Length, 512, out var parsed));
        Assert.True(parsed!.IsDirectory);
    }

    [Fact]
    public void Detects_Reparse_Point()
    {
        var buf = BuildRecord("link", 1, 5, 0, nameFlags: 0x400);
        Assert.True(NtfsMftEnumerator.TryParseFileRecord(buf, buf.Length, 512, out var parsed));
        Assert.True(parsed!.IsReparse);
    }

    [Fact]
    public void Prefers_Win32_Name_Over_Dos_Name()
    {
        const int recordSize = 1024;
        var buf = BuildRecord("LONGNA~1.TXT", 2, 5, 10);
        // Append a second $FILE_NAME attr with Win32 namespace before the end marker.
        byte[] nameBytes = Encoding.Unicode.GetBytes("longname.txt");
        int valueLen = 66 + nameBytes.Length;
        int attrLen = 24 + valueLen;
        int firstLen = 24 + 66 + "LONGNA~1.TXT".Length * 2;
        int off = 48 + firstLen;
        BitConverter.GetBytes((uint)0x30).CopyTo(buf, off);
        BitConverter.GetBytes((uint)attrLen).CopyTo(buf, off + 4);
        buf[off + 8] = 0;
        BitConverter.GetBytes((uint)valueLen).CopyTo(buf, off + 16);
        BitConverter.GetBytes((ushort)24).CopyTo(buf, off + 20);
        int w = off + 24;
        BitConverter.GetBytes(5UL).CopyTo(buf, w);
        BitConverter.GetBytes(SampleTime.ToFileTimeUtc()).CopyTo(buf, w + 8);
        BitConverter.GetBytes(SampleTime.ToFileTimeUtc()).CopyTo(buf, w + 16);
        BitConverter.GetBytes(SampleTime.ToFileTimeUtc()).CopyTo(buf, w + 24);
        BitConverter.GetBytes(SampleTime.ToFileTimeUtc()).CopyTo(buf, w + 32);
        BitConverter.GetBytes(10L).CopyTo(buf, w + 40);
        BitConverter.GetBytes(10L).CopyTo(buf, w + 48);
        BitConverter.GetBytes(0u).CopyTo(buf, w + 56);
        buf[w + 64] = (byte)"longname.txt".Length;
        buf[w + 65] = 1;
        nameBytes.CopyTo(buf, w + 66);
        BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(buf, off + attrLen);
        // Recompute sector trailers/USA (content changed after BuildRecord sealed them).
        ushort usn = (ushort)(buf[42] | (buf[43] << 8));
        buf[44] = buf[510]; buf[45] = buf[511];
        buf[46] = buf[1022]; buf[47] = buf[1023];
        buf[510] = (byte)usn; buf[511] = (byte)(usn >> 8);
        buf[1022] = (byte)usn; buf[1023] = (byte)(usn >> 8);
        _ = recordSize;
        Assert.True(NtfsMftEnumerator.TryParseFileRecord(buf, buf.Length, 512, out var parsed));
        Assert.Equal("longname.txt", parsed!.Name);
    }

    [Fact]
    public void Rejects_Bad_Magic_Truncated_And_Torn_Records()
    {
        var buf = BuildRecord("a.txt", 1, 5, 1);
        var badMagic = (byte[])buf.Clone();
        badMagic[0] = 0x00;
        Assert.False(NtfsMftEnumerator.TryParseFileRecord(badMagic, badMagic.Length, 512, out _));
        Assert.False(NtfsMftEnumerator.TryParseFileRecord(buf, 40, 512, out _));
        var torn = (byte[])buf.Clone();
        torn[510] = unchecked((byte)~torn[510]);
        Assert.False(NtfsMftEnumerator.TryParseFileRecord(torn, torn.Length, 512, out _));
        var free = (byte[])buf.Clone();
        free[0x16] = 0x00; free[0x17] = 0x00; // not in-use
        Assert.False(NtfsMftEnumerator.TryParseFileRecord(free, free.Length, 512, out _));
    }

    [Fact]
    public void Resolves_Nested_Paths_Rejects_Broken_And_Cyclic()
    {
        // Dir-only map invariant: files are resolved against it, never stored in it.
        var dirs = new Dictionary<ulong, (string Name, ulong Parent)>
        {
            [6] = ("Docs", 5),
            [9] = ("loop1", 10),
            [10] = ("loop2", 9),
        };
        Assert.True(NtfsMftEnumerator.TryResolvePath(dirs, "a.txt", 6, "E:\\", out string? full));
        Assert.Equal(Path.Combine("E:\\", "Docs", "a.txt"), full);
        Assert.False(NtfsMftEnumerator.TryResolvePath(dirs, "orphan.txt", 999, "E:\\", out _));
        Assert.False(NtfsMftEnumerator.TryResolvePath(dirs, "loop1", 9, "E:\\", out _));
        Assert.False(NtfsMftEnumerator.TryResolvePath(dirs, "", 6, "E:\\", out _));
    }

    [Fact]
    public void Mode_Selection_Is_Safe_Off_Platform()
    {
        Assert.False(NtfsMftEnumerator.TryCreate("/tmp", "off", out var volume, out string? message));
        Assert.Null(volume);
        Assert.Null(message);
        Assert.False(NtfsMftEnumerator.TryCreate("/tmp", "bogus-mode", out _, out _));
        if (!OperatingSystem.IsWindows())
        {
            Assert.False(NtfsMftEnumerator.TryCreate("/tmp", "auto", out _, out string? auto));
            Assert.NotNull(auto);
            Assert.Throws<InvalidOperationException>(() => NtfsMftEnumerator.TryCreate("/tmp", "require", out _, out _));
        }
    }

    [Fact]
    public void QuoteArg_RoundTrips()
    {
        Assert.Equal("simple", Elevation.QuoteArg("simple"));
        Assert.Equal("\"C:\\My Dir\\f.txt\"", Elevation.QuoteArg("C:\\My Dir\\f.txt"));
        Assert.Equal("\"a\\\"b\"", Elevation.QuoteArg("a\"b"));
    }
}
