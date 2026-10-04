using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BackupNormalizer.Ui.ViewModels;

public sealed record LocationRootOption(
    LocationChangeInput Input,
    string Name,
    string RootPath,
    string? ScanStatus,
    string? ScannedUtc,
    long Files,
    long UsableHashes
)
{
    public string Display => $"{Name} [{Input.RootId}]";
    public string Details =>
        $"{RootPath}\nScan: {ScanStatus ?? "not scanned"} | {ScannedUtc ?? "date unknown"} | SHA-256: {UsableHashes:N0}/{Files:N0}";
}

public sealed record LocationChangeItem(LocationChangeGroup Group)
{
    public string Classification => Group.Classification;
    public long Size => Group.Size;
    public string SizeText =>
        Group.ContentComparison is { } comparison
            ? $"{comparison.BeforeSize:N0} → {comparison.AfterSize:N0}"
            : Size.ToString("N0");
    public string PathA { get; } = PathSummary(Group, "A");
    public string PathB { get; } = PathSummary(Group, "B");
    public string Counts { get; } =
        $"{Group.Locations.Count(l => l.Side == "A")} / {Group.Locations.Count(l => l.Side == "B")}";
    public string Details { get; } =
        string.Join(
            Environment.NewLine,
            Group
                .Locations.Select(l =>
                    $"{l.Side} | {l.State} | {l.RelativePath}"
                    + $" | {l.Size ?? Group.Size:N0} bytes | SHA-256: {l.Digest ?? Group.Digest ?? "unavailable"}"
                )
                .Prepend(Group.VerificationReason)
                .Where(s => s != null)
        );

    private static string PathSummary(LocationChangeGroup group, string side)
    {
        var paths = group
            .Locations.Where(l => l.Side == side)
            .Select(l => l.RelativePath)
            .ToArray();
        return paths.Length == 1 ? paths[0]
            : paths.Length == 0 ? ""
            : $"{paths.Length:N0} locations (select for details)";
    }
}

public sealed partial class LocationChangesViewModel : ObservableObject, IDisposable
{
    private readonly Func<
        LocationChangeInput,
        LocationChangeInput,
        FileDifferenceOptions,
        CancellationToken,
        IProgress<string>?,
        LocationChangesReport
    > _analyze;
    private CancellationTokenSource? _work;
    private bool _disposed;
    private bool _updatingSources;

    public LocationChangesViewModel() => _analyze = FileLocationChanges.Analyze;

    public LocationChangesViewModel(
        Func<
            LocationChangeInput,
            LocationChangeInput,
            CancellationToken,
            IProgress<string>?,
            LocationChangesReport
        > analyze
    ) => _analyze = (a, b, options, token, progress) => analyze(a, b, token, progress);

    [ObservableProperty]
    public partial bool MatchFilenames { get; set; }

    [ObservableProperty]
    public partial string Extensions { get; set; } = ".zip,.mp4";

    [ObservableProperty]
    public partial int ReportView { get; set; }

    [ObservableProperty]
    public partial string DuplicateSide { get; set; } = "B";

    public IReadOnlyList<string> DuplicateSides { get; } = new[] { "A", "B" };

    [ObservableProperty]
    public partial IReadOnlyList<GroupedFileItem> GroupedEntries { get; set; } =
        Array.Empty<GroupedFileItem>();

    [ObservableProperty]
    public partial GroupedFileItem? SelectedGroupedEntry { get; set; }

    public string SelectedDetails =>
        (ReportView == 0 ? SelectedEntry?.Details : SelectedGroupedEntry?.Details) ?? "";

    [ObservableProperty]
    public partial string DatabaseA { get; set; } = "Select inventory A";

    [ObservableProperty]
    public partial string DatabaseB { get; set; } = "Select inventory B";

    [ObservableProperty]
    public partial IReadOnlyList<LocationRootOption> RootsA { get; set; } =
        Array.Empty<LocationRootOption>();

    [ObservableProperty]
    public partial IReadOnlyList<LocationRootOption> RootsB { get; set; } =
        Array.Empty<LocationRootOption>();

    [ObservableProperty]
    public partial LocationRootOption? SelectedA { get; set; }

    [ObservableProperty]
    public partial LocationRootOption? SelectedB { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } =
        "Select two inventory roots, then compare recorded content and locations.";

    [ObservableProperty]
    public partial string Summary { get; set; } = "No differences report yet.";

    [ObservableProperty]
    public partial string Filter { get; set; } = "Quick differences";

    [ObservableProperty]
    public partial string LocationFilter { get; set; } = "Quick differences";

    [ObservableProperty]
    public partial IReadOnlyList<LocationChangeItem> Entries { get; set; } =
        Array.Empty<LocationChangeItem>();

    [ObservableProperty]
    public partial LocationChangeItem? SelectedEntry { get; set; }
    public IReadOnlyList<string> Filters { get; } =
        FileLocationChanges.Filters.Where(f => !GroupedFileReports.IsGroupedView(f)).ToArray();
    public LocationChangesReport? Report { get; private set; }
    public Task FilterTask { get; private set; } = Task.CompletedTask;
    public bool CanEdit => !IsBusy && !_disposed;
    public bool CanAnalyze => CanEdit && SelectedA != null && SelectedB != null;
    public bool CanExport =>
        CanEdit
        && Report != null
        && (Filter != GroupedFileReports.FilenameDifferences || Report.FilenameMatchingEnabled);
    public bool CanCancel => IsBusy && _work != null;

    public async Task LoadAsync(string side, string path, string? rootId = null)
    {
        if (!CanEdit)
            return;
        if (side is not ("A" or "B"))
            throw new ArgumentException("Inventory side must be A or B.");
        ClearReport();
        using var work = BeginWork($"Loading inventory {side}...");
        try
        {
            var roots = await Task.Run(() => LoadRoots(path, work.Token), work.Token);
            work.Token.ThrowIfCancellationRequested();
            SetRoots(side, roots, rootId);
            Status = "Inventory loaded. Analyze to compare recorded content and locations.";
        }
        catch (OperationCanceledException)
        {
            Status = "Loading cancelled.";
        }
        catch (Exception ex)
        {
            Status = "Cannot load inventory: " + ex.Message;
        }
        finally
        {
            EndWork();
        }
    }

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    public async Task Analyze()
    {
        if (!CanAnalyze)
            return;
        var a = SelectedA!.Input;
        var b = SelectedB!.Input;
        ClearReport();
        using var work = BeginWork("Analyzing recorded content and locations...");
        var progress = new Progress<string>(message =>
        {
            if (_work == work)
                Status = message;
        });
        try
        {
            var options = new FileDifferenceOptions(MatchFilenames, Extensions);
            var report = await Task.Run(
                () => _analyze(a, b, options, work.Token, progress),
                work.Token
            );
            var rows = await BuildRowsAsync(report, work.Token);
            work.Token.ThrowIfCancellationRequested();
            UpdateSourceMetadata(report);
            Report = report;
            Entries = rows.Rows;
            GroupedEntries = rows.Tree;
            Summary =
                $"Path/location groups: {report.Groups.Count:N0}"
                + $" | Filename groups: {report.FilenameGroups.Count:N0} | Duplicates A/B: {report.DuplicatesA.Count:N0}/{report.DuplicatesB.Count:N0}"
                + $" | Unverified files: {report.UnverifiedFiles:N0}";
            Status = "Recorded differences in A → B. Select a group to inspect paths and content.";
        }
        catch (OperationCanceledException)
        {
            ClearReport();
            Status = "Analysis cancelled. No partial report is available.";
        }
        catch (Exception ex)
        {
            ClearReport();
            Status = "File comparison failed: " + ex.Message;
        }
        finally
        {
            EndWork();
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    public void Swap()
    {
        if (!CanEdit)
            return;
        (DatabaseA, DatabaseB) = (DatabaseB, DatabaseA);
        (RootsA, RootsB) = (RootsB, RootsA);
        (SelectedA, SelectedB) = (SelectedB, SelectedA);
        ClearReport();
        Status = "Direction swapped. Analyze to refresh the report.";
    }

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    public async Task Refresh()
    {
        if (!CanAnalyze)
            return;
        var a = SelectedA!.Input;
        var b = SelectedB!.Input;
        ClearReport();
        using var work = BeginWork("Refreshing both inventories...");
        try
        {
            var roots = await Task.Run(
                () =>
                    (
                        A: LoadRoots(a.DatabasePath, work.Token),
                        B: LoadRoots(b.DatabasePath, work.Token)
                    ),
                work.Token
            );
            work.Token.ThrowIfCancellationRequested();
            SetRoots("A", roots.A, a.RootId);
            SetRoots("B", roots.B, b.RootId);
            Status = "Inventories refreshed. Analyze to create a new report.";
        }
        catch (OperationCanceledException)
        {
            Status = "Refresh cancelled.";
        }
        catch (Exception ex)
        {
            Status = "Cannot refresh inventories: " + ex.Message;
        }
        finally
        {
            EndWork();
        }
    }

    public async Task ExportAsync(string path, string format)
    {
        if (!CanExport)
            return;
        var report = Report!;
        string filter = Filter;
        using var work = BeginWork("Exporting differences report...");
        try
        {
            await Task.Run(
                () => LocationChangesExport.Save(path, report, format, filter, work.Token),
                work.Token
            );
            Status = "File differences report exported to " + path;
        }
        catch (OperationCanceledException)
        {
            Status = "Export cancelled.";
        }
        catch (Exception ex)
        {
            Status = "Cannot export report: " + ex.Message;
        }
        finally
        {
            EndWork();
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    public void Cancel() => _work?.Cancel();

    partial void OnSelectedAChanged(LocationRootOption? value) => SourceChanged();

    partial void OnSelectedBChanged(LocationRootOption? value) => SourceChanged();

    partial void OnMatchFilenamesChanged(bool value) => SourceChanged();

    partial void OnExtensionsChanged(string value) => SourceChanged();

    partial void OnSelectedEntryChanged(LocationChangeItem? value) =>
        OnPropertyChanged(nameof(SelectedDetails));

    partial void OnSelectedGroupedEntryChanged(GroupedFileItem? value) =>
        OnPropertyChanged(nameof(SelectedDetails));

    partial void OnReportViewChanged(int value)
    {
        if (value == 1)
            Filter = GroupedFileReports.FilenameDifferences;
        else if (value == 2)
            Filter =
                DuplicateSide == "A"
                    ? GroupedFileReports.DuplicatesInA
                    : GroupedFileReports.DuplicatesInB;
        else
            Filter = LocationFilter;
        OnPropertyChanged(nameof(SelectedDetails));
    }

    partial void OnDuplicateSideChanged(string value)
    {
        if (ReportView == 2)
            Filter =
                value == "A" ? GroupedFileReports.DuplicatesInA : GroupedFileReports.DuplicatesInB;
    }

    partial void OnLocationFilterChanged(string value)
    {
        if (ReportView == 0)
            Filter = value;
    }

    partial void OnIsBusyChanged(bool value) => NotifyCommands();

    partial void OnFilterChanged(string value)
    {
        int view =
            value == GroupedFileReports.FilenameDifferences ? 1
            : GroupedFileReports.IsGroupedView(value) ? 2
            : 0;
        if (view == 2)
            DuplicateSide = value == GroupedFileReports.DuplicatesInA ? "A" : "B";
        if (view == 0 && LocationFilter != value)
            LocationFilter = value;
        if (ReportView != view)
            ReportView = view;
        OnPropertyChanged(nameof(CanExport));
        if (CanEdit && Report != null)
            FilterTask = ApplyFilterAsync();
    }

    private async Task ApplyFilterAsync()
    {
        var report = Report!;
        using var work = BeginWork("Filtering recorded locations...");
        try
        {
            var rows = await BuildRowsAsync(report, work.Token);
            work.Token.ThrowIfCancellationRequested();
            if (Report != report)
                return;
            SelectedEntry = null;
            SelectedGroupedEntry = null;
            Entries = rows.Rows;
            GroupedEntries = rows.Tree;
            Status = "Select a group to inspect every recorded location.";
        }
        catch (OperationCanceledException)
        {
            ClearReport();
            Status = "Filtering cancelled. Analyze to rebuild the report.";
        }
        catch (Exception ex)
        {
            ClearReport();
            Status = "Cannot filter report: " + ex.Message;
        }
        finally
        {
            EndWork();
        }
    }

    private async Task<(LocationChangeItem[] Rows, GroupedFileItem[] Tree)> BuildRowsAsync(
        LocationChangesReport report,
        CancellationToken token
    )
    {
        while (true)
        {
            string filter = Filter;
            var rows = await Task.Run(
                () =>
                    GroupedFileReports.IsGroupedView(filter)
                        ? (
                            Rows: Array.Empty<LocationChangeItem>(),
                            Tree: GroupedFileItem.Build(report, filter, token)
                        )
                        : (
                            Rows: FileLocationChanges
                                .Filter(report, filter, token)
                                .Select(g =>
                                {
                                    token.ThrowIfCancellationRequested();
                                    return new LocationChangeItem(g);
                                })
                                .ToArray(),
                            Tree: Array.Empty<GroupedFileItem>()
                        ),
                token
            );
            if (filter == Filter)
                return rows;
        }
    }

    private static LocationRootOption[] LoadRoots(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var db = Database.OpenReadOnly(path, pooling: false);
        var roots = new List<LocationRootOption>();
        foreach (var root in db.ListRoots())
        {
            token.ThrowIfCancellationRequested();
            var health = db.GetInventoryStatus(root.Id);
            roots.Add(
                new LocationRootOption(
                    new(db.DbPath, root.Id),
                    root.Name,
                    root.Path,
                    health.LatestScan?.Scan.Status,
                    health.LatestScan?.Scan.CompletedUtc,
                    health.RegularFiles,
                    health.UsableHashes
                )
            );
        }
        if (roots.Count == 0)
            throw new InvalidOperationException("Database contains no storage roots.");
        return roots.ToArray();
    }

    private void SetRoots(string side, LocationRootOption[] roots, string? rootId)
    {
        var selected = roots.FirstOrDefault(r => r.Input.RootId == rootId) ?? roots[0];
        _updatingSources = true;
        try
        {
            if (side == "A")
            {
                DatabaseA = roots[0].Input.DatabasePath;
                RootsA = roots;
                SelectedA = selected;
            }
            else
            {
                DatabaseB = roots[0].Input.DatabasePath;
                RootsB = roots;
                SelectedB = selected;
            }
        }
        finally
        {
            _updatingSources = false;
        }
    }

    private void UpdateSourceMetadata(LocationChangesReport report)
    {
        _updatingSources = true;
        try
        {
            foreach (var source in new[] { report.A, report.B })
            {
                bool isA = ReferenceEquals(source, report.A);
                var selected = isA ? SelectedA! : SelectedB!;
                var updated = selected with
                {
                    ScannedUtc = source.ScannedUtc,
                    ScanStatus = source.ScanStatus,
                    Files = source.Files,
                    UsableHashes = source.UsableHashes,
                };
                if (isA)
                {
                    RootsA = RootsA.Select(r => r == selected ? updated : r).ToArray();
                    SelectedA = updated;
                }
                else
                {
                    RootsB = RootsB.Select(r => r == selected ? updated : r).ToArray();
                    SelectedB = updated;
                }
            }
        }
        finally
        {
            _updatingSources = false;
        }
    }

    private void SourceChanged()
    {
        if (!_updatingSources)
        {
            _work?.Cancel();
            ClearReport();
        }
        NotifyCommands();
    }

    private CancellationTokenSource BeginWork(string status)
    {
        _work = new CancellationTokenSource();
        IsBusy = true;
        Status = status;
        return _work;
    }

    private void EndWork()
    {
        _work = null;
        IsBusy = false;
        NotifyCommands();
    }

    private void ClearReport()
    {
        Report = null;
        Entries = Array.Empty<LocationChangeItem>();
        GroupedEntries = Array.Empty<GroupedFileItem>();
        SelectedEntry = null;
        SelectedGroupedEntry = null;
        Summary = "No differences report yet.";
        OnPropertyChanged(nameof(CanExport));
    }

    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanAnalyze));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(CanCancel));
        AnalyzeCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
        SwapCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        _disposed = true;
        _work?.Cancel();
        ClearReport();
        NotifyCommands();
    }
}
