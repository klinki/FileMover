using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BackupNormalizer;
using BackupNormalizer.Ui.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BackupNormalizer.Ui.ViewModels;

public sealed class DatabasePlanReviewViewModel : ObservableObject
{
    private readonly string _targetDatabasePath;
    private string _statusMessage =
        "Review the complete selected roots before exporting this plan.";

    public PlanDoc Document { get; }
    public IReadOnlyList<DatabasePlanOperation> Operations { get; }
    public IReadOnlyList<DatabasePlanIssue> Issues { get; }
    public DatabasePlanDirection Direction { get; }
    public string DirectionLabel =>
        Direction == DatabasePlanDirection.LeftToRight ? "Left → Right" : "Right → Left";
    public string ScopeLabel =>
        "Complete selected inventory roots; panel folder navigation is ignored.";
    public string PlanId => Document.PlanId;
    public string SourceDatabasePath => Document.SourceDatabasePath ?? "";
    public string SourceRoot => $"{Document.SourceRoot} - {Document.SourcePath}";
    public string TargetRoot => $"{Document.TargetRoot} - {Document.TargetPath}";
    public long EstimatedBytesCopied => Document.EstimatedBytesCopied;
    public string EstimatedBytesCopiedText => EstimatedBytesCopied.ToString("N0");
    public int KeepCount => Count(OpType.Keep);
    public int MoveCount => Count(OpType.Move);
    public int CopyCount => Count(OpType.Copy);
    public int TrashCount => Count(OpType.Trash);
    public int VerifyCount => Count(OpType.Verify);
    public int DirectoryCount => Count(OpType.Mkdir);
    public int SkippedLinkCount => Count(OpType.SkipLink);
    public int TypeConflictCount => Issues.Count(issue => issue.Kind == "Type conflict");
    public int ContentConflictCount => Issues.Count(issue => issue.Kind == "Content conflict");
    public int UnverifiedTargetCount => Issues.Count(issue => issue.Kind == "Unverified target");
    public int SkippedLinkIssueCount => Issues.Count(issue => issue.Kind == "Skipped link");
    public bool HasBlockingIssues => Issues.Any(issue => issue.BlocksExport);
    public bool CanExportJson => !HasBlockingIssues;
    public string PlanSummary =>
        $"Plan {PlanId} | KEEP {KeepCount} | MOVE {MoveCount} | COPY {CopyCount} | MKDIR {DirectoryCount} | TRASH {TrashCount} | VERIFY {VerifyCount} | SKIP_LINK {SkippedLinkCount} | bytes to copy: {EstimatedBytesCopiedText}";
    public string IssueSummary =>
        Issues.Count == 0
            ? "No path or content conflicts were found. Review skipped links and operations before exporting."
            : $"Issues: type conflicts {TypeConflictCount}, content conflicts {ContentConflictCount}, unverified target files {UnverifiedTargetCount}, link paths skipped {SkippedLinkIssueCount}.";
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string SuggestedJsonName
    {
        get
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            string safePlanId = new(
                PlanId.Select(character => invalid.Contains(character) ? '_' : character).ToArray()
            );
            return $"{safePlanId}.json";
        }
    }

    public DatabasePlanReviewViewModel(
        DatabasePlanArtifact artifact,
        DatabasePlanDirection direction
    )
    {
        Document = artifact.Document;
        Operations = artifact.Operations;
        Issues = artifact.Issues;
        _targetDatabasePath = artifact.TargetDatabasePath;
        Direction = direction;
    }

    public void ExportJson(string destinationPath)
    {
        if (!CanExportJson)
        {
            StatusMessage = "Resolve the blocking type conflicts before exporting this plan.";
            return;
        }
        try
        {
            string fullPath = Path.GetFullPath(destinationPath);
            if (IsInventoryDatabaseOrCompanion(fullPath))
            {
                StatusMessage =
                    "Cannot export plan JSON over a selected inventory database or its SQLite companion file.";
                return;
            }
            string? directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                throw new DirectoryNotFoundException("The selected export folder does not exist.");
            }

            string temporaryPath = Path.Combine(
                directory,
                ".bn-plan-export-" + Guid.NewGuid().ToString("N") + ".tmp"
            );
            try
            {
                File.WriteAllText(temporaryPath, Planner.ToJson(Document));
                File.Move(temporaryPath, fullPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            StatusMessage = $"Saved executor-compatible plan JSON to {fullPath}.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Plan JSON export failed: " + ex.Message;
        }
    }

    public void ReportExportError(string message) => StatusMessage = message;

    private bool IsInventoryDatabaseOrCompanion(string destinationPath)
    {
        string[] databases = { Document.SourceDatabasePath ?? "", _targetDatabasePath };
        foreach (string database in databases.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            foreach (string suffix in new[] { "", "-wal", "-shm", "-journal" })
            {
                if (Paths.PathEquals(destinationPath, database + suffix))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private int Count(string operationType) =>
        Operations.Count(operation => operation.Type == operationType);
}
