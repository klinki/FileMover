using System.Text.RegularExpressions;

namespace BackupNormalizer;

/// <summary>Root-relative scan scope. A matching ancestor protects its subtree.</summary>
public sealed class PathExclusions
{
    private readonly Regex[] _regexes;
    public IReadOnlyList<string> Patterns { get; }

    public PathExclusions(IEnumerable<string> patterns, bool ignoreCase = false)
    {
        Patterns = patterns.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var options = RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
        _regexes = Patterns.Select(pattern =>
        {
            if (string.IsNullOrWhiteSpace(pattern)) throw new ArgumentException("Exclusion regexes must not be empty.");
            try { return new Regex(pattern, options, TimeSpan.FromMilliseconds(250)); }
            catch (ArgumentException ex) { throw new ArgumentException($"Invalid exclusion regex '{pattern}': {ex.Message}", ex); }
        }).ToArray();
    }

    public bool IsExcluded(string relativePath)
    {
        if (_regexes.Length == 0) return false;
        string path = relativePath.Replace('\\', '/').TrimEnd('/');
        while (path.Length > 0)
        {
            foreach (var regex in _regexes)
            {
                try { if (regex.IsMatch(path)) return true; }
                catch (RegexMatchTimeoutException ex)
                {
                    throw new InvalidOperationException($"Exclusion regex '{regex}' timed out for '{relativePath}'.", ex);
                }
            }
            int separator = path.LastIndexOf('/');
            if (separator < 0) break;
            path = path[..separator];
        }
        return false;
    }
}
