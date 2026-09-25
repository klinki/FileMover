using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BackupNormalizer;

/// <summary>
/// Fast NTFS enumeration via direct MFT reads (WizTree-style), Windows-only.
/// Yields the same logical entries as recursive enumeration but with sizes and
/// timestamps straight from $FILE_NAME records: no per-file stat calls and no
/// per-directory traversal. Requires an NTFS volume and administrator rights;
/// everything else falls back to recursive enumeration (see Scanner).
/// </summary>
public static class NtfsMftEnumerator
{
    public sealed record MftFile(ulong RecordNumber, string FullPath, string Name, ulong ParentRecord,
        long Size, DateTime ModifiedUtc, DateTime CreatedUtc, bool IsReparse);

    /// <summary>Parsed $FILE_NAME view of one MFT record. Pure function, unit-tested.</summary>
    public sealed record ParsedFileRecord(
        string Name, ulong ParentRecord, long Size,
        DateTime ModifiedUtc, DateTime CreatedUtc, bool IsDirectory, bool IsReparse);

    private const uint GenericRead = 0x80000000;
    private const uint ShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;
    private const uint FsctlGetNtfsVolumeData = 0x90064;
    private const uint FsctlGetNtfsFileRecord = 0x90068;
    private const uint FileFileName = 0x30;
    private const uint AttributeEnd = 0xFFFFFFFF;
    private const ushort RecordInUse = 0x01;
    private const ushort RecordIsDirectory = 0x02;
    private const uint FileAttributeReparsePoint = 0x400;
    private const ulong MftReservedCount = 16;
    private const ulong VolumeRootRecord = 5;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code,
        IntPtr inBuffer, int inSize, IntPtr outBuffer, int outSize,
        out int bytesReturned, IntPtr overlapped);

    public static bool IsSupportedPlatform => OperatingSystem.IsWindows();

    public static bool IsNtfsVolume(string rootPath)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(rootPath));
            if (string.IsNullOrEmpty(root)) return false;
            return string.Equals(new DriveInfo(root).DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Decides whether MFT mode applies. Never throws.</summary>
    public static bool TryCreate(string rootPath, string mode,
        out NtfsVolume? volume, out string? message)
    {
        volume = null;
        message = null;
        mode = (mode ?? "off").Trim().ToLowerInvariant();
        if (mode is "off" or "" or "false" or "no") return false;
        bool require = mode is "require" or "on" or "yes" or "true";
        if (!require && mode != "auto")
            return false;
        if (!IsSupportedPlatform)
            return Fail(require, "MFT scan requires Windows.", out message);
        if (!IsNtfsVolume(rootPath))
            return Fail(require, $"MFT scan requires an NTFS volume (root '{rootPath}' is not NTFS).", out message);
        if (!Elevation.IsWindowsAdmin())
            return Fail(require, "MFT scan requires administrator rights (re-run with --elevate or as administrator).", out message);
        try
        {
            volume = NtfsVolume.Open(rootPath);
            message = $"MFT scan of '{rootPath}' (fast NTFS enumeration).";
            return true;
        }
        catch (Exception ex) when (!require)
        {
            message = $"MFT scan unavailable ({ex.Message}); using recursive enumeration.";
            return false;
        }
    }

    private static bool Fail(bool require, string reason, out string? message)
    {
        message = require ? null : reason + " Using recursive enumeration.";
        if (require) throw new InvalidOperationException(reason);
        return false;
    }

    /// <summary>Open volume handle + geometry. Dispose when done.</summary>
    public sealed class NtfsVolume : IDisposable
    {
        private readonly SafeFileHandle _handle;
        public int BytesPerSector { get; }
        public int BytesPerFileRecord { get; }
        public ulong RecordCount { get; }
        private bool _disposed;

        private NtfsVolume(SafeFileHandle handle, int bytesPerSector, int bytesPerFileRecord, ulong recordCount)
        {
            _handle = handle;
            BytesPerSector = bytesPerSector;
            BytesPerFileRecord = bytesPerFileRecord;
            RecordCount = recordCount;
        }

        public static NtfsVolume Open(string rootPath)
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(rootPath))
                ?? throw new InvalidOperationException($"cannot determine volume for '{rootPath}'");
            string volume = @"\\.\" + root.TrimEnd('\\');
            var handle = CreateFileW(volume, GenericRead, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle.IsInvalid)
                throw new IOException($"cannot open volume '{volume}' (admin rights required).");
            try
            {
                IntPtr outBuf = Marshal.AllocHGlobal(512);
                try
                {
                    if (!DeviceIoControl(handle, FsctlGetNtfsVolumeData,
                        IntPtr.Zero, 0, outBuf, 512, out int returned, IntPtr.Zero) || returned < 72)
                        throw new IOException($"cannot query NTFS geometry for '{volume}'.");
                    byte[] data = new byte[returned];
                    Marshal.Copy(outBuf, data, 0, returned);
                    if (!BitConverter.IsLittleEndian)
                        throw new PlatformNotSupportedException("MFT parsing requires little-endian.");
                    int bytesPerSector = BitConverter.ToInt32(data, 40);
                    int bytesPerRecord = BitConverter.ToInt32(data, 48);
                    ulong validLength = BitConverter.ToUInt64(data, 56);
                    if (bytesPerSector <= 0 || bytesPerRecord <= 0)
                        throw new IOException($"invalid NTFS geometry for '{volume}'.");
                    return new NtfsVolume(handle, bytesPerSector, bytesPerRecord, validLength / (ulong)bytesPerRecord);
                }
                finally { Marshal.FreeHGlobal(outBuf); }
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        public IEnumerable<MftFile> EnumerateFiles(string volumeRoot, string requestedRoot)
        {
            // Memory strategy: the volume is read twice. Pass 1 keeps DIRECTORY
            // records only; pass 2 resolves each file against that dir map and
            // emits it immediately. Peak memory scales with directory count
            // (typically 1-5% of entries), not file count. A live volume may
            // change between passes; re-added dirs make this converge instead
            // of failing, and per-file races resolve the same way as a
            // recursive scan (size/mtime mismatch marks hashes stale later).
            var dirs = new Dictionary<ulong, (string Name, ulong Parent)>();
            int skipped = 0;
            foreach (var (frn, parsed) in ReadRecords())
            {
                if (parsed == null) { skipped++; continue; }
                if (parsed.IsDirectory) dirs[frn] = (parsed.Name, parsed.ParentRecord);
            }
            string prefix = requestedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            foreach (var (frn, parsed) in ReadRecords())
            {
                if (parsed == null) { skipped++; continue; }
                if (parsed.IsDirectory)
                {
                    // INVARIANT: the lookup map holds directory records only.
                    // Files resolve through it but are never stored in it;
                    // see TryResolvePath. Do not add file records here.
                    dirs[frn] = (parsed.Name, parsed.ParentRecord);
                    continue;
                }
                if (!TryResolvePath(dirs, parsed.Name, parsed.ParentRecord, volumeRoot, out string? full) || full == null)
                {
                    skipped++;
                    continue;
                }
                if (!full.StartsWith(prefix, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    continue;
                yield return new MftFile(frn, full, parsed.Name, parsed.ParentRecord,
                    parsed.Size, parsed.ModifiedUtc, parsed.CreatedUtc, parsed.IsReparse);
            }
            SkippedRecords = skipped;
        }

        private IEnumerable<(ulong Frn, ParsedFileRecord? Parsed)> ReadRecords()
        {
            byte[] record = new byte[Math.Max(BytesPerFileRecord, 1024)];
            IntPtr inBuf = Marshal.AllocHGlobal(8);
            IntPtr outBuf = Marshal.AllocHGlobal(record.Length);
            try
            {
                for (ulong frn = MftReservedCount; frn < RecordCount; frn++)
                {
                    Marshal.WriteInt64(inBuf, (long)frn);
                    if (!DeviceIoControl(_handle, FsctlGetNtfsFileRecord,
                        inBuf, 8, outBuf, record.Length, out int returned, IntPtr.Zero) || returned <= 0)
                    {
                        yield return (frn, null);
                        continue;
                    }
                    Marshal.Copy(outBuf, record, 0, returned);
                    yield return TryParseFileRecord(record, returned, BytesPerSector, out var parsed)
                        ? (frn, parsed)
                        : (frn, null);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(inBuf);
                Marshal.FreeHGlobal(outBuf);
            }
        }

        public int SkippedRecords { get; private set; }

        public void Dispose()
        {
            if (!_disposed) { _disposed = true; _handle.Dispose(); }
        }
    }

    /// <summary>
    /// Resolves a leaf (file name + parent FRN) to a full path through directory
    /// ancestors. INVARIANT: <paramref name="nodes"/> holds directory records
    /// only — files are resolved against it, never stored in it. A file FRN can
    /// never appear as another entry's parent, so looking the leaf itself up in
    /// the map is both unnecessary and wrong; pass its name and parent directly.
    /// </summary>
    public static bool TryResolvePath(
        Dictionary<ulong, (string Name, ulong Parent)> nodes, string leafName, ulong leafParent,
        string volumeRoot, out string? full)
    {
        full = null;
        if (string.IsNullOrEmpty(leafName)) return false;
        var segments = new List<string> { leafName };
        ulong current = leafParent;
        while (current != VolumeRootRecord)
        {
            if (segments.Count > 512) return false; // cycle guard
            if (!nodes.TryGetValue(current, out var parent)) return false;
            segments.Add(parent.Name);
            current = parent.Parent;
        }
        segments.Reverse();
        string path = volumeRoot;
        foreach (var segment in segments)
            path = Path.Combine(path, segment);
        full = path;
        return true;
    }

    public static bool TryParseFileRecord(byte[] buffer, int length, int bytesPerSector, out ParsedFileRecord? parsed)
    {
        parsed = null;
        try
        {
            if (buffer == null || length < 48 || length > buffer.Length) return false;
            if (!BitConverter.IsLittleEndian) return false;
            if (ReadU32(buffer, 0) != 0x454C4946) return false; // "FILE"
            int usaOffset = ReadU16(buffer, 4);
            int usaCount = ReadU16(buffer, 6);
            ushort flags = ReadU16(buffer, 0x16);
            int firstAttr = ReadU16(buffer, 0x14);
            if ((flags & RecordInUse) == 0) return false;
            if (firstAttr < 42 || firstAttr >= length) return false;
            if (!FixupSectors(buffer, length, usaOffset, usaCount, bytesPerSector)) return false;
            bool isDir = (flags & RecordIsDirectory) != 0;

            string? bestName = null;
            ulong bestParent = 0;
            long bestSize = 0;
            DateTime bestModified = DateTime.MinValue;
            DateTime bestCreated = DateTime.MinValue;
            bool bestReparse = false;
            int bestRank = int.MaxValue;

            int offset = firstAttr;
            while (offset + 8 <= length)
            {
                uint type = ReadU32(buffer, offset);
                if (type == AttributeEnd) break;
                int attrLen = (int)ReadU32(buffer, offset + 4);
                if (attrLen < 8 || offset + attrLen > length) return false;
                if (type == FileFileName && buffer[offset + 8] == 0) // resident only
                {
                    int valueLen = (int)ReadU32(buffer, offset + 16);
                    int valueOff = ReadU16(buffer, offset + 20);
                    if (valueOff >= 0 && valueLen >= 66 && offset + valueOff + valueLen <= length)
                    {
                        int v = offset + valueOff;
                        ulong parentRef = ReadU64(buffer, v);
                        ulong parent = parentRef & 0xFFFFFFFFFFFFUL;
                        long created = ReadI64(buffer, v + 8);
                        long modified = ReadI64(buffer, v + 16);
                        long realSize = ReadI64(buffer, v + 48);
                        uint nameFlags = ReadU32(buffer, v + 56);
                        int nameLen = buffer[v + 64];
                        int nameSpace = buffer[v + 65];
                        if (nameLen > 0 && v + 66 + nameLen * 2 <= length)
                        {
                            int rank = nameSpace switch { 3 => 0, 1 => 1, 0 => 2, _ => 3 };
                            if (rank < bestRank)
                            {
                                string name = Encoding.Unicode.GetString(buffer, v + 66, nameLen * 2);
                                if (!string.IsNullOrEmpty(name))
                                {
                                    bestRank = rank;
                                    bestName = name;
                                    bestParent = parent;
                                    bestSize = realSize;
                                    bestCreated = DateTime.FromFileTimeUtc(created);
                                    bestModified = DateTime.FromFileTimeUtc(modified);
                                    bestReparse = (nameFlags & FileAttributeReparsePoint) != 0;
                                }
                            }
                        }
                    }
                }
                offset += attrLen;
            }
            if (bestName == null) return false;
            parsed = new ParsedFileRecord(bestName, bestParent, bestSize,
                bestModified, bestCreated, isDir, bestReparse);
            return true;
        }
        catch { return false; }
    }

    private static bool FixupSectors(byte[] buffer, int length, int usaOffset, int usaCount, int bytesPerSector)
    {
        if (usaOffset == 0 || usaCount == 0) return true;
        if (bytesPerSector <= 0) return false;
        int sectors = length / bytesPerSector;
        if (usaCount != sectors + 1) return false;
        if (usaOffset + usaCount * 2 > length) return false;
        ushort usn = (ushort)(buffer[usaOffset] | (buffer[usaOffset + 1] << 8));
        for (int i = 0; i < sectors; i++)
        {
            int sectorEnd = (i + 1) * bytesPerSector - 2;
            if (sectorEnd + 2 > length) return false;
            ushort trailer = (ushort)(buffer[sectorEnd] | (buffer[sectorEnd + 1] << 8));
            if (trailer != usn) return false; // torn write
            int replOff = usaOffset + 2 + i * 2;
            buffer[sectorEnd] = buffer[replOff];
            buffer[sectorEnd + 1] = buffer[replOff + 1];
        }
        return true;
    }

    private static uint ReadU32(byte[] b, int o) =>
        (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
    private static ushort ReadU16(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));
    private static ulong ReadU64(byte[] b, int o) => BitConverter.ToUInt64(b, o);
    private static long ReadI64(byte[] b, int o) => BitConverter.ToInt64(b, o);
}
