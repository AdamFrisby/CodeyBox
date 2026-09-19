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

    /// <summary>Maximum upstream body length the guard scans. Bodies are
    /// truncated to the sync cap before they reach the store, so a bounded
    /// scan is sufficient; over-long bodies are still recognised by prefix.</summary>
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
        return body.Contains(MarkerPrefix, StringComparison.Ordinal)
            ? body
            : $"{body}\n\n{MarkerFor(id)}";
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
        if (!string.IsNullOrEmpty(body)
            && body.Length <= MaxScanChars + MarkerPrefix.Length + 64
            && body.Contains(MarkerPrefix, StringComparison.Ordinal))
            return true;

        return !string.IsNullOrWhiteSpace(lastActorLogin)
            && serviceLogins.Contains(lastActorLogin.Trim());
    }
}
