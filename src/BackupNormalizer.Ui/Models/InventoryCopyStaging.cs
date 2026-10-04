using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BackupNormalizer.Ui.Models;

/// <summary>Manual copies use recorded metadata; neither inventory nor disk files are changed.</summary>
public sealed record InventoryCopyStaging(
    InventorySnapshot SourceSnapshot,
    InventoryRoot Source,
    InventorySnapshot TargetSnapshot,
    InventoryRoot Target
)
{
    public List<PlanStaging.StagedOp> Prepare(
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory,
        IReadOnlyList<PlanStaging.StagedOp> existing
    )
    {
        if (Source.ScanStatus != ScanStatus.Completed || Target.ScanStatus != ScanStatus.Completed)
            throw new InvalidOperationException(
                "Both inventories need a complete successful scan before staging copies."
            );
        if (!Target.Root.Writable)
            throw new InvalidOperationException("The target root is read-only.");
        if (Paths.RootsOverlap(Source.Root.Path, Target.Root.Path))
            throw new InvalidOperationException("Source and target roots must be disjoint.");

        ValidatePath(destinationDirectory, allowEmpty: true);
        var sourceExclusions = new PathExclusions(
            Source.HealthStatus?.ExcludedPathRegexes ?? [],
            Source.Root.CaseSensitivity == "insensitive"
        );
        var targetExclusions = new PathExclusions(
            Target.HealthStatus?.ExcludedPathRegexes ?? [],
            Target.Root.CaseSensitivity == "insensitive"
        );
        var targetLinks = Target
            .Nodes.Values.Where(n => n.IsLink)
            .Select(n => n.RelativePath)
            .ToHashSet(Target.Comparer);
        var operations = new List<PlanStaging.StagedOp>();
        var directories = new HashSet<string>(Target.Comparer);
        var destinations = new Dictionary<string, PlanStaging.StagedOp>(Target.Comparer);
        foreach (var operation in existing)
            RegisterDestination(operation);

        foreach (string path in sourcePaths.Distinct(Source.Comparer))
        {
            ValidatePath(path, allowEmpty: false);
            if (!Source.Nodes.TryGetValue(path, out var node))
                throw new InvalidOperationException($"Source path is not indexed: '{path}'.");
            Visit(node, Join(destinationDirectory, node.Name));
        }
        return operations;

        void Visit(InventoryNode node, string destination)
        {
            if (sourceExclusions.IsExcluded(node.RelativePath))
                return;
            if (targetExclusions.IsExcluded(destination))
                throw new InvalidOperationException(
                    $"Target path is excluded from the inventory: '{destination}'."
                );
            string? targetLink = Paths.FindRecordedLink(destination, targetLinks);
            if (node.IsLink || targetLink != null)
            {
                operations.Add(
                    new PlanStaging.StagedOp(
                        OpType.SkipLink,
                        node.RelativePath,
                        destination,
                        node.Size,
                        null,
                        node.IsLink
                            ? "Source link is excluded from copying."
                            : $"Target link '{targetLink}' blocks copying."
                    )
                );
                return;
            }
            if (node.IsDirectory)
            {
                EnsureDirectory(destination);
                foreach (var child in node.Children.Values.OrderBy(n => n.Name, Source.Comparer))
                    Visit(child, Join(destination, child.Name));
                return;
            }
            if (
                node.HasScanError
                || node.Digest is not { Length: 64 } digest
                || !digest.All(Uri.IsHexDigit)
            )
                throw new InvalidOperationException(
                    $"Source file '{node.RelativePath}' needs a usable SHA-256 hash. Run hashing and refresh the inventory."
                );
            if (Target.Nodes.TryGetValue(destination, out var target) && target.IsDirectory)
                throw new InvalidOperationException(
                    $"Target directory blocks file '{destination}'."
                );
            EnsureDirectory(Parent(destination));
            var operation = new PlanStaging.StagedOp(
                target == null ? OpType.Copy : OpType.Verify,
                node.RelativePath,
                target?.RelativePath ?? destination,
                node.Size,
                digest,
                target != null
                    ? "Destination already exists; verify its contents without overwriting."
                    : null
            );
            RegisterDestination(operation);
            operations.Add(operation);
        }

        void EnsureDirectory(string path)
        {
            if (path.Length == 0 || !directories.Add(path))
                return;
            EnsureDirectory(Parent(path));
            if (Target.Nodes.TryGetValue(path, out var node))
            {
                if (!node.IsDirectory || node.IsLink)
                    throw new InvalidOperationException($"Target entry blocks directory '{path}'.");
                return;
            }
            var operation = new PlanStaging.StagedOp(OpType.Mkdir, path, path, 0, null);
            RegisterDestination(operation);
            operations.Add(operation);
        }

        void RegisterDestination(PlanStaging.StagedOp operation)
        {
            if (operation.Type == OpType.SkipLink)
                return;
            string path = operation.DestRel!;
            if (
                destinations.TryGetValue(path, out var previous)
                && (
                    previous.Type != operation.Type
                    || !Source.Comparer.Equals(previous.SourceRel, operation.SourceRel)
                )
            )
                throw new InvalidOperationException(
                    $"Staged operations collide at target path '{path}'."
                );
            destinations[path] = operation;
            string ancestor = Parent(path);
            while (ancestor.Length > 0)
            {
                if (
                    destinations.TryGetValue(ancestor, out previous)
                    && previous.Type != OpType.Mkdir
                )
                    throw new InvalidOperationException(
                        $"Staged file blocks directory '{ancestor}'."
                    );
                ancestor = Parent(ancestor);
            }
        }
    }

    public PlanDoc BuildDocument(string planId, IReadOnlyList<PlanStaging.StagedOp> staged) =>
        new(
            planId,
            Database.UtcNow(),
            staged.Where(op => op.Type == OpType.Copy).Sum(op => op.ExpectedSize),
            SourceSnapshot.DatabasePath,
            Source.Root.Id,
            Source.Root.Path,
            Target.Root.Id,
            Target.Root.Path,
            staged
                .Select(
                    (op, index) =>
                        new PlanOpDoc(
                            index + 1,
                            op.Type,
                            op.Type == OpType.Mkdir ? null : SourceScope.Source,
                            op.Type == OpType.Mkdir ? null : Source.Root.Id,
                            op.Type == OpType.Mkdir ? null : op.SourceRel,
                            Target.Root.Id,
                            op.DestRel,
                            op.ExpectedSize,
                            op.ExpectedHash,
                            op.SkipReason
                        )
                )
                .ToList()
        );

    public void EnsureOutputPath(string path)
    {
        foreach (
            string database in new[] { SourceSnapshot.DatabasePath, TargetSnapshot.DatabasePath }
        )
        foreach (string suffix in new[] { "", "-wal", "-shm", "-journal" })
            if (Paths.PathEquals(path, database + suffix))
                throw new InvalidOperationException(
                    "Choose an output outside the inventory databases and their companion files."
                );
    }

    private static string Join(string directory, string name) =>
        directory.Length == 0 ? name : directory + "/" + name;

    private static string Parent(string path) =>
        path.Contains('/') ? path[..path.LastIndexOf('/')] : "";

    private static void ValidatePath(string path, bool allowEmpty)
    {
        if (allowEmpty && path.Length == 0)
            return;
        if (
            Path.IsPathRooted(path)
            || path.Contains('\\')
            || path.Split('/').Any(part => part.Length == 0 || part is "." or "..")
        )
            throw new InvalidOperationException($"Invalid inventory-relative path: '{path}'.");
    }
}
