namespace CodeyBox.Core;

/// <summary>
/// Loop prevention, solved once here in the abstraction rather than per
/// provider. Every backend has webhooks and <see cref="IWorkTracker"/> writes
/// to the same objects <see cref="IWorkSource"/> reads, so a CodeyBox-authored
/// progress comment must not trigger ingestion or another update. Changes
/// CodeyBox itself caused are identifiable two ways — a content marker and a
/// service-account login — and are ignored on the way back in.
/// </summary>
public static class WorkSyncLoopGuard
{
    private const string MarkerPrefix = "<!-- codeybox-work-item:";

    private const string MarkerSuffix = " -->";

    /// <summary>Maximum upstream body length the guard scans per segment. Bodies are
    /// truncated to the sync cap before they reach the store, so a bounded
    /// scan is sufficient; over-long bodies are still recognised by prefix
    /// because the head and tail segments are both scanned (the marker is
    /// appended at the end, so it lands in the tail).</summary>
    public const int MaxScanChars = 64 * 1024;

    /// <summary>Builds the marker identifying content authored by CodeyBox for <paramref name="id"/>.</summary>
    public static string MarkerFor(WorkItemId id) => $"{MarkerPrefix}{id.Value}{MarkerSuffix}";

    /// <summary>
    /// Appends the CodeyBox marker to an outbound body. The tracker service
    /// applies this to every body before handing it to a provider, so all
    /// providers inherit loop safety without implementing it themselves.
    /// </summary>
    public static string Mark(string body, WorkItemId id)
    {
        ArgumentNullException.ThrowIfNull(body);
        return body.Contains(MarkerFor(id), StringComparison.Ordinal)
            ? body
            : $"{body}\n\n{MarkerFor(id)}";
    }

    /// <summary>
    /// True when <paramref name="body"/> carries the CodeyBox work-item marker.
    /// Providers call this to recognise their own writes (e.g. a surfaced
    /// question comment must never parse back as a reply) without re-encoding
    /// the marker literal.
    /// </summary>
    public static bool CarriesMarker(string? body)
    {
        if (string.IsNullOrEmpty(body))
            return false;
        if (body.Length <= MaxScanChars)
            return body.Contains(MarkerPrefix, StringComparison.Ordinal);
        // Bounded scan: check the head and tail segments so over-long
        // bodies carrying the marker (appended at the end) are still
        // recognised without scanning unbounded input.
        return body.AsSpan(0, MaxScanChars).Contains(MarkerPrefix.AsSpan(), StringComparison.Ordinal)
            || body.AsSpan(body.Length - MaxScanChars, MaxScanChars).Contains(
                MarkerPrefix.AsSpan(), StringComparison.Ordinal);
    }

    /// <summary>
    /// True when upstream content was authored by CodeyBox: the body carries
    /// our marker, or the last actor is one of the configured service logins
    /// (exact match, ordinal-ignore-case — never substring). Either signal
    /// suffices because providers may strip HTML comments when rendering.
    /// </summary>
    public static bool IsCodeyBoxAuthored(
        string? body,
        string? lastActorLogin,
        IReadOnlySet<string> serviceLogins)
    {
        if (CarriesMarker(body))
            return true;

        if (string.IsNullOrWhiteSpace(lastActorLogin))
            return false;
        string trimmed = lastActorLogin.Trim();
        foreach (string login in serviceLogins)
        {
            if (string.Equals(login, trimmed, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
