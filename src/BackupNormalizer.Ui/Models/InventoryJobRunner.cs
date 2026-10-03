using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BackupNormalizer.Ui.Models;

/// <summary>Runs one explicitly selected scan or hash job against a local inventory.</summary>
public sealed class InventoryJobRunner
{
    private readonly Func<Database, Scanner> _scannerFactory;

    public InventoryJobRunner() : this(db => new Scanner(db, mftMode: "auto", usnMode: "auto")) { }

    public InventoryJobRunner(Func<Database, Scanner> scannerFactory) => _scannerFactory = scannerFactory;

    public Task<InventoryJobResult> RunAsync(InventoryJobRequest request,
        IProgress<InventoryJobProgress>? progress = null, CancellationToken cancellationToken = default)
        => Task.Run(() => Run(request, progress, cancellationToken), CancellationToken.None);

    private InventoryJobResult Run(InventoryJobRequest request, IProgress<InventoryJobProgress>? progress,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var reporter = new ThrottledReporter(progress);
        InventoryJobProgress latest = new(InventoryJobStage.Validating, "", TimeSpan.Zero);
        void Report(InventoryJobProgress update, bool force = false)
        {
            latest = update;
            reporter.Report(update, force);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(request.DatabasePath) || string.IsNullOrWhiteSpace(request.RootId))
                throw new InvalidOperationException("Select an inventory database root first.");
            if (!Path.IsPathFullyQualified(request.RecordedRootPath))
                throw new DirectoryNotFoundException("The recorded root path is not a fully qualified local path on this computer.");
            string databasePath = Path.GetFullPath(request.DatabasePath);
            string recordedRootPath = Path.GetFullPath(request.RecordedRootPath);
            if (!File.Exists(databasePath))
                throw new FileNotFoundException("The inventory database is no longer available.", databasePath);
            if (!Directory.Exists(recordedRootPath))
                throw new DirectoryNotFoundException($"The recorded root path is not available: {recordedRootPath}");

            StorageRootRow root;
            using (var check = Database.OpenReadOnly(databasePath, pooling: false))
            {
                root = check.GetRoot(request.RootId)
                    ?? throw new InvalidOperationException($"The selected root '{request.RootId}' is no longer in the inventory.");
            }
            if (!Paths.PathEquals(root.Path, recordedRootPath))
                throw new InvalidOperationException("The selected root path changed since this inventory was loaded. Reload the database before running a job.");

            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(databasePath))
                throw new FileNotFoundException("The inventory database is no longer available.", databasePath);
            if (!Directory.Exists(root.Path))
                throw new DirectoryNotFoundException($"The recorded root path is not available: {root.Path}");

            using var db = Database.OpenWritable(databasePath, pooling: false);
            root = db.GetRoot(request.RootId)
                ?? throw new InvalidOperationException($"The selected root '{request.RootId}' is no longer in the inventory.");
            if (!Paths.PathEquals(root.Path, recordedRootPath) || !Directory.Exists(root.Path))
                throw new DirectoryNotFoundException($"The recorded root path is not available: {recordedRootPath}");

            var scanner = _scannerFactory(db);
            int scanned = 0, scanErrors = 0, hashed = 0, skipped = 0, unstable = 0, totalFiles = 0;
            long bytesRead = 0;
            bool incremental = false;
            if (request.Kind is InventoryJobKind.Scan or InventoryJobKind.ScanThenHash)
            {
                Report(latest with { Stage = InventoryJobStage.Scanning, Elapsed = timer.Elapsed }, force: true);
                var scan = scanner.ScanRoot(request.RootId,
                    progress: new CallbackProgress<ScanProgress>(value => Report(new InventoryJobProgress(
                        InventoryJobStage.Scanning, value.CurrentPath, value.Elapsed, value.Scanned, value.Errors, value.Incremental))),
                    full: request.FullScan, cancellationToken: cancellationToken);
                scanned = scan.scanned;
                scanErrors = scan.errors;
                incremental = scanner.LastScanWasIncremental;
                bool scanComplete = db.LatestScanStatus(request.RootId) == ScanStatus.Completed && scanErrors == 0;
                if (!scanComplete)
                {
                    timer.Stop();
                    string message = request.Kind == InventoryJobKind.ScanThenHash
                        ? $"Scan finished with {scanErrors:N0} error(s). Hashing was skipped because the scan is incomplete."
                        : $"Scan finished with {scanErrors:N0} error(s) and is incomplete.";
                    return new InventoryJobResult(request.Kind, InventoryJobOutcome.Incomplete, message, timer.Elapsed,
                        scanned, scanErrors, incremental);
                }
                if (request.Kind == InventoryJobKind.Scan)
                {
                    timer.Stop();
                    return new InventoryJobResult(request.Kind, InventoryJobOutcome.Completed,
                        "Scan completed.", timer.Elapsed, scanned, scanErrors, incremental);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            Report(latest with { Stage = InventoryJobStage.Hashing, Elapsed = timer.Elapsed }, force: true);
            var hash = scanner.HashNeeded(request.RootId, parallelism: Math.Max(1, request.Parallelism),
                progress: new CallbackProgress<HashProgress>(value =>
                {
                    bytesRead = value.BytesRead;
                    totalFiles = value.TotalFiles;
                    Report(new InventoryJobProgress(InventoryJobStage.Hashing, value.CurrentPath, value.Elapsed,
                        scanned, scanErrors, incremental, value.TotalFiles, value.Hashed, value.Skipped,
                        value.Unstable, value.BytesRead));
                }), cancellationToken: cancellationToken);
            hashed = hash.hashed;
            skipped = hash.skipped;
            unstable = hash.unstable;
            var inventoryStatus = db.GetInventoryStatus(request.RootId);
            timer.Stop();
            bool hashesComplete = inventoryStatus.MissingHashes == 0;
            string hashMessage = hashesComplete
                ? "Hash-needed job completed. Every regular file has a current SHA-256 hash."
                : $"Hash-needed job completed with {inventoryStatus.MissingHashes:N0} regular file(s) still missing a usable SHA-256 hash.";
            return new InventoryJobResult(request.Kind,
                hashesComplete ? InventoryJobOutcome.Completed : InventoryJobOutcome.Incomplete,
                hashMessage, timer.Elapsed, scanned, scanErrors, incremental,
                hashed, skipped, unstable, bytesRead, totalFiles);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            timer.Stop();
            string message = latest.Stage switch
            {
                InventoryJobStage.Validating => "Job canceled before filesystem processing started.",
                InventoryJobStage.Scanning => "Scan canceled. The core scanner recorded an incomplete scan.",
                _ => "Hash job canceled. A partial file hash was not stored."
            };
            return new InventoryJobResult(request.Kind, InventoryJobOutcome.Canceled,
                message, timer.Elapsed,
                latest.Scanned, latest.ScanErrors, latest.Incremental, latest.Hashed, latest.Skipped,
                latest.Unstable, latest.BytesRead, latest.TotalFiles);
        }
        catch (Exception ex)
        {
            timer.Stop();
            return new InventoryJobResult(request.Kind, InventoryJobOutcome.Failed, ex.Message, timer.Elapsed,
                latest.Scanned, latest.ScanErrors, latest.Incremental, latest.Hashed, latest.Skipped,
                latest.Unstable, latest.BytesRead, latest.TotalFiles);
        }
    }

    private sealed class CallbackProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class ThrottledReporter(IProgress<InventoryJobProgress>? progress)
    {
        private readonly object _gate = new();
        private long _lastTicks;
        private InventoryJobStage? _lastStage;
        private bool _reportedActivity;
        private bool _reportedHashBytes;
        private bool _reportedHashCompletion;

        public void Report(InventoryJobProgress value, bool force)
        {
            if (progress == null) return;
            lock (_gate)
            {
                long now = Stopwatch.GetTimestamp();
                bool elapsed = _lastTicks == 0 || Stopwatch.GetElapsedTime(_lastTicks, now) >= TimeSpan.FromMilliseconds(100);
                bool stageChanged = value.Stage != _lastStage;
                bool hasActivity = value.Scanned > 0 || value.ScanErrors > 0 || value.BytesRead > 0
                    || value.Hashed > 0 || value.Skipped > 0 || value.Unstable > 0;
                bool firstActivity = hasActivity && !_reportedActivity;
                bool firstHashBytes = value.Stage == InventoryJobStage.Hashing
                    && value.BytesRead > 0 && !_reportedHashBytes;
                int processed = value.Hashed + value.Skipped + value.Unstable;
                bool finalHashProgress = value.Stage == InventoryJobStage.Hashing
                    && value.TotalFiles > 0 && processed >= value.TotalFiles && !_reportedHashCompletion;
                if (!force && !stageChanged && !elapsed && !firstActivity && !firstHashBytes && !finalHashProgress) return;
                if (stageChanged || force)
                {
                    _reportedActivity = false;
                    _reportedHashBytes = false;
                    _reportedHashCompletion = false;
                }
                if (hasActivity) _reportedActivity = true;
                if (value.Stage == InventoryJobStage.Hashing && value.BytesRead > 0) _reportedHashBytes = true;
                if (finalHashProgress) _reportedHashCompletion = true;
                _lastTicks = now;
                _lastStage = value.Stage;
            }
            try { progress.Report(value); }
            catch { }
        }
    }
}
