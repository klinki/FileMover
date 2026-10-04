namespace BackupNormalizer;

public sealed record FileDifferenceOptions(
    bool MatchFilenames = false,
    string Extensions = ".zip,.mp4",
    IReadOnlyList<string>? ExcludedPaths = null
)
{
    internal string[] NormalizeExcludedPaths() =>
        (ExcludedPaths ?? [])
            .Select(path =>
            {
                string relative = path.Replace('\\', '/').Trim();
                if (relative.StartsWith('/') || relative.Contains(':'))
                    throw new ArgumentException($"Excluded path must be root-relative: '{path}'.");
                relative = Paths.NormalizeRelative(relative).TrimEnd('/');
                if (
                    relative.Length == 0
                    || relative.IndexOfAny(['*', '?']) >= 0
                    || relative.Split('/').Any(part => part is "." or "..")
                )
                    throw new ArgumentException(
                        $"Invalid excluded path: '{path}'. Use a relative file or directory path without wildcards."
                    );
                return relative;
            })
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
}

public sealed record FilenameContentVersion(
    string Id,
    long Size,
    string? Digest,
    string? VerificationReason,
    IReadOnlyList<FileLocation> Locations
)
{
    public int CopiesA => Locations.Count(l => l.Side == "A");
    public int CopiesB => Locations.Count(l => l.Side == "B");
    public string State =>
        Digest == null ? FileLocationChanges.Unverified
        : CopiesA == 0 ? "Content only in B"
        : CopiesB == 0 ? "Content only in A"
        : "Same content in A and B";
}

public sealed record FilenameDifferenceGroup(
    string Id,
    string Filename,
    IReadOnlyList<FilenameContentVersion> Versions
)
{
    public string Classification => "Filename-based candidate";
    public int CopiesA => Versions.Sum(v => v.CopiesA);
    public int CopiesB => Versions.Sum(v => v.CopiesB);
    public int VerifiedVersionsA => Versions.Count(v => v.Digest != null && v.CopiesA > 0);
    public int VerifiedVersionsB => Versions.Count(v => v.Digest != null && v.CopiesB > 0);
    public int UnverifiedFiles => Versions.Count(v => v.Digest == null);
}

public sealed record DuplicateContentGroup(
    string Id,
    string Side,
    long Size,
    string Digest,
    IReadOnlyList<FileLocation> Locations
)
{
    public int Copies => Locations.Count;
    public int ExtraCopies => Copies - 1;
    public long PotentialSavingsBytes => checked(Size * ExtraCopies);
}

public sealed record UnverifiedFileLocation(FileLocation Location, string Reason);

public static class GroupedFileReports
{
    public const string FilenameDifferences = "Filename differences";
    public const string DuplicatesInA = "Duplicates in A";
    public const string DuplicatesInB = "Duplicates in B";

    public static bool IsGroupedView(string filter) =>
        filter is FilenameDifferences or DuplicatesInA or DuplicatesInB;

    public static string[] NormalizeExtensions(string extensions)
    {
        var result = new SortedSet<string>(StringComparer.Ordinal);
        foreach (
            string value in extensions.Split(
                [',', ';', ' ', '\t', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries
            )
        )
        {
            string extension = (value.StartsWith('.') ? value[1..] : value).ToLowerInvariant();
            if (extension.Length == 0 || extension.Any(c => !char.IsAsciiLetterOrDigit(c)))
                throw new ArgumentException(
                    $"Invalid extension '{value}'. Use extensions such as zip,mp4 without paths or wildcards."
                );
            result.Add("." + extension);
        }
        if (result.Count == 0)
            throw new ArgumentException("Filename matching requires at least one extension.");
        return result.ToArray();
    }

    internal static FilenameDifferenceGroup[] BuildFilenameGroups(
        IReadOnlyDictionary<string, Database.FileWithHashRow> a,
        IReadOnlyDictionary<string, Database.FileWithHashRow> b,
        IReadOnlyList<string> extensions,
        StringComparer comparer,
        CancellationToken token
    )
    {
        var allowed = extensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var families = new Dictionary<string, List<(string Side, Database.FileWithHashRow File)>>(
            comparer
        );
        foreach (var (side, files) in new[] { ("A", a), ("B", b) })
        foreach (var file in files.Values)
        {
            token.ThrowIfCancellationRequested();
            string filename = file.RelativePath.Split('/').Last();
            if (!allowed.Contains(Path.GetExtension(filename)))
                continue;
            if (!families.TryGetValue(filename, out var family))
                families[filename] = family = [];
            family.Add((side, file));
        }
        var result = new List<FilenameDifferenceGroup>();
        foreach (var family in families.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var before = family.Value.Where(f => f.Side == "A").ToArray();
            var after = family.Value.Where(f => f.Side == "B").ToArray();
            if (before.Length == 0 || after.Length == 0)
                continue;
            if (
                before
                    .Select(f => f.File.RelativePath)
                    .ToHashSet(comparer)
                    .SetEquals(after.Select(f => f.File.RelativePath))
            )
                continue;
            bool unknown = family.Value.Any(f => f.File.Digest == null);
            if (
                !unknown
                && before
                    .Select(f => (f.File.Size, f.File.Digest))
                    .ToHashSet()
                    .SetEquals(after.Select(f => (f.File.Size, f.File.Digest)))
            )
                continue;
            var verified = new Dictionary<(long Size, string Digest), List<FileLocation>>();
            var versions = new List<FilenameContentVersion>();
            foreach (var (side, file) in family.Value)
            {
                token.ThrowIfCancellationRequested();
                var location = new FileLocation(
                    side,
                    file.RelativePath,
                    "Recorded",
                    file.Size,
                    file.Digest
                );
                if (file.Digest == null)
                {
                    versions.Add(
                        new FilenameContentVersion(
                            $"unverified:{side}:{file.RelativePath}",
                            file.Size,
                            null,
                            file.Status != FileStatus.Ok
                                ? file.Error ?? "Entry has a scan error."
                                : "No usable current full SHA-256 hash.",
                            [location with { State = FileLocationChanges.Unverified }]
                        )
                    );
                    continue;
                }
                var key = (file.Size, file.Digest);
                if (!verified.TryGetValue(key, out var locations))
                    verified[key] = locations = [];
                locations.Add(location);
            }
            var ordered = verified
                .OrderBy(v => v.Key.Size)
                .ThenBy(v => v.Key.Digest, StringComparer.Ordinal)
                .Select(v => new FilenameContentVersion(
                    $"sha256:{v.Key.Size}:{v.Key.Digest}",
                    v.Key.Size,
                    v.Key.Digest,
                    null,
                    v.Value.OrderBy(l => l.Side, StringComparer.Ordinal)
                        .ThenBy(l => l.RelativePath, StringComparer.Ordinal)
                        .ToArray()
                ))
                .Concat(versions.OrderBy(v => v.Id, StringComparer.Ordinal))
                .ToArray();
            result.Add(new FilenameDifferenceGroup("filename:" + family.Key, family.Key, ordered));
        }
        return result.ToArray();
    }

    internal static DuplicateContentGroup[] BuildDuplicates(
        Dictionary<(long Size, string Digest), List<FileLocation>> groups,
        string side,
        CancellationToken token
    )
    {
        var result = new List<DuplicateContentGroup>();
        foreach (
            var group in groups
                .OrderBy(g => g.Key.Size)
                .ThenBy(g => g.Key.Digest, StringComparer.Ordinal)
        )
        {
            token.ThrowIfCancellationRequested();
            var locations = group
                .Value.Where(l => l.Side == side)
                .OrderBy(l => l.RelativePath, StringComparer.Ordinal)
                .ToArray();
            if (locations.Length > 1)
                result.Add(
                    new DuplicateContentGroup(
                        $"duplicates:{side}:{group.Key.Size}:{group.Key.Digest}",
                        side,
                        group.Key.Size,
                        group.Key.Digest,
                        locations
                    )
                );
        }
        return result.ToArray();
    }
}
