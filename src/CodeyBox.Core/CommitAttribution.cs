using System.Text;

namespace CodeyBox.Core;

/// <summary>
/// Host-level defaults for commit/PR attribution, bound from
/// <c>CodeyBox:CommitAttribution</c>. All flags default to true (today's
/// behaviour). Hot-reloadable through <see cref="CommitAttributionSnapshot"/>.
/// </summary>
public sealed record CommitAttributionOptions
{
    public bool CoAuthoredBy { get; init; } = true;
    public bool CodeyBoxTrailers { get; init; } = true;
    public bool PullRequestFooter { get; init; } = true;
}

/// <summary>
/// Swappable holder for the current <see cref="CommitAttributionOptions"/>.
/// Same pattern as SmokeOptionsSnapshot: dispatch paths read
/// <see cref="Current"/> live so a config reload takes effect on the next
/// commit without a restart.
/// </summary>
public sealed class CommitAttributionSnapshot
{
    private CommitAttributionOptions _current;

    public CommitAttributionSnapshot(CommitAttributionOptions initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _current = initial;
    }

    public CommitAttributionOptions Current => Volatile.Read(ref _current);

    public void Replace(CommitAttributionOptions next)
    {
        ArgumentNullException.ThrowIfNull(next);
        Volatile.Write(ref _current, next);
    }
}

/// <summary>
/// Nullable per-project override for attribution. Null per flag means
/// "inherit the host default". Merged in ProjectRepository from
/// project entry with fallback to Defaults; resolution against the live
/// host snapshot happens in <see cref="CommitAttributionPolicy"/> at use
/// time so host hot-reload applies without a project reload.
/// </summary>
public sealed record CommitAttributionOverride
{
    public bool? CoAuthoredBy { get; init; }
    public bool? CodeyBoxTrailers { get; init; }
    public bool? PullRequestFooter { get; init; }
}

/// <summary>
/// Effective attribution for one commit/PR decision. Resolved per project
/// (project override wins over the host default).
/// </summary>
public sealed record CommitAttribution
{
    public static CommitAttribution Default { get; } = new(true, true, true);

    public CommitAttribution(bool includeCoAuthoredBy, bool includeCodeyBoxTrailers, bool includePullRequestFooter)
    {
        IncludeCoAuthoredBy = includeCoAuthoredBy;
        IncludeCodeyBoxTrailers = includeCodeyBoxTrailers;
        IncludePullRequestFooter = includePullRequestFooter;
    }

    public bool IncludeCoAuthoredBy { get; }
    public bool IncludeCodeyBoxTrailers { get; }
    public bool IncludePullRequestFooter { get; }
}

/// <summary>
/// Single source of truth for attribution decisions. Every commit/PR/prompt
/// writer resolves through this policy; no scattered config reads.
/// Inject it and call <see cref="Resolve"/> with the active project (or its
/// override) on each use so hot-reload applies to the next commit.
/// </summary>
public sealed class CommitAttributionPolicy
{
    private readonly CommitAttributionSnapshot _snapshot;

    public CommitAttributionPolicy(CommitAttributionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshot = snapshot;
    }

    public CommitAttribution Resolve(CommitAttributionOverride? projectOverride)
    {
        var host = _snapshot.Current;
        return new CommitAttribution(
            projectOverride?.CoAuthoredBy ?? host.CoAuthoredBy,
            projectOverride?.CodeyBoxTrailers ?? host.CodeyBoxTrailers,
            projectOverride?.PullRequestFooter ?? host.PullRequestFooter);
    }

    public CommitAttribution Resolve(Project? project) => Resolve(project?.CommitAttribution);

    public CommitAttribution CurrentDefault => Resolve((CommitAttributionOverride?)null);

    /// <summary>
    /// Defence in depth: strip disabled trailer lines from a host-composed
    /// final message, in case an agent added them anyway. Only exact trailer
    /// keys are removed (a line whose first token is exactly the key followed
    /// by a colon, with an optional leading markdown emphasis marker); prose
    /// that merely mentions a key is left intact.
    /// </summary>
    public string StripDisabledTrailers(string message, CommitAttribution attribution)
        => StripDisabledTrailers(message, attribution.IncludeCoAuthoredBy, attribution.IncludeCodeyBoxTrailers);

    public static string StripDisabledTrailers(string message, bool includeCoAuthoredBy, bool includeCodeyBoxTrailers)
    {
        if (includeCoAuthoredBy && includeCodeyBoxTrailers)
            return message;
        if (string.IsNullOrEmpty(message))
            return message;

        var stripCoAuthor = !includeCoAuthoredBy;
        var stripCodeyBox = !includeCodeyBoxTrailers;
        var lines = message.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var kept = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            if (IsTrailerLine(line, out var key))
            {
                if (stripCoAuthor && key.Equals("Co-Authored-By", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (stripCodeyBox && key.StartsWith("CodeyBox-", StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            kept.Add(line);
        }

        var result = string.Join("\n", kept);
        return CollapseExcessBlankLines(result);
    }

    internal static bool IsTrailerLine(string line, out string key)
    {
        key = string.Empty;
        if (string.IsNullOrWhiteSpace(line))
            return false;
        var trimmed = line.Trim();
        if (trimmed.StartsWith("*", StringComparison.Ordinal))
            trimmed = trimmed[1..].TrimStart();
        var colon = trimmed.IndexOf(':');
        if (colon <= 0)
            return false;
        var candidate = trimmed[..colon].Trim();
        if (candidate.Length == 0 || candidate.Contains(' ') || candidate.Contains('\t'))
            return false;
        foreach (var c in candidate)
        {
            if (!(char.IsLetterOrDigit(c) || c == '-'))
                return false;
        }
        key = candidate;
        return true;
    }

    private static string CollapseExcessBlankLines(string text)
    {
        while (text.Contains("\n\n\n", StringComparison.Ordinal))
            text = text.Replace("\n\n\n", "\n\n", StringComparison.Ordinal);
        return text.TrimEnd();
    }

    /// <summary>
    /// Join a subject with an (optional) trailer block. When attribution is
    /// fully disabled the block is empty and the subject is returned alone
    /// (no dangling blank line); otherwise subject + blank line + block.
    /// The result is passed through <see cref="StripDisabledTrailers"/> so a
    /// caller that embedded agent text still enforces the policy.
    /// </summary>
    public string ComposeMessage(string subject, string trailerBlock, CommitAttribution attribution)
        => ComposeMessageStatic(subject, trailerBlock, attribution);

    /// <summary>
    /// Static join used by static call sites (e.g. conflict-resolution commit)
    /// that already hold a resolved <see cref="CommitAttribution"/> but have no
    /// policy instance. Same single source of truth as the instance overload.
    /// </summary>
    public static string ComposeMessageStatic(string subject, string trailerBlock, CommitAttribution attribution)
    {
        subject ??= string.Empty;
        trailerBlock ??= string.Empty;
        string message = string.IsNullOrWhiteSpace(trailerBlock)
            ? subject.TrimEnd()
            : $"{subject.TrimEnd()}\n\n{trailerBlock.Trim()}";
        return StripDisabledTrailers(message, attribution.IncludeCoAuthoredBy, attribution.IncludeCodeyBoxTrailers);
    }
}
