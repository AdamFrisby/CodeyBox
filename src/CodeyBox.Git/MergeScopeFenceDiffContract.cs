namespace CodeyBox.Git;

/// <summary>
/// Single source of truth for the <c>git diff</c> argv contract the merge
/// scope fence parses. The host-side <see cref="CodeyBox.Core.IGitHost"/>
/// diff path and
/// the in-sandbox fence must emit byte-identical zero-context diffs or the
/// in-loop verdict can diverge from the final host fence, so both build
/// their argv through these helpers — a flag change (rename handling,
/// context width, textconv, color) cannot drift between the two call sites.
/// </summary>
public static class MergeScopeFenceDiffContract
{
    /// <summary>
    /// Whole-tree change-set diff behind the fence's added/removed/renamed
    /// path check. Rename detection stays on so a rename surfaces as 'R'
    /// rather than a delete+add pair the fence would misclassify.
    /// </summary>
    public static string[] NameStatusArguments(string fromTreeish, string toTreeish)
    {
        ValidateTreeish(fromTreeish, nameof(fromTreeish));
        ValidateTreeish(toTreeish, nameof(toTreeish));
        return ["diff", "--name-status", "-M", fromTreeish, toTreeish];
    }

    /// <summary>
    /// Per-file zero-context content diff behind the fence's line-coordinate
    /// check. <c>--no-ext-diff</c>/<c>--no-textconv</c>/<c>--text</c>/
    /// <c>--no-color</c> keep repository-controlled attributes and diff
    /// drivers from suppressing or rewriting the hunks the fence measures;
    /// <c>--unified=0</c> makes every changed line visible to the parser.
    /// </summary>
    public static string[] FileContentArguments(string fromTreeish, string toTreeish, string path)
    {
        ValidateTreeish(fromTreeish, nameof(fromTreeish));
        ValidateTreeish(toTreeish, nameof(toTreeish));
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return ["diff", "--no-ext-diff", "--no-textconv", "--text", "--no-color", "--unified=0",
            fromTreeish, toTreeish, "--", path];
    }

    private static void ValidateTreeish(string treeish, string paramName)
    {
        if (string.IsNullOrWhiteSpace(treeish) || treeish.StartsWith('-'))
            throw new ArgumentException("treeish must be a non-empty revision, not an option", paramName);
    }
}
