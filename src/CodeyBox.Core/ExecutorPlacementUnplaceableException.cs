namespace CodeyBox.Core;

/// <summary>
/// A phase cannot place because its work item demands a capability no
/// registered executor host provides. Permanent until registration changes:
/// the caller must report the item as unplaceable naming
/// <see cref="UnmetCapability"/> — not dispatch it, not fail it as an agent
/// failure, and not consume a rework iteration. Distinct from
/// <see cref="SandboxProvisioningDeferredException"/> (transient: capacity,
/// cordon, health — requeue under backoff) and from
/// <see cref="ExecutorPhaseTransportException"/> (host failure — retry
/// elsewhere) so each outcome keeps its own recovery path.
/// </summary>
public sealed class ExecutorPlacementUnplaceableException : Exception
{
    public ExecutorPlacementUnplaceableException(
        string unmetCapability,
        string detail,
        ExecutorPlacementDecision? decision = null,
        Exception? innerException = null)
        : base(BuildMessage(unmetCapability, detail), innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unmetCapability);
        UnmetCapability = unmetCapability.Trim();
        Detail = detail;
        Decision = decision;
    }

    /// <summary>Required capability tag no registered host declares.</summary>
    public string UnmetCapability { get; }

    /// <summary>Per-candidate placement detail for logs; carries host ids and reasons only.</summary>
    public string Detail { get; }

    /// <summary>Full placement decision, when the caller computed one.</summary>
    public ExecutorPlacementDecision? Decision { get; }

    private static string BuildMessage(string unmetCapability, string detail)
    {
        var suffix = string.IsNullOrWhiteSpace(detail) ? "" : $": {detail.Trim()}";
        return $"executor placement unplaceable: no registered host provides capability '{unmetCapability.Trim()}'{suffix}";
    }
}
