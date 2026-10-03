using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BackupNormalizer.Ui.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BackupNormalizer.Ui.ViewModels;

public sealed partial class CoverageSourceItem : ObservableObject
{
    public string DatabasePath { get; }
    public string RootId { get; }
    public string RootLabel { get; }
    public string RootPath { get; }

    [ObservableProperty]
    public partial string DeviceId { get; set; }

    [ObservableProperty]
    public partial string SnapshotStatus { get; set; }
    public event Action? Changed;

    public CoverageSourceItem(string databasePath, InventoryRoot root)
    {
        DatabasePath = databasePath;
        RootId = root.Root.Id;
        RootLabel = root.Display;
        RootPath = root.Root.Path;
        DeviceId =
            string.IsNullOrWhiteSpace(root.Root.FileSystemId)
            || root.Root.FileSystemId.Equals("unknown", StringComparison.OrdinalIgnoreCase)
                ? ""
                : root.Root.FileSystemId;
        SnapshotStatus =
            $"{root.ScanStatus ?? "Not scanned"} | {BackupCoverage.ScanAge(root.HealthStatus?.LatestScan?.Scan.CompletedUtc)} | {(root.LocalRootAvailable ? "Available locally" : "Offline / not available locally")}";
    }

    partial void OnDeviceIdChanged(string value) => Changed?.Invoke();
}

public sealed record CoverageItem(
    string Path,
    long Size,
    string Coverage,
    string Devices,
    string Digest,
    string Details,
    bool IsUnverified
)
{
    public string SizeText => Size.ToString("N0");
}

public sealed partial class BackupCoverageViewModel : ObservableObject
{
    public ObservableCollection<CoverageSourceItem> Sources { get; } = new();
    public ObservableCollection<CoverageItem> Entries { get; } = new();
    public IReadOnlyList<string> Filters { get; } =
    ["All verified content", "Only one device", "Every device", "Unverified entries"];

    [ObservableProperty]
    public partial string Filter { get; set; } = "Only one device";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } =
        "Add inventory databases, assign device labels, then analyze.";

    [ObservableProperty]
    public partial CoverageSourceItem? SelectedSource { get; set; }

    [ObservableProperty]
    public partial CoverageItem? SelectedEntry { get; set; }
    public CoverageReport? Report { get; private set; }
    public bool CanEdit => !IsBusy;
    public bool CanAnalyze => !IsBusy && Sources.Count > 0;
    public string Summary =>
        Report == null
            ? "No coverage report yet."
            : $"{Report.Devices.Count:N0} device labels | {Report.Content.Count:N0} verified content groups | {Report.SingleDeviceContent:N0} on one device | {Report.ContentOnEveryDevice:N0} on every device | {Report.Unverified.Count:N0} unverified entries";

    public async Task AddDatabaseAsync(string path)
    {
        if (IsBusy)
            return;
        IsBusy = true;
        try
        {
            var snapshot = await Task.Run(() => InventorySnapshot.Load(path));
            foreach (var root in snapshot.Roots)
            {
                if (
                    Sources.Any(source =>
                        Paths.PathEquals(source.DatabasePath, snapshot.DatabasePath)
                        && source.RootId == root.Root.Id
                    )
                )
                    continue;
                var item = new CoverageSourceItem(snapshot.DatabasePath, root);
                item.Changed += Invalidate;
                Sources.Add(item);
            }
            Invalidate();
        }
        catch (Exception ex)
        {
            Status = "Cannot load inventory: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void RemoveSource()
    {
        if (SelectedSource == null)
            return;
        SelectedSource.Changed -= Invalidate;
        Sources.Remove(SelectedSource);
        SelectedSource = null;
        Invalidate();
    }

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    public async Task Analyze()
    {
        var inputs = Sources
            .Select(source => new CoverageInput(
                source.DatabasePath,
                source.RootId,
                source.DeviceId
            ))
            .ToArray();
        IsBusy = true;
        Status = "Analyzing recorded content...";
        try
        {
            Report = await Task.Run(() => BackupCoverage.Analyze(inputs));
            foreach (var source in Report.Sources)
            {
                var item = Sources.First(row =>
                    Paths.PathEquals(row.DatabasePath, source.Input.DatabasePath)
                    && row.RootId == source.Input.RootId
                );
                item.SnapshotStatus =
                    $"{source.ScanStatus ?? "Not scanned"} | {BackupCoverage.ScanAge(source.ScannedUtc)} | {(source.LocallyAvailable ? "Available locally" : "Offline / not available locally")} | {source.UnverifiedFiles:N0} unverified";
            }
            ApplyFilter();
            OnPropertyChanged(nameof(Summary));
            Status =
                "Coverage is based on recorded scans and hashes. Offline roots still contribute their recorded content.";
        }
        catch (Exception ex)
        {
            Invalidate();
            Status = "Coverage analysis failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanAnalyze));
        AnalyzeCommand.NotifyCanExecuteChanged();
        RemoveSourceCommand.NotifyCanExecuteChanged();
    }

    private void Invalidate()
    {
        Report = null;
        Entries.Clear();
        SelectedEntry = null;
        Status =
            "Assign the same device label to all roots and exports from the same physical device. Analyze to refresh coverage.";
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(CanAnalyze));
        AnalyzeCommand.NotifyCanExecuteChanged();
    }

    private void ApplyFilter()
    {
        Entries.Clear();
        SelectedEntry = null;
        if (Report == null)
            return;
        if (Filter == "Unverified entries")
        {
            foreach (var file in Report.Unverified)
                Entries.Add(
                    new CoverageItem(
                        file.Location.RelativePath,
                        file.Size,
                        "Unverified",
                        file.Location.DeviceId,
                        "",
                        $"{file.Reason}\n{file.Location.DatabasePath} [{file.Location.RootId}] {file.Location.RelativePath}",
                        true
                    )
                );
            return;
        }
        foreach (
            var group in Report.Content.Where(group =>
                Filter switch
                {
                    "Only one device" => group.DeviceCount == 1,
                    "Every device" => group.DeviceCount == Report.Devices.Count,
                    _ => true,
                }
            )
        )
        {
            string details = string.Join(
                Environment.NewLine,
                group.Locations.Select(location =>
                    $"{location.DeviceId} | {location.DatabasePath} [{location.RootId}] | {location.RelativePath}"
                )
            );
            Entries.Add(
                new CoverageItem(
                    group.Locations[0].RelativePath,
                    group.Size,
                    $"{group.DeviceCount}/{Report.Devices.Count} devices",
                    string.Join(", ", group.Devices),
                    group.Digest,
                    details,
                    false
                )
            );
        }
    }
}
