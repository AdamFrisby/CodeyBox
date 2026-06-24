namespace CodeyBox.Core;

public static class WorkItemFailureKinds
{
    public const string AuthRequired = "auth_required";

    /// <summary>
    /// Sandbox/provisioning failures the pipeline classified as infrastructure
    /// (binary missing, materialisation failure, network blip in setup). The
    /// agent's reasoning loop never meaningfully started.
    /// </summary>
    public const string Infrastructure = "infrastructure";

    /// <summary>
    /// Pickup-time credential / smoke gate refused dispatch. The credential
    /// may have rotated since the cache was filled; re-probing on a later
    /// retry is the recovery path.
    /// </summary>
    public const string AgentUnavailable = "agent_unavailable";

    /// <summary>
    /// Failure kinds whose root cause is the AGENT's environment — when the
    /// agent recovers (smoke passes again, missing binary installed, auth
    /// fixed), the pipeline can usefully re-attempt items that failed for
    /// these reasons during the outage. Genuine code-work failures (build,
    /// agent, configuration, audit non-convergence) are excluded — those
    /// would only re-fail on the same input.
    /// </summary>
    public static readonly IReadOnlyList<string> InfraShaped =
    [
        Infrastructure,
        AgentUnavailable,
        AuthRequired,
    ];

    public static bool IsInfraShaped(string? failureKind)
    {
        if (string.IsNullOrEmpty(failureKind)) return false;
        foreach (var candidate in InfraShaped)
        {
            if (string.Equals(failureKind, candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
