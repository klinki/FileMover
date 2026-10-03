using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BackupNormalizer;

namespace BackupNormalizer.Ui.Models;

public enum DatabasePlanDirection
{
    LeftToRight,
    RightToLeft,
}

public sealed record DatabasePlanRequest(
    string SourceDatabasePath,
    string SourceRootId,
    string TargetDatabasePath,
    string TargetRootId,
    string PlanId,
    DatabasePlanDirection Direction);

public sealed record DatabasePlanIssue(string RelativePath, string Kind, string Message, bool BlocksExport)
{
    public string Display => $"{Kind}{(BlocksExport ? " - BLOCKS EXPORT" : "")}{(RelativePath.Length > 0 ? $" - {RelativePath}" : "")}: {Message}";
}

public sealed record DatabasePlanOperation(
    int Id,
    string Type,
    string Source,
    string Destination,
    long Size,
    string Hash,
    string Note,
    bool IsConflict)
{
    public string SizeText => Size == 0 ? "" : Size.ToString("N0");
}

/// <summary>A self-contained plan review. Its temporary planning database is removed before this is returned.</summary>
public sealed class DatabasePlanArtifact
{
    public PlanDoc Document { get; }
    public Planner.PlanResult Result { get; }
    public IReadOnlyList<DatabasePlanOperation> Operations { get; }
    public IReadOnlyList<DatabasePlanIssue> Issues { get; }
    public string TargetDatabasePath { get; }

    private DatabasePlanArtifact(string targetDatabasePath, PlanDoc document, Planner.PlanResult result,
        IReadOnlyList<DatabasePlanOperation> operations,
        IReadOnlyList<DatabasePlanIssue> issues)
    {
        TargetDatabasePath = targetDatabasePath;
        Document = document;
        Result = result;
        Operations = operations;
        Issues = issues;
    }

    public static DatabasePlanArtifact Build(DatabasePlanRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.PlanId))
            throw new InvalidOperationException("A plan ID is required.");

        string sourcePath = Path.GetFullPath(request.SourceDatabasePath);
        string targetPath = Path.GetFullPath(request.TargetDatabasePath);
        string tempRoot = Path.GetFullPath(Path.GetTempPath());
        string tempDir = Path.Combine(tempRoot, "backup-normalizer-ui-plan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string workingDbPath = Path.Combine(tempDir, "target-plan.db");
        try
        {
            // ExportSnapshot reads the selected target database and creates an independent
            // writable copy. The source inventory remains open read-only for the planner.
            BackupNormalizer.Database.ExportSnapshot(targetPath, workingDbPath);
            PlanDoc document;
            Planner.PlanResult result;
            List<DatabasePlanIssue> issues;
            StringComparer targetComparer;

            using (var sourceDb = BackupNormalizer.Database.OpenReadOnly(sourcePath, pooling: false))
            using (var targetDb = BackupNormalizer.Database.OpenWritable(workingDbPath, pooling: false))
            {
                var sourceRoot = sourceDb.GetRoot(request.SourceRootId)
                    ?? throw new InvalidOperationException($"Unknown source root '{request.SourceRootId}'.");
                var targetRoot = targetDb.GetRoot(request.TargetRootId)
                    ?? throw new InvalidOperationException($"Unknown target root '{request.TargetRootId}'.");
                targetComparer = targetRoot.CaseSensitivity == "insensitive"
                    ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

                var planner = new Planner(targetDb);
                result = planner.PlanFromRoots(sourceDb, request.SourceRootId, request.TargetRootId, request.PlanId.Trim());
                document = planner.ExportPlan(result.PlanId);
                issues = AnalyzeIssues(sourceDb, sourceRoot, targetDb, targetRoot, document);
            }

            var operations = BuildOperationRows(document, issues, targetComparer);
            if (operations.Count == 0)
                issues.Add(new DatabasePlanIssue("", "Empty plan",
                    "The selected roots already match, so there are no executor-compatible operations to export.", true));

            var artifact = new DatabasePlanArtifact(targetPath, document, result,
                operations, issues.ToArray());
            return artifact;
        }
        finally
        {
            DeleteTemporaryDirectory(tempDir, tempRoot);
        }
    }

    private static List<DatabasePlanIssue> AnalyzeIssues(Database sourceDb, StorageRootRow sourceRoot,
        Database targetDb, StorageRootRow targetRoot, PlanDoc document)
    {
        var issues = new List<DatabasePlanIssue>();
        var targetComparer = targetRoot.CaseSensitivity == "insensitive"
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var sourceFiles = sourceDb.ListFilesWithHashes(sourceRoot.Id, "sha256")
            .Where(f => f.Status != BackupNormalizer.FileStatus.Missing)
            .ToList();
        var targetFiles = targetDb.ListFilesWithHashes(targetRoot.Id, "sha256")
            .Where(f => f.Status != BackupNormalizer.FileStatus.Missing)
            .ToList();
        var sourceRegular = sourceFiles.Where(f => f.Status == FileStatus.Ok
            && f.EntryKind == EntryKind.File).ToDictionary(f => f.RelativePath, targetComparer);
        var targetByPath = targetFiles.ToDictionary(f => f.RelativePath, targetComparer);
        var targetRegular = targetFiles.Where(f => f.Status == FileStatus.Ok
            && f.EntryKind == EntryKind.File).ToDictionary(f => f.RelativePath, targetComparer);

        foreach (var wanted in sourceRegular.Values)
        {
            string path = wanted.RelativePath;
            if (targetByPath.TryGetValue(path, out var exact) && exact.EntryKind != EntryKind.File)
            {
                issues.Add(new DatabasePlanIssue(path, "Skipped link",
                    $"A target {exact.EntryKind} occupies '{path}'. The planner safely skips this path; other operations remain available.", false));
            }
            else if (targetByPath.TryGetValue(path, out exact) && exact.Status != FileStatus.Ok)
            {
                issues.Add(new DatabasePlanIssue(path, "Unverified target",
                    $"The target inventory entry at '{path}' has status {exact.Status}. The plan cannot trust its contents; execution checks the destination and refuses a mismatch.", false));
            }
            else if (targetRegular.TryGetValue(path, out var existing))
            {
                if (existing.Digest != null && wanted.Digest != null
                    && !string.Equals(existing.Digest, wanted.Digest, StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(new DatabasePlanIssue(path, "Content conflict",
                        $"The target already has different content at '{path}'. The plan contains VERIFY and the executor will report a conflict without overwriting it.", false));
                }
                else if (existing.Digest == null)
                {
                    issues.Add(new DatabasePlanIssue(path, "Unverified target",
                        $"The target content at '{path}' has no usable hash. The plan contains VERIFY; execution checks it and refuses a mismatch.", false));
                }
            }

            foreach (string ancestor in Ancestors(path))
            {
                if (targetByPath.TryGetValue(ancestor, out var blocking)
                    && blocking.EntryKind == EntryKind.File)
                    issues.Add(new DatabasePlanIssue(path, "Type conflict",
                        $"Target file '{ancestor}' blocks the required directory path for '{path}'. Resolve the path conflict before exporting this plan.", true));
            }
        }

        foreach (string targetPath in targetByPath.Keys)
        {
            foreach (string ancestor in Ancestors(targetPath))
            {
                if (sourceRegular.ContainsKey(ancestor))
                    issues.Add(new DatabasePlanIssue(ancestor, "Type conflict",
                        $"Target file '{targetPath}' is below source file '{ancestor}'. Resolve the file-versus-directory conflict before exporting this plan.", true));
            }
        }

        // Keep each path-specific explanation once. Planner-generated operations stay untouched.
        return issues.GroupBy(issue => (issue.RelativePath, issue.Kind, issue.Message))
            .Select(group => group.First()).ToList();
    }

    private static List<DatabasePlanOperation> BuildOperationRows(PlanDoc document,
        IReadOnlyList<DatabasePlanIssue> issues, StringComparer pathComparer)
    {
        var issueByPath = issues.Where(issue => issue.RelativePath.Length > 0)
            .GroupBy(issue => issue.RelativePath, pathComparer)
            .ToDictionary(group => group.Key, group => group.Select(issue => issue.Message).ToArray(), pathComparer);
        var conflictPaths = issues.Where(issue => issue.Kind is "Type conflict" or "Content conflict" or "Unverified target")
            .Select(issue => issue.RelativePath).ToHashSet(pathComparer);
        return document.Operations.Select(operation =>
        {
            string issuePath = operation.DestinationPath ?? operation.SourcePath ?? "";
            string note = operation.SkipReason ?? "";
            if (issuePath.Length > 0 && issueByPath.TryGetValue(issuePath, out var notes))
                note = string.Join(" ", new[] { note }.Concat(notes).Where(value => value.Length > 0).Distinct());
            bool conflict = issuePath.Length > 0 && conflictPaths.Contains(issuePath);
            return new DatabasePlanOperation(operation.Id, operation.Type,
                PhysicalPath(document, operation.SourceKind, operation.SourceRoot, operation.SourcePath),
                PhysicalPath(document, BackupNormalizer.SourceScope.Target, operation.DestinationRoot, operation.DestinationPath),
                operation.ExpectedSize, operation.ExpectedHash ?? "", note, conflict);
        }).ToList();
    }

    private static string PhysicalPath(PlanDoc document, string? scope, string? rootId, string? relativePath)
    {
        if (rootId == null || relativePath == null) return "";
        string? root = scope switch
        {
            SourceScope.Source when rootId == document.SourceRoot => document.SourcePath,
            SourceScope.Target when rootId == document.TargetRoot => document.TargetPath,
            _ => null,
        };
        return root == null ? $"{scope}:{rootId}:{relativePath}"
            : Paths.CombineRoot(root, relativePath);
    }

    private static IEnumerable<string> Ancestors(string relativePath)
    {
        for (int slash = relativePath.IndexOf('/'); slash >= 0; slash = relativePath.IndexOf('/', slash + 1))
            yield return relativePath[..slash];
    }

    private static void DeleteTemporaryDirectory(string directory, string tempRoot)
    {
        string fullDirectory = Path.GetFullPath(directory);
        string fullTempRoot = Path.GetFullPath(tempRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullDirectory.StartsWith(fullTempRoot, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || !Path.GetFileName(fullDirectory).StartsWith("backup-normalizer-ui-plan-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to remove a path outside the generated plan temporary directory.");
        if (Directory.Exists(fullDirectory)) Directory.Delete(fullDirectory, recursive: true);
    }
}
