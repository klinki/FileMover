namespace BackupNormalizer;

public static class Paths
{
    public static string NormalizeRelative(string relative)
    {
        var p = relative.Replace('\\', '/').TrimStart('/');
        // collapse ./ and duplicate slashes, preserve case
        while (p.Contains("//")) p = p.Replace("//", "/");
        if (p.StartsWith("./")) p = p[2..];
        return p;
    }

    public static string CombineRoot(string rootAbsPath, string relative)
    {
        var rel = NormalizeRelative(relative).Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(rootAbsPath, rel);
    }

    public static string GetRelative(string rootAbsPath, string fullPath)
    {
        var root = Path.GetFullPath(rootAbsPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(fullPath);
        if (!full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException($"Path '{full}' is not under root '{root}'");
        var rel = full.Substring(root.Length);
        return NormalizeRelative(rel);
    }

    public static string DetectCaseSensitivity(string rootPath)
    {
        // Best-effort: Windows => insensitive, Linux => sensitive, macOS => test
        if (OperatingSystem.IsWindows()) return "insensitive";
        if (OperatingSystem.IsLinux()) return "sensitive";
        try
        {
            var probe = Path.Combine(rootPath, ".bn-case-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "x");
            try
            {
                bool upperExists = File.Exists(probe.ToUpperInvariant()) || File.Exists(probe.ToLowerInvariant());
                // If upper variant maps to same file, FS is insensitive. Crude but functional.
                var dir = Path.GetDirectoryName(probe)!;
                var name = Path.GetFileName(probe);
                var found = Directory.GetFiles(dir, "*").Any(f =>
                    string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(Path.GetFileName(f), name, StringComparison.Ordinal));
                return found || upperExists ? "insensitive" : "sensitive";
            }
            finally { try { File.Delete(probe); } catch { } }
        }
        catch { return "unknown"; }
    }

    public static string GetFileSystemId(string absPath)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(absPath)) ?? absPath;
            var di = new DriveInfo(root);
            return $"{di.Name}|{di.DriveFormat}|{di.VolumeLabel}";
        }
        catch { return "unknown"; }
    }

    public static bool IsReservedWindowsName(string relative)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "CON","PRN","AUX","NUL","COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
              "LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9" };
        var parts = NormalizeRelative(relative).Split('/');
        return parts.Any(p => names.Contains(Path.GetFileNameWithoutExtension(p)));
    }
}
