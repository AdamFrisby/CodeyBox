namespace CodeyBox.Core;

/// <summary>
/// A sandbox placement was refused because a required capability is declared
/// by no member available to the work (after intersecting member declarations
/// with what each member's provider actually implements). Permanent: the item
/// can never place until registration changes, so callers must surface this
/// as an operator-facing failure naming <see cref="UnmetCapability"/> — never
/// as a retry loop. Transient refusals (capacity, cordon, health, credential
/// or profile mismatch on the currently-available set) surface instead as
/// <see cref="SandboxProvisioningDeferredException"/> so the existing
/// backoff requeues the item.
/// </summary>
public sealed class SandboxPlacementUnplaceableException : Exception
{
    public SandboxPlacementUnplaceableException(
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

    /// <summary>Required capability tag no available member provides.</summary>
    public string UnmetCapability { get; }

    /// <summary>Per-candidate placement detail for logs; carries member ids and reasons only.</summary>
    public string Detail { get; }

    /// <summary>Full placement decision, when the caller computed one.</summary>
    public ExecutorPlacementDecision? Decision { get; }

    private static string BuildMessage(string unmetCapability, string detail)
    {
        var suffix = string.IsNullOrWhiteSpace(detail) ? "" : $": {detail.Trim()}";
        return $"sandbox placement unplaceable: no available member provides capability '{unmetCapability.Trim()}'{suffix}";
    }
}
