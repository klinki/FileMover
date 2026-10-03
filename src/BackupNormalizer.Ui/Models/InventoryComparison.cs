using System;
using System.Collections.Generic;
using System.Linq;

namespace BackupNormalizer.Ui.Models;

public enum ComparisonState
{
    None,
    Equal,
    Different,
    OnlyLeft,
    OnlyRight,
    Unverified,
    TypeConflict,
    ScanError,
    Skipped,
}

public sealed class InventoryComparison
{
    public Dictionary<string, ComparisonState> Left { get; }
    public Dictionary<string, ComparisonState> Right { get; }
    public string LeftBase { get; }
    public string RightBase { get; }
    public string Summary { get; private set; } = "";
    private int _conflicts;
    private int _scanErrors;

    private InventoryComparison(
        InventoryRoot left,
        string leftBase,
        InventoryRoot right,
        string rightBase
    )
    {
        Left = new(left.Comparer);
        Right = new(right.Comparer);
        LeftBase = leftBase;
        RightBase = rightBase;
    }

    public static InventoryComparison Compare(
        InventoryRoot left,
        string leftBase,
        InventoryRoot right,
        string rightBase
    )
    {
        if (
            !left.Nodes.TryGetValue(leftBase, out var leftNode)
            || !leftNode.IsDirectory
            || leftNode.IsLink
            || !right.Nodes.TryGetValue(rightBase, out var rightNode)
            || !rightNode.IsDirectory
            || rightNode.IsLink
        )
        {
            throw new InvalidOperationException(
                "Choose an indexed folder in each panel before comparing."
            );
        }

        var result = new InventoryComparison(left, leftBase, right, rightBase);
        var comparer =
            left.Root.CaseSensitivity == "insensitive"
            && right.Root.CaseSensitivity == "insensitive"
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
        result.CompareNodes(leftNode, rightNode, comparer);
        int Count(
            InventoryRoot root,
            Dictionary<string, ComparisonState> states,
            ComparisonState state
        ) =>
            states.Count(pair =>
                pair.Value == state
                && !root.Nodes[pair.Key].IsDirectory
                && !root.Nodes[pair.Key].IsLink
            );
        result.Summary =
            $"Files: {Count(left, result.Left, ComparisonState.OnlyLeft)} only left, "
            + $"{Count(right, result.Right, ComparisonState.OnlyRight)} only right, "
            + $"{Count(left, result.Left, ComparisonState.Different)} different, "
            + $"{Count(left, result.Left, ComparisonState.Equal)} equal, "
            + $"{Count(left, result.Left, ComparisonState.Unverified)} unverified. "
            + $"Conflicts: {result._conflicts}. Scan errors: {result._scanErrors}. "
            + $"Skipped links: {result.Left.Count(p => p.Value == ComparisonState.Skipped && left.Nodes[p.Key].IsLink) + result.Right.Count(p => p.Value == ComparisonState.Skipped && right.Nodes[p.Key].IsLink)}.";
        if (left.ScanStatus != ScanStatus.Completed || right.ScanStatus != ScanStatus.Completed)
        {
            result.Summary +=
                " Warning: an inventory scan is incomplete or unavailable; absence may reflect unscanned files.";
        }

        return result;
    }

    private ComparisonState CompareNodes(
        InventoryNode left,
        InventoryNode right,
        StringComparer comparer
    )
    {
        ComparisonState state;
        if (left.HasScanError || right.HasScanError)
        {
            _scanErrors += (left.HasScanError ? 1 : 0) + (right.HasScanError ? 1 : 0);
            state = ComparisonState.ScanError;
        }
        else if (
            left.IsLink != right.IsLink
            || (!left.IsLink && left.IsDirectory != right.IsDirectory)
        )
        {
            _conflicts++;
            SetSubtree(left, Left, ComparisonState.TypeConflict);
            SetSubtree(right, Right, ComparisonState.TypeConflict);
            return ComparisonState.TypeConflict;
        }
        else if (left.IsLink && right.IsLink)
        {
            state = ComparisonState.Skipped;
        }
        else if (!left.IsDirectory)
        {
            state =
                left.Size != right.Size ? ComparisonState.Different
                : left.Digest == null || right.Digest == null ? ComparisonState.Unverified
                : string.Equals(left.Digest, right.Digest, StringComparison.OrdinalIgnoreCase)
                    ? ComparisonState.Equal
                : ComparisonState.Different;
        }
        else
        {
            state = ComparisonState.Equal;
            var rightChildren = right.Children.Values.ToDictionary(n => n.Name, comparer);
            var matched = new HashSet<string>(comparer);
            foreach (var child in left.Children.Values)
            {
                ComparisonState childState;
                if (rightChildren.TryGetValue(child.Name, out var other))
                {
                    matched.Add(other.Name);
                    childState = CompareNodes(child, other, comparer);
                }
                else
                {
                    childState = SetSubtree(child, Left, ComparisonState.OnlyLeft);
                }
                state = Aggregate(state, childState);
            }
            foreach (var child in right.Children.Values.Where(n => !matched.Contains(n.Name)))
            {
                state = Aggregate(state, SetSubtree(child, Right, ComparisonState.OnlyRight));
            }
        }
        Left[left.RelativePath] = state;
        Right[right.RelativePath] = state;
        return state;
    }

    private static ComparisonState Aggregate(ComparisonState current, ComparisonState child)
    {
        if (
            current == ComparisonState.Different
            || child
                is ComparisonState.Different
                    or ComparisonState.OnlyLeft
                    or ComparisonState.OnlyRight
                    or ComparisonState.TypeConflict
        )
        {
            return ComparisonState.Different;
        }

        if (
            current == ComparisonState.Unverified
            || child is ComparisonState.Unverified or ComparisonState.ScanError
        )
        {
            return ComparisonState.Unverified;
        }

        return ComparisonState.Equal;
    }

    private ComparisonState SetSubtree(
        InventoryNode node,
        Dictionary<string, ComparisonState> states,
        ComparisonState state
    )
    {
        if (node.HasScanError)
        {
            _scanErrors++;
        }

        var result =
            node.HasScanError ? ComparisonState.ScanError
            : node.IsLink && state != ComparisonState.TypeConflict ? ComparisonState.Skipped
            : state;
        bool skippedOnly = node.Children.Count > 0;
        foreach (var child in node.Children.Values)
        {
            skippedOnly &= SetSubtree(child, states, state) == ComparisonState.Skipped;
        }

        if (
            skippedOnly
            && state is ComparisonState.OnlyLeft or ComparisonState.OnlyRight
            && !node.HasScanError
        )
        {
            result = ComparisonState.Skipped;
        }

        states[node.RelativePath] = result;
        return result;
    }
}
