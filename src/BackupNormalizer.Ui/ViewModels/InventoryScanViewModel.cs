using System;
using System.IO;
using BackupNormalizer.Ui.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BackupNormalizer.Ui.ViewModels;

public sealed partial class InventoryScanViewModel : ObservableObject
{
    private readonly AppConfig _configuration;

    public InventoryScanViewModel(MainViewModel main)
    {
        _configuration = main.Configuration;
        ConfigurationSummary = main.ConfigurationSummary;
        DatabasePath = Path.GetFullPath(_configuration.Database);
        RootPath = main.Active.IsLive
            ? main.Active.CurrentPath
            : main.Active.SelectedInventoryRoot?.Root.Path ?? main.AppliedBasePath;
        string name = new DirectoryInfo(RootPath).Name.Trim(':', '\\', '/');
        RootId = name.Length == 0 ? "files" : name.ToLowerInvariant();
    }

    public string ConfigurationSummary { get; }

    [ObservableProperty]
    public partial string DatabasePath { get; set; }

    [ObservableProperty]
    public partial string RootPath { get; set; }

    [ObservableProperty]
    public partial string RootId { get; set; }

    [ObservableProperty]
    public partial bool HashAfterScan { get; set; } = true;

    [ObservableProperty]
    public partial string Error { get; set; } = "";

    public InventoryJobRequest CreateRequest()
    {
        if (string.IsNullOrWhiteSpace(DatabasePath) || string.IsNullOrWhiteSpace(RootId))
        {
            throw new InvalidOperationException("Choose a new database file and enter a root ID.");
        }
        if (!Path.IsPathFullyQualified(RootPath))
        {
            throw new InvalidOperationException("Choose a folder with a fully qualified path.");
        }
        return new InventoryJobRequest(
            Path.GetFullPath(DatabasePath),
            RootId.Trim(),
            Path.GetFullPath(RootPath),
            HashAfterScan ? InventoryJobKind.ScanThenHash : InventoryJobKind.Scan,
            FullScan: true,
            Parallelism: _configuration.HashParallelism,
            CreateInventory: true,
            Configuration: _configuration
        );
    }
}
