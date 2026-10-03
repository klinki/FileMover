using System.Diagnostics;

namespace BackupNormalizer;

public sealed record VerificationIssue(int OperationId, string Path, string Message);

public sealed record VerificationSummary(
    int Ok,
    int Bad,
    int Skipped,
    bool Canceled,
    IReadOnlyList<VerificationIssue> Issues
);

public static class PlanVerifier
{
    public static VerificationSummary Verify(
        Database db,
        string planId,
        string? sourcePath = null,
        string? targetPath = null,
        IProgress<ExecutionProgress>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plan =
            db.GetPlan(planId) ?? throw new InvalidOperationException($"Unknown plan '{planId}'.");
        sourcePath = Path.GetFullPath(
            sourcePath ?? plan.ExecutionSourceRootPath ?? plan.SourceRootPath
        );
        targetPath = Path.GetFullPath(
            targetPath ?? plan.ExecutionTargetRootPath ?? plan.TargetRootPath
        );
        var operations = db.ListPlanOperations(planId);
        if (
            operations.Any(op => op.SourceKind == SourceScope.Source && op.Type != OpType.SkipLink)
            && Paths.RootsOverlap(sourcePath, targetPath)
        )
        {
            throw new InvalidOperationException(
                "Source and target paths overlap; verification requires disjoint roots."
            );
        }

        var issues = new List<VerificationIssue>();
        var consumedPaths = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal
        );
        var superseded = new HashSet<long>();
        foreach (var operation in operations.AsEnumerable().Reverse())
        {
            if (
                operation.DestRoot == plan.TargetRootId
                && operation.DestPath != null
                && consumedPaths.Contains(Paths.NormalizeRelative(operation.DestPath))
            )
            {
                superseded.Add(operation.Id);
            }

            if (
                operation.Status == OpStatus.Completed
                && operation.SourceKind == SourceScope.Target
                && operation.SourceRoot == plan.TargetRootId
                && operation.Type is OpType.Move or OpType.Trash
                && operation.SourcePath != null
            )
            {
                consumedPaths.Add(Paths.NormalizeRelative(operation.SourcePath));
            }
        }
        var hasher = HasherFactory.Create("sha256");
        var timer = Stopwatch.StartNew();
        int ok = 0,
            bad = 0,
            skipped = 0,
            position = 0;
        long bytesRead = 0;
        TimeSpan lastReport = TimeSpan.Zero;
        foreach (var operation in operations)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            position++;
            string path = operation.DestPath ?? operation.SourcePath ?? "";
            void Report(string status, string? message = null) =>
                progress?.Report(
                    new ExecutionProgress(
                        position,
                        operations.Count,
                        operation.Type,
                        path,
                        status,
                        ok,
                        bad,
                        skipped,
                        0,
                        0,
                        bytesRead,
                        timer.Elapsed,
                        message
                    )
                );
            Report("Verifying");
            if (
                operation.Type == OpType.SkipLink
                || operation.Status == OpStatus.Skipped
                || operation.Type
                    is not (OpType.Keep or OpType.Verify or OpType.Move or OpType.Copy)
                || superseded.Contains(operation.Id)
            )
            {
                skipped++;
                Report("Verification skipped", "No final file destination to verify.");
                continue;
            }
            try
            {
                if (operation.DestRoot != plan.TargetRootId || operation.DestPath == null)
                {
                    throw new InvalidOperationException("Destination is outside the plan target.");
                }

                string absolute = Paths.CombineRoot(targetPath, operation.DestPath);
                if (Paths.FindLink(targetPath, operation.DestPath) is { } link)
                {
                    skipped++;
                    Report("Verification skipped", $"Destination is blocked by link '{link}'.");
                    continue;
                }
                if (!File.Exists(absolute))
                {
                    throw new IOException("MISSING: destination file is unavailable.");
                }

                var file = new FileInfo(absolute);
                if (operation.ExpectedSize != 0 && file.Length != operation.ExpectedSize)
                {
                    throw new IOException("SIZE-MISMATCH: destination size changed.");
                }

                if (operation.ExpectedHash != null)
                {
                    string digest = hasher.HashFile(
                        absolute,
                        file.Length,
                        count =>
                        {
                            bytesRead += count;
                            if (timer.Elapsed - lastReport >= TimeSpan.FromMilliseconds(100))
                            {
                                lastReport = timer.Elapsed;
                                Report("Verifying");
                            }
                        }
                    );
                    if (!StringComparer.OrdinalIgnoreCase.Equals(digest, operation.ExpectedHash))
                    {
                        throw new IOException("HASH-MISMATCH: destination content changed.");
                    }
                }
                ok++;
                Report("Verified");
            }
            catch (Exception ex)
            {
                bad++;
                issues.Add(new VerificationIssue(operation.Sequence, path, ex.Message));
                Report("Verification failed", ex.Message);
            }
        }
        return new VerificationSummary(
            ok,
            bad,
            skipped,
            cancellationToken.IsCancellationRequested,
            issues
        );
    }
}
