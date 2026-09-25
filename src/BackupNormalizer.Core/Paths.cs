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
        if (Path.IsPathRooted(relative)) throw new InvalidOperationException($"Relative path is rooted: '{relative}'");
        var normalized = NormalizeRelative(relative);
        if (normalized.Split('/').Any(part => part == ".."))
            throw new InvalidOperationException($"Relative path escapes its root: '{relative}'");
        var root = Path.GetFullPath(rootAbsPath);
        var rel = normalized.Replace('/', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(root, rel));
        if (!string.Equals(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            && !full.StartsWith(root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException($"Relative path escapes its root: '{relative}'");
        return full;
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

    public static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public static bool RootsOverlap(string left, string right)
    {
        string a = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string b = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(a, b, comparison)
            || b.StartsWith(a + Path.DirectorySeparatorChar, comparison)
            || a.StartsWith(b + Path.DirectorySeparatorChar, comparison);
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
