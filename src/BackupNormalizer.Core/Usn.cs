using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BackupNormalizer;

internal sealed record UsnState(
    string VolumeIdentity,
    string RootIdentity,
    string JournalId,
    long FirstUsn,
    long NextUsn,
    long LowestValidUsn,
    string? CanonicalRootPath = null
);

internal sealed record UsnRecord(
    ulong FileId,
    ulong ParentId,
    long Usn,
    uint Reason,
    FileAttributes Attributes,
    string Name
);

internal interface IUsnJournal : IDisposable
{
    UsnState Query();
    IEnumerable<UsnRecord> ReadChanges(long startUsn, long endUsn, string journalId);
    string ResolveParent(ulong fileId);
    uint GetLinkCount(ulong fileId);
}

internal sealed record UsnChanges(Dictionary<string, bool> Paths, long NextUsn);

internal static class UsnReplay
{
    internal const uint Close = 0x80000000;
    internal const uint Delete = 0x200;
    internal const uint NamespaceChange = 0x100 | Delete | 0x1000 | 0x2000;
    internal const uint ReparseChange = 0x100000;
    internal const uint HardLinkChange = 0x10000;
    private const uint ContentChange = 0x7 | NamespaceChange | ReparseChange | 0x400000;

    internal static bool IsValid(
        ScanCheckpointRow checkpoint,
        ScanRow? previous,
        string rootPath,
        UsnState state
    ) =>
        previous?.Status == ScanStatus.Completed
        && previous.Id == checkpoint.ScanId
        && Paths.PathEquals(checkpoint.RootPath, rootPath)
        && checkpoint.VolumeIdentity == state.VolumeIdentity
        && checkpoint.RootIdentity == state.RootIdentity
        && checkpoint.JournalId == state.JournalId
        && checkpoint.NextUsn >= Math.Max(state.FirstUsn, state.LowestValidUsn)
        && checkpoint.NextUsn <= state.NextUsn;

    internal static UsnChanges Read(
        IUsnJournal journal,
        string root,
        long start,
        UsnState state,
        IReadOnlySet<string> excludedPaths
    )
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var paths = new Dictionary<string, bool>(StringComparer.Ordinal);
        var parents = new Dictionary<ulong, string>();
        var pending = new Dictionary<ulong, long>();
        var oldNames = new Dictionary<ulong, (ulong Parent, string Name)>();
        string fullRoot = Path.GetFullPath(state.CanonicalRootPath ?? root)
            .TrimEnd(Path.DirectorySeparatorChar);
        string prefix = fullRoot + Path.DirectorySeparatorChar;
        int count = 0;
        foreach (var record in journal.ReadChanges(start, state.NextUsn, state.JournalId))
        {
            if (++count > 1_000_000)
            {
                throw new IOException("USN window exceeds one million records.");
            }

            if (record.Usn < start || record.Usn >= state.NextUsn)
            {
                throw new IOException("USN record is outside the requested window.");
            }

            uint reasons = record.Reason & ~Close;
            if ((record.Reason & (Close | Delete)) != 0)
            {
                pending.Remove(record.FileId);
            }

            if (reasons == 0)
            {
                continue;
            }

            if ((reasons & ~0x00ffff77U) != 0)
            {
                throw new IOException("An unsupported USN change reason requires a full scan.");
            }

            bool isDirectory = record.Attributes.HasFlag(FileAttributes.Directory);
            // The recorded name of a hard-linked file can be outside this root
            // even when another name for the same content is inside it.
            if (
                (reasons & HardLinkChange) != 0
                || (!isDirectory && journal.GetLinkCount(record.FileId) > 1)
            )
            {
                throw new IOException("A hard-linked file changed; a full scan is required.");
            }

            if (!parents.TryGetValue(record.ParentId, out var parent))
            {
                parents[record.ParentId] = parent = journal.ResolveParent(record.ParentId);
            }

            string path = Path.GetFullPath(Path.Combine(parent, record.Name));
            if (!path.StartsWith(prefix, comparison))
            {
                if (
                    isDirectory
                    && Paths.RootsOverlap(path, root)
                    && (record.Reason & (NamespaceChange | ReparseChange | 0x800)) != 0
                )
                {
                    throw new IOException("An ancestor of the root changed in the USN journal.");
                }

                continue;
            }
            if (!path.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new IOException("Ambiguous path casing requires a full scan.");
            }

            if (excludedPaths.Contains(path))
            {
                continue;
            }

            if (isDirectory)
            {
                if (
                    (reasons & (NamespaceChange | ReparseChange | 0x800)) != 0
                    || (reasons != 0 && record.Attributes.HasFlag(FileAttributes.ReparsePoint))
                )
                {
                    throw new IOException(
                        "A directory namespace, link, or access change requires a full scan."
                    );
                }

                continue;
            }
            if ((reasons & 0x1000) != 0)
            {
                oldNames.TryAdd(record.FileId, (record.ParentId, record.Name));
            }

            if (
                (reasons & 0x2000) != 0
                && oldNames.TryGetValue(record.FileId, out var old)
                && old.Parent == record.ParentId
                && old.Name != record.Name
                && string.Equals(old.Name, record.Name, StringComparison.OrdinalIgnoreCase)
            )
            {
                throw new IOException("A case-only rename requires a full scan.");
            }

            if ((record.Reason & (Close | Delete)) == 0)
            {
                pending.TryAdd(record.FileId, record.Usn);
            }

            string relative = Paths.GetRelative(root, path);
            bool invalidate = (reasons & ContentChange) != 0;
            paths[relative] = invalidate || paths.GetValueOrDefault(relative);
        }
        // Open handles can coalesce later writes into their eventual close record.
        // Replay their earliest changes next time instead of advancing past them.
        return new UsnChanges(paths, pending.Count == 0 ? state.NextUsn : pending.Values.Min());
    }
}

/// <summary>Read-only access to an existing local NTFS journal. Never creates a journal.</summary>
internal sealed class NtfsUsnJournal : IUsnJournal
{
    private const uint QueryJournal = 0x000900f4;
    private const uint ReadJournal = 0x000900bb;
    private const uint BackupAndReparse = 0x02200000;
    private readonly SafeFileHandle _volume;
    private readonly string _root;
    private readonly string _volumeRoot;
    private readonly string _volumeName;

    private NtfsUsnJournal(
        SafeFileHandle volume,
        string root,
        string volumeRoot,
        string volumeName
    ) => (_volume, _root, _volumeRoot, _volumeName) = (volume, root, volumeRoot, volumeName);

    internal static IUsnJournal? TryOpen(string root)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            string full = Path.GetFullPath(root);
            string volumeRoot = Path.GetPathRoot(full)!;
            if (
                !string.Equals(
                    new DriveInfo(volumeRoot).DriveFormat,
                    "NTFS",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return null;
            }

            var volumeName = new StringBuilder(128);
            if (!GetVolumeNameForVolumeMountPointW(volumeRoot, volumeName, volumeName.Capacity))
            {
                return null;
            }

            var volume = CreateFileW(
                volumeName.ToString().TrimEnd('\\'),
                0x80000000,
                7,
                IntPtr.Zero,
                3,
                0,
                IntPtr.Zero
            );
            if (volume.IsInvalid)
            {
                volume.Dispose();
                return null;
            }
            var journal = new NtfsUsnJournal(volume, full, volumeRoot, volumeName.ToString());
            try
            {
                _ = journal.Query();
                return journal;
            }
            catch
            {
                journal.Dispose();
                throw;
            }
        }
        catch (Exception ex)
            when (ex
                    is IOException
                        or UnauthorizedAccessException
                        or ArgumentException
                        or NotSupportedException
            )
        {
            return null;
        }
    }

    public UsnState Query()
    {
        byte[] buffer = new byte[80];
        if (
            !DeviceIoControl(
                _volume,
                QueryJournal,
                null,
                0,
                buffer,
                buffer.Length,
                out int returned,
                IntPtr.Zero
            )
        )
        {
            throw NativeError("Cannot query the USN journal");
        }

        if (returned < 56)
        {
            throw new IOException("Truncated USN journal information.");
        }

        using var rootHandle = CreateFileW(
            _root,
            0,
            7,
            IntPtr.Zero,
            3,
            BackupAndReparse,
            IntPtr.Zero
        );
        if (rootHandle.IsInvalid || !GetFileInformationByHandle(rootHandle, out var info))
        {
            throw NativeError("Cannot identify the USN scan root");
        }

        string canonicalRoot = GetPath(rootHandle);
        if (
            (info.Attributes & (uint)FileAttributes.Directory) == 0
            || (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0
            || !Paths.PathEquals(canonicalRoot, _root)
        )
        {
            throw new IOException(
                "USN scanning requires a root without redirected parent components."
            );
        }

        var volumeName = new StringBuilder(128);
        if (!GetVolumeNameForVolumeMountPointW(_volumeRoot, volumeName, volumeName.Capacity))
        {
            throw NativeError("Cannot identify the USN volume");
        }

        if (!string.Equals(volumeName.ToString(), _volumeName, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The USN volume was replaced or remounted while scanning.");
        }

        long first = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(8));
        long next = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(16));
        long lowest = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(24));
        if (first < 0 || next < first || lowest < 0 || lowest > next)
        {
            throw new IOException("Invalid USN journal boundaries.");
        }

        return new UsnState(
            volumeName + ":" + info.VolumeSerial.ToString("X8"),
            ((ulong)info.FileIndexHigh << 32 | info.FileIndexLow).ToString("X16"),
            BinaryPrimitives.ReadUInt64LittleEndian(buffer).ToString("X16"),
            first,
            next,
            lowest,
            canonicalRoot
        );
    }

    public IEnumerable<UsnRecord> ReadChanges(long startUsn, long endUsn, string journalId)
    {
        byte[] request = new byte[40]; // READ_USN_JOURNAL_DATA_V0; immediate, nonblocking notifications.
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(8), uint.MaxValue);
        BinaryPrimitives.WriteUInt64LittleEndian(
            request.AsSpan(32),
            Convert.ToUInt64(journalId, 16)
        );
        byte[] buffer = new byte[64 * 1024];
        long cursor = startUsn;
        while (cursor < endUsn)
        {
            BinaryPrimitives.WriteInt64LittleEndian(request, cursor);
            if (
                !DeviceIoControl(
                    _volume,
                    ReadJournal,
                    request,
                    request.Length,
                    buffer,
                    buffer.Length,
                    out int returned,
                    IntPtr.Zero
                )
            )
            {
                throw NativeError("Cannot read the USN journal");
            }

            var decoded = DecodeBuffer(buffer, returned, cursor);
            if (decoded.NextUsn <= cursor)
            {
                throw new IOException("USN journal did not advance through the requested window.");
            }

            foreach (var record in decoded.Records)
            {
                if (record.Usn < endUsn)
                {
                    yield return record;
                }
            }

            cursor = decoded.NextUsn;
        }
    }

    internal static (long NextUsn, IReadOnlyList<UsnRecord> Records) DecodeBuffer(
        byte[] buffer,
        int length,
        long startUsn
    )
    {
        if (startUsn < 0)
        {
            throw new IOException("Invalid starting USN.");
        }

        if (length < 8 || length > buffer.Length)
        {
            throw new IOException("Invalid USN buffer length.");
        }

        long next = BinaryPrimitives.ReadInt64LittleEndian(buffer);
        if (next < startUsn)
        {
            throw new IOException("USN cursor moved backwards.");
        }

        var records = new List<UsnRecord>();
        long previous = startUsn - 1;
        for (int offset = 8; offset < length; )
        {
            if (length - offset < 60)
            {
                throw new IOException("Truncated USN record.");
            }

            var data = buffer.AsSpan(offset, length - offset);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(data);
            if (size < 60 || size > data.Length || size % 8 != 0)
            {
                throw new IOException("Invalid USN record length.");
            }

            if (BinaryPrimitives.ReadUInt16LittleEndian(data[4..]) != 2)
            {
                throw new IOException("Unsupported USN record version; a full scan is required.");
            }

            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(data[56..]);
            int nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(data[58..]);
            if (
                nameOffset < 60
                || nameLength == 0
                || nameOffset % 2 != 0
                || nameLength % 2 != 0
                || nameOffset + nameLength > size
            )
            {
                throw new IOException("Invalid USN file name bounds.");
            }
            // Read UTF-16 code units exactly, including names containing unpaired surrogates.
            var nameChars = new char[nameLength / 2];
            for (int i = 0; i < nameChars.Length; i++)
            {
                nameChars[i] = (char)
                    BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(nameOffset + i * 2, 2));
            }

            string name = new(nameChars);
            if (name is "." or ".." || name.IndexOfAny(['/', '\\', '\0', ':']) >= 0)
            {
                throw new IOException("Invalid USN file name.");
            }

            long usn = BinaryPrimitives.ReadInt64LittleEndian(data[24..]);
            if (usn <= previous || usn >= next)
            {
                throw new IOException("Invalid USN record ordering.");
            }

            if (
                BinaryPrimitives.ReadUInt64LittleEndian(data[8..]) == 0
                || BinaryPrimitives.ReadUInt64LittleEndian(data[16..]) == 0
            )
            {
                throw new IOException("Invalid USN file identity.");
            }

            previous = usn;
            records.Add(
                new UsnRecord(
                    BinaryPrimitives.ReadUInt64LittleEndian(data[8..]),
                    BinaryPrimitives.ReadUInt64LittleEndian(data[16..]),
                    usn,
                    BinaryPrimitives.ReadUInt32LittleEndian(data[40..]),
                    (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(data[52..]),
                    name
                )
            );
            offset += (int)size;
        }
        return (next, records);
    }

    public string ResolveParent(ulong fileId)
    {
        using var handle = OpenById(fileId);
        if (handle.IsInvalid)
        {
            throw NativeError("Cannot resolve a USN parent directory");
        }

        if (
            !GetFileInformationByHandle(handle, out var info)
            || (info.Attributes & (uint)FileAttributes.Directory) == 0
            || (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0
        )
        {
            throw new IOException("USN parent directory is unavailable or redirected.");
        }

        return GetPath(handle);
    }

    public uint GetLinkCount(ulong fileId)
    {
        using var handle = OpenById(fileId);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            if (error is 2 or 3)
            {
                return 0; // A deleted identity is expected.
            }

            throw new IOException($"Cannot inspect a USN file identity: Win32 error {error}.");
        }
        if (!GetFileInformationByHandle(handle, out var info))
        {
            throw NativeError("Cannot inspect USN hard links");
        }

        return info.LinkCount;
    }

    private SafeFileHandle OpenById(ulong fileId)
    {
        var descriptor = new FileIdDescriptor { Size = 24, FileId = fileId };
        return OpenFileById(_volume, ref descriptor, 0, 7, IntPtr.Zero, BackupAndReparse);
    }

    private static string GetPath(SafeFileHandle handle)
    {
        var path = new StringBuilder(512);
        uint length = GetFinalPathNameByHandleW(handle, path, (uint)path.Capacity, 0);
        if (length >= path.Capacity)
        {
            if (length > 32768)
            {
                throw new IOException("USN path exceeds the supported length.");
            }

            path = new StringBuilder((int)length + 1);
            length = GetFinalPathNameByHandleW(handle, path, (uint)path.Capacity, 0);
        }
        if (length == 0 || length >= path.Capacity)
        {
            throw NativeError("Cannot obtain a USN entry path");
        }

        string result = path.ToString();
        return result.StartsWith(@"\\?\", StringComparison.Ordinal) ? result[4..] : result;
    }

    private static IOException NativeError(string operation) =>
        new($"{operation}: Win32 error {Marshal.GetLastWin32Error()}.");

    public void Dispose() => _volume.Dispose();

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct FileIdDescriptor
    {
        [FieldOffset(0)]
        public uint Size;

        [FieldOffset(4)]
        public uint Type;

        [FieldOffset(8)]
        public ulong FileId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HandleInfo
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created,
            Accessed,
            Written;
        public uint VolumeSerial,
            SizeHigh,
            SizeLow,
            LinkCount,
            FileIndexHigh,
            FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string path,
        uint access,
        uint share,
        IntPtr security,
        uint disposition,
        uint flags,
        IntPtr template
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle handle,
        uint code,
        byte[]? input,
        int inputLength,
        byte[] output,
        int outputLength,
        out int returned,
        IntPtr overlapped
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle OpenFileById(
        SafeFileHandle volume,
        ref FileIdDescriptor id,
        uint access,
        uint share,
        IntPtr security,
        uint flags
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle handle,
        out HandleInfo info
    );

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle handle,
        StringBuilder path,
        uint length,
        uint flags
    );

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetVolumeNameForVolumeMountPointW(
        string mount,
        StringBuilder name,
        int length
    );
}
