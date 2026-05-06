namespace CodeyBox.Core;

/// <summary>
/// Probes the available quota for a specific agent kind. Implementations are
/// registered as <c>IEnumerable&lt;IAgentQuotaProbe&gt;</c> in DI; the router
/// resolves by <see cref="Kind"/>.
///
/// Implementations MUST be thread-safe (the router may call concurrently from
/// multiple worker threads). Any probe error returns
/// <see cref="AgentQuotaSnapshot.AvailablePct"/> = -1; the router's
/// configured unknown policy decides whether that fails open or falls through.
/// </summary>
public interface IAgentQuotaProbe
{
    /// <summary>The agent kind this probe covers.</summary>
    AgentKind Kind { get; }

    /// <summary>Returns a quota snapshot, possibly from an in-process cache.</summary>
    Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct);
}

/// <summary>Point-in-time quota snapshot returned by an <see cref="IAgentQuotaProbe"/>.</summary>
public sealed record AgentQuotaSnapshot
{
    /// <summary>
    /// 0.0-100.0 percentage of overall quota remaining. Negative means unknown;
    /// the router's <c>QuotaUnknownPolicy</c> decides how to gate it.
    /// </summary>
    public required double AvailablePct { get; init; }

    /// <summary>When the quota window is expected to reset, if known.</summary>
    public DateTimeOffset? ResetAt { get; init; }

    /// <summary>Human-readable notes, e.g. "endpoint returned 404".</summary>
    public string? Notes { get; init; }

    /// <summary>
    /// Per-model quota breakdown. Empty when the probe has no model-specific
    /// information. Key = model id; value = available percentage and reset.
    /// </summary>
    public IReadOnlyDictionary<string, ModelQuota> PerModel { get; init; } =
        new Dictionary<string, ModelQuota>(StringComparer.OrdinalIgnoreCase);
}

public sealed record ModelQuota
{
    public required double AvailablePct { get; init; }
    public DateTimeOffset? ResetAt { get; init; }
    public string? Window { get; init; }
}
