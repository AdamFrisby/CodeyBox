namespace CodeyBox.Core;

/// <summary>
/// Probes the available quota for a specific agent kind. Implementations are
/// registered as <c>IEnumerable&lt;IAgentQuotaProbe&gt;</c> in DI; the router
/// resolves by <see cref="Kind"/>.
///
/// Implementations MUST be thread-safe (the router may call concurrently from
/// multiple worker threads) and MUST be fail-open: any error returns
/// <see cref="AgentQuotaSnapshot.AvailablePct"/> = -1 so that a broken
/// endpoint never blocks work items.
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
    /// 0.0–100.0 percentage of quota remaining. Negative means unknown — treat
    /// as available (fail-open). The router gates on
    /// <c>AvailablePct &lt; 0 || AvailablePct &gt;= MinQuotaPct</c>.
    /// </summary>
    public required double AvailablePct { get; init; }

    /// <summary>When the quota window is expected to reset, if known.</summary>
    public DateTimeOffset? ResetAt { get; init; }

    /// <summary>Human-readable notes, e.g. "endpoint returned 404".</summary>
    public string? Notes { get; init; }
}
