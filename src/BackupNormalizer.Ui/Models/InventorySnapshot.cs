using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace BackupNormalizer.Ui.Models;

public sealed class InventoryNode(string name, string relativePath, bool isDirectory, StringComparer comparer)
{
    public string Name { get; } = name;
    public string RelativePath { get; } = relativePath;
    public bool IsDirectory { get; } = isDirectory;
    public long Size { get; init; }
    public DateTime Modified { get; init; }
    public DateTime? Created { get; init; }
    public string? Digest { get; init; }
    public bool HasScanError { get; init; }
    public string EntryKind { get; init; } = BackupNormalizer.EntryKind.File;
    public bool IsLink => EntryKind != BackupNormalizer.EntryKind.File;
    public string? LinkTarget { get; init; }
    public string? TargetPath { get; init; }
    public string? LinkNote { get; init; }
    public Dictionary<string, InventoryNode> Children { get; } = new(comparer);
}

public sealed class InventoryRoot(StorageRootRow root, string? scanStatus, InventoryStatusRow? healthStatus = null)
{
    public StorageRootRow Root { get; } = root;
    public string Display => $"{Root.Name} [{Root.Id}]";
    public string? ScanStatus { get; } = scanStatus;
    public InventoryStatusRow? HealthStatus { get; } = healthStatus;
    public string HealthSummary => HealthStatus is { } health
        ? $"Scan: {ScanStatus ?? "not scanned"} | SHA-256: {health.UsableHashes:N0}/{health.RegularFiles:N0} | " +
            (health.PlanningReady ? "Content ready for planning" : health.BlockingReason)
        : $"Scan: {ScanStatus ?? "not scanned"}";
    public StringComparer Comparer { get; } = root.CaseSensitivity == "insensitive"
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    public Dictionary<string, InventoryNode> Nodes { get; } = new(root.CaseSensitivity == "insensitive"
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    internal void Add(Database.FileWithHashRow file)
    {
        if (file.Status == FileStatus.Missing) return;
        string path = Paths.NormalizeRelative(file.RelativePath);
        string[] parts = path.Split('/');
        if (parts.Any(p => p.Length == 0 || p is "." or ".."))
            throw new InvalidDataException($"Invalid inventory path: {file.RelativePath}");

        var parent = Nodes[""];
        string relative = "";
        for (int i = 0; i < parts.Length; i++)
        {
            if (parent.IsLink) return; // Historical descendants beneath a newly recorded link are not navigable.
            relative = relative.Length == 0 ? parts[i] : relative + "/" + parts[i];
            bool intermediate = i < parts.Length - 1;
            bool directory = intermediate || file.EntryKind == EntryKind.DirectoryLink;
            if (!Nodes.TryGetValue(relative, out var node))
            {
                node = new InventoryNode(parts[i], relative, directory, Comparer)
                {
                    Size = intermediate ? 0 : file.Size,
                    Modified = !intermediate && DateTimeOffset.TryParse(file.ModifiedUtc, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var date) ? date.LocalDateTime : DateTime.MinValue,
                    Created = !intermediate && DateTimeOffset.TryParse(file.CreatedUtc, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var created) ? created.LocalDateTime : null,
                    Digest = directory ? null : file.Digest,
                    HasScanError = !intermediate && file.Status != FileStatus.Ok,
                    EntryKind = intermediate ? EntryKind.File : file.EntryKind,
                    LinkTarget = intermediate ? null : file.LinkTarget,
                    TargetPath = intermediate ? null : file.TargetPath,
                    LinkNote = intermediate ? null : file.LinkNote,
                };
                Nodes.Add(relative, node);
                parent.Children.Add(parts[i], node);
            }
            else if (node.IsDirectory != directory || !directory)
            {
                throw new InvalidDataException($"Conflicting inventory path: {file.RelativePath}");
            }
            parent = node;
        }
    }
}

public sealed record InventorySnapshot(string DatabasePath, IReadOnlyList<InventoryRoot> Roots)
{
    public static InventorySnapshot Load(string databasePath)
    {
        using var db = Database.OpenReadOnly(databasePath, pooling: false);
        var roots = db.ListRoots().Select(r =>
        {
            var health = db.GetInventoryStatus(r.Id);
            return new InventoryRoot(r, health.LatestScan?.Scan.Status, health);
        }).ToList();
        if (roots.Count == 0) throw new InvalidDataException("Database contains no storage roots.");
        foreach (var root in roots)
            root.Nodes.Add("", new InventoryNode("", "", true, root.Comparer));
        var byId = roots.ToDictionary(r => r.Root.Id);
        foreach (var file in db.ListFilesWithHashes(null, "sha256"))
        {
            if (byId.TryGetValue(file.StorageRootId, out var root)) root.Add(file);
        }
        return new InventorySnapshot(db.DbPath, roots);
    }
}
