namespace CodeyBox.Core;

/// <summary>
/// Hot-reloadable knobs for proving a CodeyBox-owned work branch's remote tip
/// is CodeyBox's own history when no prior push was recorded. Bound from
/// <c>CodeyBox:UpstreamBranchOwnership</c>; edits take effect on the next
/// push or re-drive without a restart.
/// </summary>
public sealed class UpstreamBranchOwnershipOptions
{
    /// <summary>
    /// Trailer keys that must each appear on every commit reachable from the
    /// remote tip but not from the base for the tip to count as CodeyBox's
    /// own history. Matched as <c>Key: value</c> trailer lines (key compared
    /// ordinal-ignore-case); <c>Co-Authored-By</c> additionally requires its
    /// value to name CodeyBox so a foreign <c>Co-Authored-By: attacker</c>
    /// line cannot pass. Defaults cover the trailers every CodeyBox-emitted
    /// commit carries (see <see cref="CodeyBoxTrailers"/>).
    /// </summary>
    public string[] RequiredTrailerKeys { get; set; } =
        [CodeyBoxTrailers.WorkItemTrailerKey, "Co-Authored-By"];

    /// <summary>
    /// Upper bound on the exclusive commits examined for ownership proof.
    /// Enforced BEFORE buffering: a larger exclusive set is unverifiable and
    /// refused rather than read fully. Default 100.
    /// </summary>
    public int MaxCommitsToVerify { get; set; } = 100;

    public UpstreamBranchOwnershipOptions Clone() => new()
    {
        RequiredTrailerKeys = (string[])RequiredTrailerKeys.Clone(),
        MaxCommitsToVerify = MaxCommitsToVerify,
    };
}

/// <summary>
/// Swappable holder for the current <see cref="UpstreamBranchOwnershipOptions"/>.
/// Same pattern as <see cref="CommitAttributionSnapshot"/>: push and re-drive
/// paths read <see cref="Current"/> live so a config reload takes effect on
/// the next decision without a restart.
/// </summary>
public sealed class UpstreamBranchOwnershipSnapshot
{
    private UpstreamBranchOwnershipOptions _current;

    public UpstreamBranchOwnershipSnapshot(UpstreamBranchOwnershipOptions initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _current = initial;
    }

    public UpstreamBranchOwnershipOptions Current => Volatile.Read(ref _current);

    public void Replace(UpstreamBranchOwnershipOptions next)
    {
        ArgumentNullException.ThrowIfNull(next);
        Volatile.Write(ref _current, next);
    }
}

/// <summary>
/// Pure ownership proof over commit messages: a remote tip counts as
/// CodeyBox's own history only when EVERY commit reachable from it but not
/// from the base carries the CodeyBox trailers. A single unmarked commit
/// fails the whole tip — a third party's commit anywhere in the exclusive
/// range must never be rewritten.
/// </summary>
public static class BranchOwnershipPolicy
{
    /// <summary>
    /// Default required trailer keys when no configured options are
    /// available. Single source of truth for the fallback; the configured
    /// <see cref="UpstreamBranchOwnershipOptions.RequiredTrailerKeys"/> wins
    /// whenever options are present.
    /// </summary>
    public static readonly string[] DefaultRequiredTrailerKeys =
        [CodeyBoxTrailers.WorkItemTrailerKey, "Co-Authored-By"];

    /// <summary>
    /// True when <paramref name="commitMessage"/> carries every key in
    /// <paramref name="requiredTrailerKeys"/> as a <c>Key: value</c> trailer
    /// line. Keys compare ordinal-ignore-case; values must be non-empty, and
    /// a <c>Co-Authored-By</c> value must name CodeyBox (ordinal) so a
    /// foreign co-author line cannot satisfy the check.
    /// </summary>
    public static bool IsOwnedCommit(string? commitMessage, IReadOnlyList<string> requiredTrailerKeys)
    {
        if (string.IsNullOrWhiteSpace(commitMessage) || requiredTrailerKeys.Count == 0)
            return false;
        foreach (var key in requiredTrailerKeys)
        {
            if (string.IsNullOrWhiteSpace(key))
                return false;
            if (!HasTrailer(commitMessage, key.Trim()))
                return false;
        }
        return true;
    }

    /// <summary>
    /// True when <paramref name="exclusiveCommitMessages"/> is non-empty and
    /// every entry satisfies <see cref="IsOwnedCommit"/>. An empty range is
    /// NOT ownership (nothing proves CodeyBox wrote the tip); callers with a
    /// vacuous range decide separately.
    /// </summary>
    public static bool AreAllCommitsOwned(
        IReadOnlyList<string> exclusiveCommitMessages,
        IReadOnlyList<string> requiredTrailerKeys)
    {
        if (exclusiveCommitMessages.Count == 0)
            return false;
        foreach (var message in exclusiveCommitMessages)
        {
            if (!IsOwnedCommit(message, requiredTrailerKeys))
                return false;
        }
        return true;
    }

    private static bool HasTrailer(string message, string key)
    {
        // Trailer block = contiguous trailing lines of `Token: value` form
        // (git trailer semantics: only the end-of-message block counts, so a
        // quoted trailer line in the body middle cannot satisfy the check).
        // The walk stops at the first blank or non-trailer line.
        var lines = message.Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim().TrimEnd('\r');
            if (line.Length == 0)
                break;
            var colon = line.IndexOf(':');
            if (colon <= 0)
                break;
            var name = line[..colon].Trim();
            if (!IsTrailerToken(name))
                break;
            var value = line[(colon + 1)..].Trim();
            if (value.Length == 0)
                continue;
            if (!name.Equals(key, StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(key, "Co-Authored-By", StringComparison.OrdinalIgnoreCase)
                && !value.Contains("CodeyBox", StringComparison.Ordinal))
                continue;
            return true;
        }
        return false;
    }

    private static bool IsTrailerToken(string name)
    {
        if (name.Length == 0 || name.Length > 64)
            return false;
        foreach (var c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-')
                return false;
        }
        return true;
    }
}
