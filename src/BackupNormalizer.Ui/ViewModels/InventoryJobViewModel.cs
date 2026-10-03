using System;
using System.Globalization;
using BackupNormalizer.Ui.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BackupNormalizer.Ui.ViewModels;

public sealed partial class InventoryJobViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(ProgressText),
        nameof(CurrentPath),
        nameof(RateAndElapsed),
        nameof(BytesReadText)
    )]
    public partial InventoryJobProgress? Progress { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(LastResultText),
        nameof(LastResultCounts),
        nameof(LastResultElapsed)
    )]
    public partial InventoryJobResult? LastResult { get; set; }

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial bool CancellationRequested { get; set; }

    [ObservableProperty]
    public partial string Context { get; set; } = "";

    [ObservableProperty]
    public partial bool FullScan { get; set; }

    public string CurrentJobTitle { get; private set; } = "No job running.";

    public string ProgressText =>
        Progress == null
            ? "Waiting for a job."
            : Progress.Stage switch
            {
                InventoryJobStage.Validating => "Checking the database and recorded root path.",
                InventoryJobStage.Scanning =>
                    $"{Progress.Scanned:N0} entries scanned, {Progress.ScanErrors:N0} errors"
                        + (Progress.Incremental ? " (USN incremental scan)" : ""),
                InventoryJobStage.Hashing =>
                    $"{Progress.Hashed + Progress.Skipped + Progress.Unstable:N0} / {Progress.TotalFiles:N0} entries processed, "
                        + $"{Progress.Hashed:N0} hashed, {Progress.Skipped:N0} skipped, {Progress.Unstable:N0} unstable",
                _ => "Waiting for a job.",
            };

    public string CurrentPath =>
        string.IsNullOrEmpty(Progress?.CurrentPath)
            ? "Waiting for progress..."
            : Progress.CurrentPath;

    public string BytesReadText =>
        Progress?.Stage == InventoryJobStage.Hashing
            ? DriveView.FormatBytes(Progress.BytesRead) + " read"
            : "";

    public string RateAndElapsed
    {
        get
        {
            if (Progress == null)
            {
                return "";
            }

            var elapsed = FormatElapsed(Progress.Elapsed);
            if (Progress.Stage == InventoryJobStage.Hashing)
            {
                double mbps =
                    Progress.Elapsed.TotalSeconds <= 0
                        ? 0
                        : Progress.BytesRead / Progress.Elapsed.TotalSeconds / (1024 * 1024);
                return string.Create(
                    CultureInfo.CurrentCulture,
                    $"{mbps:N1} MB/s | {elapsed} elapsed"
                );
            }
            double entriesPerSecond =
                Progress.Elapsed.TotalSeconds <= 0
                    ? 0
                    : Progress.Scanned / Progress.Elapsed.TotalSeconds;
            return string.Create(
                CultureInfo.CurrentCulture,
                $"{entriesPerSecond:N0} entries/s | {elapsed} elapsed"
            );
        }
    }

    public string LastResultText =>
        LastResult == null
            ? "No inventory job has run in this session."
            : $"{LastResult.Kind}: {LastResult.Outcome}. {LastResult.Message}";

    public string LastResultCounts => LastResult?.CountsText ?? "";

    public string LastResultElapsed =>
        LastResult == null ? "" : $"{FormatElapsed(LastResult.Elapsed)} elapsed";

    internal void Start(InventoryJobKind kind)
    {
        CurrentJobTitle = kind switch
        {
            InventoryJobKind.Scan => "Scan",
            InventoryJobKind.HashNeeded => "Hash needed",
            _ => "Scan then hash",
        };
        OnPropertyChanged(nameof(CurrentJobTitle));
        IsRunning = true;
        CancellationRequested = false;
        Progress = new InventoryJobProgress(InventoryJobStage.Validating, "", TimeSpan.Zero);
    }

    internal void Complete(InventoryJobResult result)
    {
        LastResult = result;
        IsRunning = false;
        CancellationRequested = false;
    }

    private static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalDays >= 1
            ? $"{(int)elapsed.TotalDays}d {elapsed.ToString(@"hh\:mm\:ss", CultureInfo.CurrentCulture)}"
        : elapsed.TotalHours >= 1 ? elapsed.ToString(@"hh\:mm\:ss", CultureInfo.CurrentCulture)
        : elapsed.ToString(@"mm\:ss", CultureInfo.CurrentCulture);
}
