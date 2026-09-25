namespace CodeyBox.Core;

/// <summary>
/// Shared text handling for work-sync provider plugins: the one truncation
/// helper and the one outbound-comment clip every <see cref="IWorkTracker"/>
/// applies, so the limit and the loop-guard-marker invariant cannot drift
/// per provider.
/// </summary>
public static class WorkSyncText
{
    /// <summary>Maximum comment body posted upstream (provider text-limit guard).</summary>
    public const int MaxCommentChars = 32 * 1024;

    /// <summary>Truncates <paramref name="value"/> to <paramref name="maxChars"/>.</summary>
    public static string Truncate(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..maxChars];

    /// <summary>
    /// Clips an outbound comment body to <see cref="MaxCommentChars"/>.
    /// Throws <see cref="ArgumentException"/> on an empty body. When the body
    /// carries the caller's loop-guard marker the clip reserves room for it —
    /// a plain head-clip would cut the appended marker off and our own write
    /// would not be recognised as CodeyBox-authored on the way back in.
    /// </summary>
    public static string ClipComment(string body, WorkItemId workItemId)
    {
        if (string.IsNullOrEmpty(body))
            throw new ArgumentException("tracker body must not be empty", nameof(body));
        if (body.Length <= MaxCommentChars)
            return body;
        var marker = WorkSyncLoopGuard.MarkerFor(workItemId);
        if (!body.Contains(marker, StringComparison.Ordinal))
            return body[..MaxCommentChars];
        return string.Concat(body.AsSpan(0, MaxCommentChars - marker.Length), marker);
    }
}
