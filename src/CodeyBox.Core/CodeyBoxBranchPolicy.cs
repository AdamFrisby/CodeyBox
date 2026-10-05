namespace CodeyBox.Core;

/// <summary>
/// Ownership policy for branches CodeyBox is allowed to rewrite with a
/// force-push. CodeyBox owns <c>codeybox/*</c> work branches; base branches
/// (e.g. <c>main</c>) and any branch outside the prefix must never be
/// force-pushed, even when a push is rejected as non-fast-forward.
/// </summary>
public static class CodeyBoxBranchPolicy
{
    /// <summary>
    /// Exact prefix identifying CodeyBox-owned work branches. Matched with
    /// ordinal case-sensitive comparison: <c>Codeybox/x</c> is not owned.
    /// </summary>
    public const string OwnedBranchPrefix = "codeybox/";

    /// <summary>
    /// True when <paramref name="branch"/> is a CodeyBox-owned work branch:
    /// non-empty beyond the bare prefix and starting with
    /// <see cref="OwnedBranchPrefix"/> under ordinal comparison.
    /// </summary>
    public static bool IsOwnedWorkBranch(string? branch) =>
        !string.IsNullOrEmpty(branch)
        && branch.Length > OwnedBranchPrefix.Length
        && branch.StartsWith(OwnedBranchPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Throws <see cref="ArgumentException"/> when <paramref name="branch"/>
    /// is not a CodeyBox-owned work branch. Force-push call sites must invoke
    /// this before touching the remote so a misconfigured base branch can
    /// never be rewritten.
    /// </summary>
    public static void RequireOwnedWorkBranch(string branch, string fieldName = "branch")
    {
        if (!IsOwnedWorkBranch(branch))
            throw new ArgumentException(
                $"Refusing to force-push '{branch ?? "(null)"}': only branches under '{OwnedBranchPrefix}' are CodeyBox-owned and eligible for a lease-guarded rewrite.",
                fieldName);
    }
}
