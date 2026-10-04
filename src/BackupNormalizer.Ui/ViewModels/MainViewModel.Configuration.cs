using System;
using System.IO;

namespace BackupNormalizer.Ui.ViewModels;

public sealed partial class MainViewModel
{
    public AppConfig Configuration { get; private set; } = new();
    public string ConfigurationPath { get; private set; } = "";
    public string ConfigurationSummary =>
        (ConfigurationPath.Length == 0 ? "Built-in defaults" : ConfigurationPath)
        + $" | Hash workers: {Configuration.HashParallelism} | MFT: {Configuration.MftMode} | USN: {Configuration.UsnMode} | Exclusions: {Configuration.ExcludedPathRegexes?.Length ?? 0}";

    public bool LoadConfiguration(string? path = null)
    {
        if (IsBusy)
        {
            return false;
        }
        try
        {
            string resolved = Path.GetFullPath(AppConfig.ResolvePath(path));
            var config = AppConfig.Load(path);
            Configuration = config;
            ConfigurationPath = File.Exists(resolved) ? resolved : "";
            OnPropertyChanged(nameof(Configuration));
            OnPropertyChanged(nameof(ConfigurationPath));
            OnPropertyChanged(nameof(ConfigurationSummary));
            StatusMessage =
                ConfigurationPath.Length == 0
                    ? "Using built-in inventory settings."
                    : "Configuration loaded: " + ConfigurationPath;
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = "Load configuration failed: " + ex.Message;
            return false;
        }
    }
}
