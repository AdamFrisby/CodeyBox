namespace CodeyBox.Core;

/// <summary>
/// Correlation token binding a notification to the work item question being
/// decided. The token is echoed back by inbound interactions so a stale
/// message cannot answer a superseded question. Format:
/// <c>{workItemId}:{questionId}</c> — exact, parseable, and safe to echo
/// back over the wire.
///
/// <para>Lives in Core (not Notifications) so out-of-process contributors —
/// notification provider plugins, which may only reference the SDK surface —
/// bind and parse the same token as the host's inbound endpoint. One source
/// of truth: do not re-implement this format elsewhere.</para>
/// </summary>
public static class NotificationCorrelation
{
    /// <summary>Build the token for one work item question.</summary>
    public static string TokenFor(string workItemId, string questionId) =>
        $"{workItemId}:{questionId}";

    /// <summary>Split a token back into work item and question ids.
/// Returns false for null, empty, or malformed tokens.</summary>
    public static bool TryParse(string? token, out string workItemId, out string questionId)
    {
        workItemId = string.Empty;
        questionId = string.Empty;
        if (string.IsNullOrEmpty(token))
            return false;
        var split = token.IndexOf(':');
        if (split <= 0 || split == token.Length - 1)
            return false;
        workItemId = token[..split];
        questionId = token[(split + 1)..];
        return true;
    }
}
