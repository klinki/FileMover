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
    private const string ReadyStatus =
        "Coverage is based on recorded scans and hashes. Offline roots still contribute their recorded content.";
    private readonly Func<CoverageInput[], CoverageReport> _analyze;
    private int _reportVersion;

    public BackupCoverageViewModel()
        : this(inputs => BackupCoverage.Analyze(inputs)) { }

    public BackupCoverageViewModel(Func<CoverageInput[], CoverageReport> analyze) =>
        _analyze = analyze;

    public ObservableCollection<CoverageSourceItem> Sources { get; } = new();

    [ObservableProperty]
    public partial IReadOnlyList<CoverageItem> Entries { get; set; } = Array.Empty<CoverageItem>();
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
    public Task FilterTask { get; private set; } = Task.CompletedTask;
    public bool CanEdit => !IsBusy;
    public bool CanAnalyze => !IsBusy && Sources.Count > 0;

    [ObservableProperty]
    public partial string Summary { get; set; } = "No coverage report yet.";

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
        if (!CanAnalyze)
            return;
        var inputs = Sources
            .Select(source => new CoverageInput(
                source.DatabasePath,
                source.RootId,
                source.DeviceId
            ))
            .ToArray();
        string filter = Filter;
        int version = _reportVersion;
        IsBusy = true;
        Status = "Analyzing recorded content...";
        try
        {
            var result = await Task.Run(() =>
            {
                var report = _analyze(inputs);
                return (
                    Report: report,
                    Entries: CreateEntries(report, filter),
                    Summary: $"{report.Devices.Count:N0} device labels | {report.Content.Count:N0} verified content groups | {report.SingleDeviceContent:N0} on one device | {report.ContentOnEveryDevice:N0} on every device | {report.Unverified.Count:N0} unverified entries"
                );
            });
            while (filter != Filter && version == _reportVersion)
            {
                filter = Filter;
                result.Entries = await Task.Run(() => CreateEntries(result.Report, filter));
            }
            if (version != _reportVersion)
                return;
            Report = result.Report;
            foreach (var source in Report.Sources)
            {
                var item = Sources.First(row =>
                    Paths.PathEquals(row.DatabasePath, source.Input.DatabasePath)
                    && row.RootId == source.Input.RootId
                );
                item.SnapshotStatus =
                    $"{source.ScanStatus ?? "Not scanned"} | {BackupCoverage.ScanAge(source.ScannedUtc)} | {(source.LocallyAvailable ? "Available locally" : "Offline / not available locally")} | {source.UnverifiedFiles:N0} unverified";
            }
            SelectedEntry = null;
            Entries = result.Entries;
            Summary = result.Summary;
            Status = ReadyStatus;
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

    partial void OnFilterChanged(string value)
    {
        if (!IsBusy)
            FilterTask = ApplyFilterAsync();
    }

    private async Task ApplyFilterAsync()
    {
        if (IsBusy || Report == null)
            return;
        var report = Report;
        int version = _reportVersion;
        string filter = Filter;
        IsBusy = true;
        Status = "Filtering recorded content...";
        try
        {
            var entries = await Task.Run(() => CreateEntries(report, filter));
            while (filter != Filter && version == _reportVersion)
            {
                filter = Filter;
                entries = await Task.Run(() => CreateEntries(report, filter));
            }
            if (version != _reportVersion)
                return;
            SelectedEntry = null;
            Entries = entries;
            Status = ReadyStatus;
        }
        catch (Exception ex)
        {
            Invalidate();
            Status = "Coverage filtering failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanAnalyze));
        AnalyzeCommand.NotifyCanExecuteChanged();
        RemoveSourceCommand.NotifyCanExecuteChanged();
    }

    private void Invalidate()
    {
        _reportVersion++;
        Report = null;
        SelectedEntry = null;
        Entries = Array.Empty<CoverageItem>();
        Summary = "No coverage report yet.";
        Status =
            "Assign the same device label to all roots and exports from the same physical device. Analyze to refresh coverage.";
        OnPropertyChanged(nameof(CanAnalyze));
        AnalyzeCommand.NotifyCanExecuteChanged();
    }

    private static CoverageItem[] CreateEntries(CoverageReport report, string filter)
    {
        if (filter == "Unverified entries")
        {
            return report
                .Unverified.Select(file => new CoverageItem(
                    file.Location.RelativePath,
                    file.Size,
                    "Unverified",
                    file.Location.DeviceId,
                    "",
                    $"{file.Reason}\n{file.Location.DatabasePath} [{file.Location.RootId}] {file.Location.RelativePath}",
                    true
                ))
                .ToArray();
        }
        return report
            .Content.Where(group =>
                filter switch
                {
                    "Only one device" => group.DeviceCount == 1,
                    "Every device" => group.DeviceCount == report.Devices.Count,
                    _ => true,
                }
            )
            .Select(group =>
            {
                string details = string.Join(
                    Environment.NewLine,
                    group.Locations.Select(location =>
                        $"{location.DeviceId} | {location.DatabasePath} [{location.RootId}] | {location.RelativePath}"
                    )
                );
                return new CoverageItem(
                    group.Locations[0].RelativePath,
                    group.Size,
                    $"{group.DeviceCount}/{report.Devices.Count} devices",
                    string.Join(", ", group.Devices),
                    group.Digest,
                    details,
                    false
                );
            })
            .ToArray();
    }
}
