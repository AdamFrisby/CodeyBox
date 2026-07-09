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
    /// Aggregate routing/capacity failure where no single agent was invoked
    /// and therefore no restored-agent sweep can safely attribute blame.
    /// </summary>
    public const string AgentRoutingUnavailable = "agent_routing_unavailable";

    private static readonly string[] InfraShaped =
    [
        Infrastructure,
        AgentUnavailable,
        AuthRequired,
    ];

    public static bool IsInfraShaped(string? failureKind)
        => !string.IsNullOrEmpty(failureKind)
            && InfraShaped.Contains(failureKind, StringComparer.OrdinalIgnoreCase);
}
