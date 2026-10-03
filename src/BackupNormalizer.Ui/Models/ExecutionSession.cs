using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BackupNormalizer.Ui.Models;

public sealed record ExecutionSession(
    string DatabasePath,
    PlanDoc Document,
    string SourcePath,
    string TargetPath,
    bool RootsBound,
    IReadOnlyList<Database.PlanOperationRow> Operations,
    IReadOnlyList<ExecutionLogRow> Logs
)
{
    public static ExecutionSession Load(string path)
    {
        using var db = Database.OpenReadOnly(path, pooling: false);
        EnsureJournal(db);
        string planId = db.ListPlanIds().Single();
        var plan = db.GetPlan(planId)!;
        return new ExecutionSession(
            db.DbPath,
            new Planner(db).ExportPlan(planId),
            plan.ExecutionSourceRootPath ?? plan.SourceRootPath,
            plan.ExecutionTargetRootPath ?? plan.TargetRootPath,
            plan.ExecutionTargetRootPath != null,
            db.ListPlanOperations(planId),
            db.ListExecutionLog(planId)
        );
    }

    internal static void EnsureJournal(Database db)
    {
        if (
            db.ListPlanIds().Count != 1
            || db.ListRoots()
                .Any(root => db.CountFiles(root.Id) != 0 || db.GetScanDetails(root.Id) != null)
        )
        {
            throw new InvalidOperationException(
                "Select a dedicated execution database containing one plan, not an inventory database."
            );
        }
    }
}

public sealed record ExecutionRunRequest(
    PlanDoc Document,
    string DatabasePath,
    string SourcePath,
    string TargetPath,
    bool Resume = false,
    bool VerifyOnly = false,
    bool StopOnError = true,
    bool VerifyAfterExecution = true
);

public sealed record ExecutionRunResult(
    string Outcome,
    string Message,
    Executor.ExecSummary? Execution,
    VerificationSummary? Verification,
    ExecutionSession? Session
);

public sealed class ExecutionJobRunner
{
    public Task<ExecutionRunResult> RunAsync(
        ExecutionRunRequest request,
        IProgress<ExecutionProgress>? progress = null,
        CancellationToken cancellationToken = default
    ) => Task.Run(() => Run(request, progress, cancellationToken), CancellationToken.None);

    private static ExecutionRunResult Run(
        ExecutionRunRequest request,
        IProgress<ExecutionProgress>? progress,
        CancellationToken cancellationToken
    )
    {
        Executor.ExecSummary? execution = null;
        VerificationSummary? verification = null;
        string path = request.DatabasePath;
        try
        {
            path = Path.GetFullPath(path);
            cancellationToken.ThrowIfCancellationRequested();
            if (
                !Path.IsPathFullyQualified(request.TargetPath)
                || !Directory.Exists(request.TargetPath)
            )
            {
                throw new DirectoryNotFoundException("Select an existing local target root.");
            }

            bool readsSource = request.Document.Operations.Any(op =>
                op.SourceKind == SourceScope.Source && op.Type != OpType.SkipLink
            );
            if (
                readsSource
                && !request.VerifyOnly
                && (
                    !Path.IsPathFullyQualified(request.SourcePath)
                    || !Directory.Exists(request.SourcePath)
                )
            )
            {
                throw new DirectoryNotFoundException(
                    "The copy source root is unavailable on this computer."
                );
            }

            if (
                Paths.RootsOverlap(path, request.TargetPath)
                || (
                    Path.IsPathFullyQualified(request.SourcePath)
                    && Paths.RootsOverlap(path, request.SourcePath)
                )
            )
            {
                throw new InvalidOperationException(
                    "Store the execution database outside the source and target trees."
                );
            }

            if (readsSource && Paths.RootsOverlap(request.SourcePath, request.TargetPath))
            {
                throw new InvalidOperationException("Source and target roots must be disjoint.");
            }

            bool existing = File.Exists(path);
            if (existing)
            {
                if (!request.Resume && !request.VerifyOnly)
                {
                    throw new InvalidOperationException(
                        "The execution database already exists. Open it to resume, or choose a new path."
                    );
                }

                var session = ExecutionSession.Load(path);
                if (
                    session.Document.PlanId != request.Document.PlanId
                    || session.Document.SourceRoot != request.Document.SourceRoot
                    || session.Document.TargetRoot != request.Document.TargetRoot
                    || !session.Document.Operations.SequenceEqual(
                        request
                            .Document.Operations.OrderBy(op => op.Id)
                            .Select(op =>
                                op with
                                {
                                    SourceKind = op.SourceKind ?? SourceScope.Target,
                                }
                            )
                    )
                )
                {
                    throw new InvalidOperationException(
                        "The reviewed plan does not match this execution database."
                    );
                }

                if (
                    session.RootsBound
                    && (
                        !Paths.PathEquals(session.TargetPath, request.TargetPath)
                        || (
                            readsSource && !Paths.PathEquals(session.SourcePath, request.SourcePath)
                        )
                    )
                )
                {
                    throw new InvalidOperationException(
                        "This execution is bound to its original roots. Use a new journal to replay on another drive."
                    );
                }
            }
            else
            {
                if (request.Resume || request.VerifyOnly)
                {
                    throw new FileNotFoundException(
                        "The execution journal is unavailable. Choose a new journal to start a new execution."
                    );
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using (new FileStream(path, FileMode.CreateNew, FileAccess.Write)) { }
            }

            using (var db = Database.OpenWritable(path, pooling: false))
            {
                if (!existing)
                {
                    var localPlan = request.Document with
                    {
                        SourcePath = request.SourcePath,
                        TargetPath = request.TargetPath,
                    };
                    PlanStaging.WriteToDatabase(
                        db,
                        localPlan,
                        localPlan.TargetRoot,
                        request.TargetPath
                    );
                }
                if (!request.VerifyOnly)
                {
                    execution = new Executor(db).Execute(
                        request.Document.PlanId,
                        request.SourcePath,
                        request.TargetPath,
                        request.Resume,
                        request.StopOnError,
                        progress,
                        cancellationToken
                    );
                }

                if (
                    !cancellationToken.IsCancellationRequested
                    && (
                        request.VerifyOnly
                        || (
                            request.VerifyAfterExecution
                            && execution is { Failed: 0, Conflicts: 0, Canceled: false }
                        )
                    )
                )
                {
                    verification = PlanVerifier.Verify(
                        db,
                        request.Document.PlanId,
                        request.SourcePath,
                        request.TargetPath,
                        progress,
                        cancellationToken
                    );
                }
            }
            var final = ExecutionSession.Load(path);
            bool canceled =
                cancellationToken.IsCancellationRequested
                || execution?.Canceled == true
                || verification?.Canceled == true;
            bool failed =
                execution is { Failed: > 0 } or { Conflicts: > 0 } || verification is { Bad: > 0 };
            string outcome =
                canceled ? "Canceled"
                : failed ? "Partial"
                : "Completed";
            string message =
                canceled
                    ? "Stopped at an operation boundary. Completed operations are saved; open this execution database to resume."
                : failed
                    ? "Execution or verification reported problems. Review operation errors before retrying."
                : verification != null
                    ? $"Verification finished: {verification.Ok:N0} file destinations verified, {verification.Skipped:N0} operations skipped."
                : "Execution completed. Verification has not been run.";
            return new ExecutionRunResult(outcome, message, execution, verification, final);
        }
        catch (Exception ex)
        {
            ExecutionSession? session = null;
            try
            {
                if (File.Exists(path))
                {
                    session = ExecutionSession.Load(path);
                }
            }
            catch { }
            return new ExecutionRunResult(
                ex is OperationCanceledException ? "Canceled" : "Failed",
                ex.Message,
                execution,
                verification,
                session
            );
        }
    }
}
