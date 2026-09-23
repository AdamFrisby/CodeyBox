namespace CodeyBox.Core;

/// <summary>
/// The single safe-link policy for URLs notification providers emit into
/// rendered output or click targets. Lives in Core (like
/// <see cref="NotificationCorrelation"/>) so every provider applies the
/// same rule — never a per-plugin fork.
/// </summary>
public static class NotificationLinks
{
    /// <summary>
    /// The parsed URI when <paramref name="url"/> is safe to emit as a
    /// rendered link or click target: an absolute http/https URI with no
    /// user-info, else null. Emitters should write the returned URI's
    /// <see cref="Uri.AbsoluteUri"/> (canonical, percent-encoded) rather
    /// than the raw input string. User-info is refused because
    /// <c>https://user:secret@host/</c> smuggles credentials into rendered
    /// output and lets a link's visible origin lie.
    /// </summary>
    public static Uri? SafeLinkUri(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            return null;
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return null;
        return uri;
    }

    /// <summary>Deep link to the Agnes front end for the owning work item.
    /// Agnes steers; notification integrations only link. Returns null when
    /// no base URL is configured, the base is not a clean absolute http(s)
    /// URL (a query or fragment would corrupt the composed path), or no
    /// work item is bound.</summary>
    public static string? AgnesWorkItemUrl(string? agnesBaseUrl, string? workItemId)
    {
        if (string.IsNullOrWhiteSpace(workItemId))
            return null;
        var baseUri = SafeLinkUri(agnesBaseUrl);
        if (baseUri is null
            || !string.IsNullOrEmpty(baseUri.Query)
            || !string.IsNullOrEmpty(baseUri.Fragment))
            return null;
        return $"{baseUri.AbsoluteUri.TrimEnd('/')}/workitems/{Uri.EscapeDataString(workItemId)}";
    }
}
