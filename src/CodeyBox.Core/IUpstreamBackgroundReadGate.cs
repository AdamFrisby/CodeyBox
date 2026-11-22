namespace CodeyBox.Core;

/// <summary>
/// Latency-tolerant upstream reads may implement this optional capability to
/// report when the forge's shared request budget is too low to spend on
/// enrichment (status/dossier reads, background scans). Pipeline-critical
/// calls never consult it — they skip only the background reserve and still
/// honor hard rate-limit blocks at send time.
///
/// <para>Implementations must be cheap to call: no network I/O, only a lookup
/// against already-observed rate-limit state.</para>
/// </summary>
public interface IUpstreamBackgroundReadGate
{
    /// <summary>
    /// Returns a deferral describing why background reads should pause, or
    /// <c>null</c> when they may proceed. The check reflects the most recent
    /// rate-limit headers observed for this remote's credential; before any
    /// response has been seen it returns <c>null</c> (optimistic).
    /// </summary>
    Task<UpstreamReadDeferral?> GetBackgroundReadDeferralAsync(CancellationToken ct = default);
}

/// <summary>
/// Why a background upstream read is being deferred: the instant the budget
/// is expected to recover (<see cref="DeferredUntil"/>, null when unknown)
/// and a short human-readable reason safe to log.
/// </summary>
public sealed record UpstreamReadDeferral(DateTimeOffset? DeferredUntil, string Reason);
