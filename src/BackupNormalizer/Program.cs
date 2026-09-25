using BackupNormalizer;

var cliArgs = args.Where(a => !string.Equals(a, "--elevate", StringComparison.OrdinalIgnoreCase)).ToArray();
bool elevateRequested = cliArgs.Length != args.Length;
if (elevateRequested && OperatingSystem.IsWindows() && !Elevation.IsWindowsAdmin())
{
    try
    {
        return Elevation.RelaunchElevated(cliArgs);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"warning: {ex.Message}");
    }
}
return Cli.Run(cliArgs);
