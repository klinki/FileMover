using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using BackupNormalizer.Ui.Models;

namespace BackupNormalizer.Ui.ViewModels;

public sealed class InventoryHealthViewModel
{
    public string RootName { get; }
    public string RecordedPath { get; }
    public string ScanStatus { get; }
    public string ScanAge { get; }
    public string StartedAt { get; }
    public string CompletedAt { get; }
    public string ScanMode { get; }
    public string FallbackReason { get; }
    public string ScannedCount { get; }
    public string ScanErrorCount { get; }
    public string RegularFiles { get; }
    public string Links { get; }
    public string EntryErrors { get; }
    public string MissingEntries { get; }
    public string HashReadiness { get; }
    public string PlanningReadiness { get; }
    public string PlanningBlocker { get; }
    public string DiagnosticsSummary { get; }
    public ObservableCollection<InventoryHealthErrorItem> Errors { get; }

    public InventoryHealthViewModel(InventoryRoot root, DateTimeOffset? now = null)
    {
        RootName = root.Root.Name;
        RecordedPath = root.Root.Path;

        var health = root.HealthStatus;
        if (health == null)
        {
            ScanStatus = "Health details unavailable";
            ScanAge = StartedAt = CompletedAt = ScanMode = FallbackReason = "Unknown";
            ScannedCount = ScanErrorCount = "Unknown";
            RegularFiles = Links = EntryErrors = MissingEntries = "Unknown";
            HashReadiness = "Hash status unavailable";
            PlanningReadiness = "Unknown";
            PlanningBlocker = "Health details are unavailable for this root.";
            DiagnosticsSummary = "Diagnostic details are unavailable.";
            Errors = [];
            return;
        }

        var latest = health.LatestScan;
        ScanStatus = latest?.Scan.Status ?? "Never scanned";
        StartedAt = FormatTime(latest?.Scan.StartedUtc);
        CompletedAt = FormatTime(latest?.Scan.CompletedUtc);
        var scanTime = ParseTime(latest?.Scan.CompletedUtc) ?? ParseTime(latest?.Scan.StartedUtc);
        ScanAge = FormatAge(scanTime, now ?? DateTimeOffset.Now);
        ScanMode = FormatMode(latest?.Mode);
        FallbackReason = latest?.FallbackReason ?? (latest?.Mode == null
            ? "Not recorded by this inventory version"
            : "None recorded");
        ScannedCount = FormatLegacyCount(latest?.ScannedCount);
        ScanErrorCount = FormatLegacyCount(latest?.ErrorCount);
        RegularFiles = health.RegularFiles.ToString("N0", CultureInfo.CurrentCulture);
        Links = health.Links.ToString("N0", CultureInfo.CurrentCulture);
        EntryErrors = health.EntryErrors.ToString("N0", CultureInfo.CurrentCulture);
        MissingEntries = health.MissingEntries.ToString("N0", CultureInfo.CurrentCulture);
        HashReadiness = $"{health.UsableHashes.ToString("N0", CultureInfo.CurrentCulture)} of " +
            $"{health.RegularFiles.ToString("N0", CultureInfo.CurrentCulture)} regular files have a current SHA-256 hash";
        PlanningReadiness = health.PlanningReady ? "Ready" : "Blocked";
        PlanningBlocker = health.BlockingReason ?? "No planning blockers.";
        DiagnosticsSummary = latest == null ? "No scan diagnostics." : latest.ErrorCount == null
            ? "Diagnostic count was not recorded by this inventory version."
            : latest.ErrorCount == 0 ? "No scan errors recorded."
            : $"{latest.ErrorCount.Value.ToString("N0", CultureInfo.CurrentCulture)} scan error(s) recorded.";
        Errors = new ObservableCollection<InventoryHealthErrorItem>(health.Errors.Select(error =>
            new InventoryHealthErrorItem(error.Path, error.Message, FormatTime(error.RecordedUtc))));
    }

    private static string FormatLegacyCount(int? count) => count?.ToString("N0", CultureInfo.CurrentCulture)
        ?? "Unknown (older inventory)";

    private static string FormatMode(string? mode) => mode switch
    {
        "USN" => "USN incremental scan",
        "Recursive" => "Full scan (recursive)",
        "MFT" => "Full scan (MFT)",
        "NotStarted" => "Not started",
        null or "" => "Unknown (older inventory)",
        _ => mode
    };

    private static string FormatTime(string? value)
    {
        var parsed = ParseTime(value);
        return parsed?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "Unavailable";
    }

    private static DateTimeOffset? ParseTime(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;

    private static string FormatAge(DateTimeOffset? scanTime, DateTimeOffset now)
    {
        if (scanTime == null) return "Unavailable";
        var age = now - scanTime.Value;
        if (age < TimeSpan.Zero) return "Scan time is in the future";
        if (age < TimeSpan.FromMinutes(1)) return "Under a minute ago";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes:N0} minutes ago";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours:N0} hours ago";
        return $"{(int)age.TotalDays:N0} days ago";
    }
}

public sealed record InventoryHealthErrorItem(string Path, string Message, string RecordedAt);
