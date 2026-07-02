namespace CodeyBox.Core;

/// <summary>
/// Probes the available quota for a specific agent kind. Implementations are
/// registered as <c>IEnumerable&lt;IAgentQuotaProbe&gt;</c> in DI; the router
/// resolves by <see cref="Kind"/>.
///
/// Implementations MUST be thread-safe (the router may call concurrently from
/// multiple worker threads). When a probe cannot produce a real reading it
/// returns an <em>unknown</em> snapshot (<see cref="AgentQuotaSnapshot.IsKnown"/>
/// is false) tagged with a <see cref="QuotaUnknownReason"/>; the router's
/// configured unknown policy decides whether that fails open or falls through,
/// and the last-known-good layer uses the reason to decide whether a prior good
/// reading may be retained.
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

/// <summary>
/// Why a quota snapshot carries no real availability reading. The
/// last-known-good layer keys its retain/discard decision off this: a
/// <see cref="Transient"/> blip leaves the account state unchanged (retain a
/// recent good reading), while <see cref="Permanent"/> / <see cref="NoCredential"/>
/// mean the prior reading can no longer be trusted (discard it).
/// </summary>
public enum QuotaUnknownReason
{
    /// <summary>Network blip, timeout, 5xx, or a 429 on the probe endpoint
    /// itself — transient; a recent good reading may be retained.</summary>
    Transient,

    /// <summary>Contract/permission failure: a non-429 4xx, revoked token,
    /// unparseable body, or unexpected response shape. Drop any retained
    /// last-known-good — the prior reading is no longer trustworthy.</summary>
    Permanent,

    /// <summary>No credential is configured for this member: nothing to read and
    /// nothing to retain.</summary>
    NoCredential,
}

/// <summary>Helpers for classifying quota-probe unknowns.</summary>
public static class QuotaUnknownReasons
{
    /// <summary>
    /// Classifies a non-success, non-429 HTTP status: 5xx and 408 are
    /// <see cref="QuotaUnknownReason.Transient"/> (server-side blip — retain a
    /// recent good reading); every other status (4xx auth/permission/contract)
    /// is <see cref="QuotaUnknownReason.Permanent"/> (don't trust stale data).
    /// 429 is NOT routed here — probes treat it as a known 0% exhaustion.
    /// </summary>
    public static QuotaUnknownReason FromHttpStatus(System.Net.HttpStatusCode status) =>
        (int)status >= 500 || status == System.Net.HttpStatusCode.RequestTimeout
            ? QuotaUnknownReason.Transient
            : QuotaUnknownReason.Permanent;
}

/// <summary>Point-in-time quota snapshot returned by an <see cref="IAgentQuotaProbe"/>.</summary>
public sealed record AgentQuotaSnapshot
{
    /// <summary>
    /// 0.0-100.0 percentage of overall quota remaining. <b>Only meaningful when
    /// <see cref="IsKnown"/> is true</b> — for an unknown snapshot this is a
    /// placeholder; gate on <see cref="IsKnown"/>/<see cref="Unknown"/>, never on
    /// the sign of this number. For providers with multiple cap windows (e.g.
    /// Codex 5h + weekly, Claude five_hour + seven_day), this is the minimum
    /// across the windows in <see cref="Windows"/>; the router gates on this
    /// aggregated value so a fresh short window can't hide an exhausted long one.
    /// </summary>
    public required double AvailablePct { get; init; }

    /// <summary>
    /// Null for a real reading; otherwise the reason the probe could not produce
    /// one — which drives how the last-known-good layer treats it (retain vs
    /// discard). <see cref="IsKnown"/> is the convenience inverse.
    /// </summary>
    public QuotaUnknownReason? Unknown { get; init; }

    /// <summary>
    /// True when this snapshot is a real availability reading. Requires both an
    /// absent <see cref="Unknown"/> reason and a valid (non-negative)
    /// <see cref="AvailablePct"/> — a negative percentage is definitionally not a
    /// reading, so it is treated as unknown even if a reason was never set.
    /// </summary>
    public bool IsKnown => Unknown is null && AvailablePct >= 0;

    /// <summary>Builds an unknown snapshot with the given reason.</summary>
    public static AgentQuotaSnapshot UnknownSnapshot(QuotaUnknownReason reason, string? notes = null) =>
        new() { AvailablePct = -1, Unknown = reason, Notes = notes };

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

    /// <summary>
    /// Number of banked manual quota resets the account can still spend, when
    /// the provider exposes it (Codex's top-level
    /// <c>rate_limit_reset_credits.available_count</c>). Null when the provider
    /// has no reset-credit concept or the field was absent. Purely
    /// informational — it does not gate routing; it is the foundation for the
    /// reset-credit-expiry tracker and reset advisor.
    /// </summary>
    public int? ResetCreditsAvailable { get; init; }
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

    /// <summary>
    /// Raw <c>used_percent</c> as reported by the provider, before it is
    /// inverted/clamped into <see cref="AvailablePct"/>. Null when the provider
    /// gates the window on an explicit deny flag rather than a percentage.
    /// Exposed so operators (and the reset advisor) can see the untransformed
    /// consumption figure.
    /// </summary>
    public double? UsedPercent { get; init; }

    /// <summary>
    /// Raw <c>reset_at</c> epoch (Unix seconds) as reported by the provider,
    /// preserved verbatim alongside the parsed <see cref="ResetAt"/>. Null when
    /// the provider expresses the reset as a non-numeric value or omits it.
    /// </summary>
    public long? ResetAtEpochSeconds { get; init; }
}
