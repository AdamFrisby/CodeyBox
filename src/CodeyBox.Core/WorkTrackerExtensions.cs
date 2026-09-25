namespace CodeyBox.Core;

/// <summary>
/// Shared guards every <see cref="IWorkTracker"/> applies to inbound post
/// requests, defined once so the "is this mine to write to" check cannot
/// drift per provider.
/// </summary>
public static class WorkTrackerExtensions
{
    /// <summary>
    /// Returns a <see cref="TrackerPostOutcome.NotTracked"/> result when the
    /// request names another provider's namespace or carries no external id;
    /// null when the tracker may proceed.
    /// </summary>
    public static TrackerPostResult? CheckTracked(
        this IWorkTracker tracker, string @namespace, string externalId)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        if (!string.Equals(@namespace, tracker.Namespace, StringComparison.OrdinalIgnoreCase))
            return new TrackerPostResult(TrackerPostOutcome.NotTracked,
                Detail: $"namespace '{@namespace}' is not tracked by '{tracker.Namespace}'");
        if (string.IsNullOrWhiteSpace(externalId))
            return new TrackerPostResult(TrackerPostOutcome.NotTracked,
                Detail: "no external id to track");
        return null;
    }
}
