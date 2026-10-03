using System.Buffers.Binary;
using System.Text;
using BackupNormalizer;

namespace BackupNormalizer.Tests;

public sealed class UsnParserTests
{
    private static byte[] Buffer(params (long Usn, string Name)[] records)
    {
        var result = new List<byte>(BitConverter.GetBytes(200L));
        foreach (var record in records)
        {
            byte[] name = Encoding.Unicode.GetBytes(record.Name);
            byte[] data = new byte[(60 + name.Length + 7) / 8 * 8];
            BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)data.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), 2);
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(8), 0x1234000000000020);
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(16), 0x5678000000000005);
            BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(24), record.Usn);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(40), 1 | UsnReplay.Close);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(52), (uint)FileAttributes.Normal);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(56), (ushort)name.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(58), 60);
            name.CopyTo(data, 60);
            result.AddRange(data);
        }
        return result.ToArray();
    }

    [Fact]
    public void Decodes_Variable_Length_Names_Full_Identities_And_Reasons()
    {
        var buffer = Buffer((110, "写真😀.txt"), (120, "second"));

        var result = NtfsUsnJournal.DecodeBuffer(buffer, buffer.Length, 100);

        Assert.Equal(200, result.NextUsn);
        Assert.Equal(2, result.Records.Count);
        Assert.Equal("写真😀.txt", result.Records[0].Name);
        Assert.Equal(0x1234000000000020UL, result.Records[0].FileId);
        Assert.Equal(0x5678000000000005UL, result.Records[0].ParentId);
        Assert.Equal(1U | UsnReplay.Close, result.Records[0].Reason);
    }

    [Theory]
    [InlineData("length")]
    [InlineData("alignment")]
    [InlineData("version")]
    [InlineData("name-offset")]
    [InlineData("name-length")]
    [InlineData("odd-name")]
    [InlineData("zero-id")]
    [InlineData("zero-parent")]
    [InlineData("cursor")]
    [InlineData("before-start")]
    [InlineData("at-end")]
    public void Malformed_Records_Are_Rejected_Instead_Of_Partially_Applied(string corruption)
    {
        byte[] buffer = Buffer((110, "name"));
        switch (corruption)
        {
            case "length": BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), 2000); break;
            case "alignment": BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), 61); break;
            case "version": BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), 3); break;
            case "name-offset": BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(66), 4); break;
            case "name-length": BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(64), 500); break;
            case "odd-name": BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(64), 3); break;
            case "zero-id": BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(16), 0); break;
            case "zero-parent": BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(24), 0); break;
            case "cursor": BinaryPrimitives.WriteInt64LittleEndian(buffer, 90); break;
            case "before-start": BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(32), 99); break;
            case "at-end": BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(32), 200); break;
        }
        Assert.Throws<IOException>(() => NtfsUsnJournal.DecodeBuffer(buffer, buffer.Length, 100));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(9)]
    [InlineData(67)]
    [InlineData(1000)]
    public void Invalid_Buffer_Bounds_Are_Rejected(int length)
    {
        var buffer = Buffer((110, "name"));
        Assert.Throws<IOException>(() => NtfsUsnJournal.DecodeBuffer(buffer, length, 100));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a\\b")]
    [InlineData("a\0b")]
    [InlineData("a:b")]
    public void Names_Cannot_Escape_Their_Parent_Or_Name_Alternate_Streams(string name)
    {
        var buffer = Buffer((110, name));
        Assert.Throws<IOException>(() => NtfsUsnJournal.DecodeBuffer(buffer, buffer.Length, 100));
    }

    [Fact]
    public void Repeated_Or_Unordered_Record_Positions_Are_Rejected()
    {
        foreach (var buffer in new[] { Buffer((110, "first"), (110, "second")), Buffer((120, "first"), (110, "second")) })
            Assert.Throws<IOException>(() => NtfsUsnJournal.DecodeBuffer(buffer, buffer.Length, 100));
    }

    [Fact]
    public void Empty_Window_And_UTF16_Code_Units_Are_Preserved()
    {
        var empty = Buffer();
        Assert.Empty(NtfsUsnJournal.DecodeBuffer(empty, empty.Length, 100).Records);
        var buffer = Buffer((110, "x"));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(68), 0xd800);
        Assert.Equal("\ud800", Assert.Single(NtfsUsnJournal.DecodeBuffer(buffer, buffer.Length, 100).Records).Name);
    }
}
