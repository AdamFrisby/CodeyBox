namespace CodeyBox.Core;

/// <summary>
/// Structured scope for an <c>auth_required</c> work-item failure.
/// </summary>
public enum WorkItemAuthFailureScope
{
    /// <summary>
    /// The auth evidence was corroborated or otherwise trusted enough to bench
    /// the agent fleet-wide. Restore sweeps may retry these items when that
    /// agent is restored.
    /// </summary>
    Fleet = 1,

    /// <summary>
    /// The auth evidence was accepted only for this work item, typically
    /// stdout-only evidence that forced in-VM smoke did not corroborate. Restore
    /// sweeps must not treat these as fleet-outage victims.
    /// </summary>
    Item = 2,
}
