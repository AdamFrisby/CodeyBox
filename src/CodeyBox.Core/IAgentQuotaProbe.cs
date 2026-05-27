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

    /// <summary>
    /// Marks <paramref name="member"/> as exhausted for the duration <paramref name="ttl"/>
    /// without waiting for the next periodic probe. Called by the pipeline when an
    /// agent invocation classifies as <see cref="AgentFailureKind.QuotaExhausted"/>:
    /// the probe should suppress positive availability for <paramref name="ttl"/>
    /// (or until <paramref name="resetAt"/>, whichever is sooner) so subsequent
    /// pickups skip this member.
    ///
    /// <para>
    /// Default implementation is a no-op so existing probes don't have to opt in;
    /// the in-process fallback registry held by <c>AgentClassRouter</c> still tracks
    /// short-lived exhaustion across pipeline retries even when the underlying probe
    /// has no write-back path.
    /// </para>
    /// </summary>
    Task MarkExhaustedAsync(
        AgentMembership member,
        TimeSpan ttl,
        DateTimeOffset? resetAt = null,
        CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>OAuth/subscription credentials used by quota probes.</summary>
public sealed record AgentQuotaCredentials(string? AccessToken, string? AccountId = null);

/// <summary>Point-in-time quota snapshot returned by an <see cref="IAgentQuotaProbe"/>.</summary>
public sealed record AgentQuotaSnapshot
{
    /// <summary>
    /// 0.0-100.0 percentage of overall quota remaining. Negative means unknown;
    /// the router's <c>QuotaUnknownPolicy</c> decides how to gate it. For
    /// providers with multiple cap windows (e.g. Codex 5h + weekly, Claude
    /// five_hour + seven_day), this is the minimum across the windows in
    /// <see cref="Windows"/>; the router gates on this aggregated value so a
    /// fresh short window can't hide an exhausted long one.
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

    /// <summary>
    /// Raw per-window readings for the overall account, before aggregation to
    /// <see cref="AvailablePct"/>. Lets operators see which window (e.g.
    /// weekly) is the actual constraint when the aggregated number gates a
    /// pickup. Empty when the probe has no window concept.
    /// </summary>
    public IReadOnlyList<WindowQuota> Windows { get; init; } = Array.Empty<WindowQuota>();
}

public sealed record ModelQuota
{
    public required double AvailablePct { get; init; }
    public DateTimeOffset? ResetAt { get; init; }
    public string? Window { get; init; }

    /// <summary>
    /// Raw per-window readings for this model, before aggregation to
    /// <see cref="AvailablePct"/>. Same role as
    /// <see cref="AgentQuotaSnapshot.Windows"/> but scoped to one model bucket.
    /// </summary>
    public IReadOnlyList<WindowQuota> Windows { get; init; } = Array.Empty<WindowQuota>();
}

/// <summary>
/// One window's availability reading, as observed before any cross-window or
/// overall capping. Exposed by <see cref="IAgentQuotaProbe"/> so operators can
/// see all the windows behind the aggregated <see cref="AgentQuotaSnapshot.AvailablePct"/>.
/// </summary>
public sealed record WindowQuota
{
    /// <summary>Provider's window name, e.g. <c>5h-rolling</c>, <c>weekly</c>, <c>five_hour</c>, <c>seven_day</c>.</summary>
    public required string Name { get; init; }
    /// <summary>0.0-100.0 percentage remaining in this specific window.</summary>
    public required double AvailablePct { get; init; }
    /// <summary>When this window resets, if known.</summary>
    public DateTimeOffset? ResetAt { get; init; }
}
