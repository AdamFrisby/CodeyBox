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
    /// Trailer keys accepted as ownership proof, any-of: a commit reachable
    /// from the remote tip but not from the current base tip counts as
    /// CodeyBox's own history when its trailer block carries ANY one of
    /// these keys (matched exactly; <c>Co-Authored-By</c> additionally
    /// requires its value to be exactly the canonical CodeyBox identity so
    /// a foreign <c>Co-Authored-By: attacker</c> line cannot pass). Any-of
    /// because the writers stamp different sets: agents write
    /// <c>CodeyBox-Prompt-Revision</c> + <c>Co-Authored-By</c>, the
    /// orchestrator writes <c>CodeyBox-WorkItem</c> + <c>CodeyBox-Agent</c> /
    /// <c>CodeyBox-Mechanical-Fixer</c> + <c>Co-Authored-By</c> (see
    /// <see cref="CodeyBoxTrailers"/>). Requiring every key on every commit
    /// refused branches that are provably CodeyBox's own. Defaults cover
    /// every trailer key this instance writes (see
    /// <see cref="BranchOwnershipPolicy.DefaultRequiredTrailerKeys"/>); an
    /// empty list never proves ownership. Attribution filtering applies on
    /// top (see <see cref="BranchOwnershipPolicy.EffectiveAcceptedKeys"/>):
    /// keys the instance no longer writes cannot prove anything it wrote.
    /// </summary>
    public string[] RequiredTrailerKeys { get; set; } =
        [.. BranchOwnershipPolicy.DefaultRequiredTrailerKeys];

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
/// from the current base tip carries ANY accepted CodeyBox trailer in its
/// trailer block. A single unmarked commit fails the whole tip — a third
/// party's commit anywhere in the exclusive range must never be rewritten.
/// </summary>
public static class BranchOwnershipPolicy
{
    /// <summary>
    /// Every trailer key this instance writes on CodeyBox-emitted commits
    /// (see <see cref="CodeyBoxTrailers"/>). A commit carrying any one of
    /// these (subject to attribution filtering) is CodeyBox's own.
    /// </summary>
    public static readonly string[] KnownCodeyBoxTrailerKeys =
    [
        "Co-Authored-By",
        CodeyBoxTrailers.WorkItemTrailerKey,
        CodeyBoxTrailers.AgentTrailerKey,
        CodeyBoxTrailers.PromptRevisionTrailerKey,
        CodeyBoxTrailers.MechanicalFixerTrailerKey,
    ];

    /// <summary>
    /// Default required trailer keys when no configured options are
    /// available. Single source of truth for the fallback; the configured
    /// <see cref="UpstreamBranchOwnershipOptions.RequiredTrailerKeys"/> wins
    /// whenever options are present. Covers every trailer key this instance
    /// writes (see <see cref="KnownCodeyBoxTrailerKeys"/>) because each
    /// writer stamps a different subset.
    /// </summary>
    public static readonly string[] DefaultRequiredTrailerKeys =
        [.. KnownCodeyBoxTrailerKeys];

    /// <summary>
    /// True when <paramref name="commitMessage"/> carries ANY key in
    /// <paramref name="acceptedTrailerKeys"/> as a <c>Key: value</c> trailer
    /// line. Keys compare ordinally (exact); values must be non-empty, and a
    /// <c>Co-Authored-By</c> value must exactly equal the canonical CodeyBox
    /// identity (<see cref="CodeyBoxTrailers.CoAuthoredByValue"/>) so a
    /// foreign co-author line cannot satisfy the check.
    /// </summary>
    public static bool IsOwnedCommit(string? commitMessage, IReadOnlyList<string> acceptedTrailerKeys)
    {
        if (string.IsNullOrWhiteSpace(commitMessage) || acceptedTrailerKeys.Count == 0)
            return false;
        foreach (var key in acceptedTrailerKeys)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;
            if (HasTrailer(commitMessage, key.Trim()))
                return true;
        }
        return false;
    }

    /// <summary>
    /// True when <paramref name="exclusiveCommits"/> is non-empty and every
    /// entry satisfies <see cref="IsOwnedCommit"/>. An empty range is NOT
    /// ownership (nothing proves CodeyBox wrote the tip); callers with a
    /// vacuous range decide separately.
    /// </summary>
    public static bool AreAllCommitsOwned(
        IReadOnlyList<string> exclusiveCommitMessages,
        IReadOnlyList<string> acceptedTrailerKeys)
    {
        if (exclusiveCommitMessages.Count == 0)
            return false;
        foreach (var message in exclusiveCommitMessages)
        {
            if (!IsOwnedCommit(message, acceptedTrailerKeys))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Shas of the commits in <paramref name="exclusiveCommits"/> that carry
    /// no accepted trailer, oldest first. Empty when the whole range is
    /// CodeyBox's own. Callers name these shas in the refusal so the operator
    /// can inspect exactly which commits blocked the proof.
    /// </summary>
    public static IReadOnlyList<string> FindUnownedCommitShas(
        IReadOnlyList<OwnedBranchCommit> exclusiveCommits,
        IReadOnlyList<string> acceptedTrailerKeys)
    {
        var unowned = new List<string>();
        foreach (var commit in exclusiveCommits)
        {
            if (!IsOwnedCommit(commit.Message, acceptedTrailerKeys))
                unowned.Add(commit.Sha);
        }
        return unowned;
    }

    /// <summary>
    /// Human-readable summary of <paramref name="unownedShas"/> for refusal
    /// messages: up to <paramref name="maxToName"/> shas named, the rest
    /// counted. Entries are forge-reported shas already normalized to hex by
    /// the listing implementation; blanks are never emitted, only counted.
    /// </summary>
    public static string DescribeUnownedCommits(IReadOnlyList<string> unownedShas, int maxToName = 10)
    {
        ArgumentNullException.ThrowIfNull(unownedShas);
        if (maxToName < 1)
            maxToName = 1;
        var named = unownedShas.Where(s => !string.IsNullOrWhiteSpace(s)).Take(maxToName).ToList();
        if (named.Count == 0)
            return "unidentified commits";
        var remaining = unownedShas.Count - named.Count;
        var summary = string.Join(", ", named);
        if (remaining > 0)
            summary += $" (and {remaining} more)";
        return summary;
    }

    /// <summary>
    /// Narrows the configured ownership keys to what this instance can still
    /// have written under <paramref name="attribution"/>: a disabled
    /// <c>Co-Authored-By</c> family cannot prove anything (its line is
    /// stripped from everything CodeyBox writes), and likewise for disabled
    /// <c>CodeyBox-*</c> trailers. An empty result proves nothing — callers
    /// must require explicit ownership confirmation. Configured keys compare
    /// ordinal-ignore-case here (operator-written config); commit trailer
    /// lines still match exactly (see <see cref="HasTrailer"/>).
    /// </summary>
    public static string[] EffectiveAcceptedKeys(
        IReadOnlyList<string> configuredKeys,
        CommitAttribution attribution)
    {
        ArgumentNullException.ThrowIfNull(configuredKeys);
        ArgumentNullException.ThrowIfNull(attribution);
        var accepted = new List<string>(configuredKeys.Count);
        foreach (var key in configuredKeys)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;
            var trimmed = key.Trim();
            if (!attribution.IncludeCoAuthoredBy
                && trimmed.Equals("Co-Authored-By", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!attribution.IncludeCodeyBoxTrailers
                && trimmed.StartsWith("CodeyBox-", StringComparison.OrdinalIgnoreCase))
                continue;
            accepted.Add(trimmed);
        }
        return [.. accepted];
    }

    private static bool HasTrailer(string message, string key)
    {
        // Trailer block = contiguous trailing lines of `Token: value` form
        // (git trailer semantics: only the end-of-message block counts, so a
        // quoted trailer line in the body middle cannot satisfy the check).
        // The walk stops at the first blank or non-trailer line. Keys match
        // exactly (ordinal): CodeyBox always writes canonical-case keys, and
        // a near-miss must not pass as proof of authorship.
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
            if (!name.Equals(key, StringComparison.Ordinal))
                continue;
            if (string.Equals(key, "Co-Authored-By", StringComparison.Ordinal)
                && !value.Equals(CodeyBoxTrailers.CoAuthoredByValue, StringComparison.Ordinal))
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
