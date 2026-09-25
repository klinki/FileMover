using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;

namespace BackupNormalizer;

/// <summary>Administrator detection and UAC relaunch (Windows only).</summary>
public static class Elevation
{
    public static bool IsWindowsAdmin()
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return false;
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>
    /// Relaunches the current process elevated via UAC ("runas") with the same
    /// arguments minus --elevate, then the caller should exit. Throws with a
    /// human-readable message when elevation is declined or impossible; the
    /// caller is expected to fall back to unelevated execution.
    /// </summary>
    public static int RelaunchElevated(string[] args)
    {
        string? exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("cannot determine current executable for relaunch.");
        var kept = args.Where(a => !string.Equals(a, "--elevate", StringComparison.OrdinalIgnoreCase)).ToArray();
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = string.Join(" ", kept.Select(QuoteArg)),
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Environment.CurrentDirectory,
        };
        try
        {
            Console.WriteLine("Requesting administrator rights (UAC)...");
            Process.Start(psi);
            return 0;
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                "UAC elevation was declined or unavailable. Continuing without administrator rights.", ex);
        }
    }

    public static string QuoteArg(string arg)
    {
        if (arg.Length == 0) return "\"\"";
        bool needsQuotes = false;
        foreach (char c in arg)
            if (char.IsWhiteSpace(c) || c == '"') { needsQuotes = true; break; }
        if (!needsQuotes) return arg;
        var sb = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
            }
            else
            {
                sb.Append('\\', backslashes);
                sb.Append(c);
            }
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
    }
}
