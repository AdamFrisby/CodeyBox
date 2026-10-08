namespace CodeyBox.Core;

/// <summary>
/// Point-in-time view of a wedged baseline-bake loop: the provider has no
/// usable baseline and every provisioning attempt is being deferred. Surfaced
/// in <c>/queue/status</c> and the <c>baseline_provisioning_blocked</c>
/// notification condition so a deterministic bake failure reads as blocked —
/// not idle — without requiring a process restart to observe.
/// </summary>
public sealed record BaselineProvisioningBlockedStatus
{
    /// <summary>When the current consecutive-failure streak started.</summary>
    public required DateTimeOffset BlockedSince { get; init; }

    /// <summary>Sandbox provider reporting the blockage (e.g. "incus").</summary>
    public required string Provider { get; init; }

    /// <summary>Content-addressed baseline that failed to bake.</summary>
    public required string BaselineName { get; init; }

    /// <summary>Bounded, single-line, sanitized bake-failure cause.</summary>
    public required string LastBakeCause { get; init; }

    /// <summary>Consecutive bake failures for this baseline.</summary>
    public required int ConsecutiveFailures { get; init; }

    /// <summary>When the most recent bake failure was observed.</summary>
    public required DateTimeOffset LastFailureAt { get; init; }
}

/// <summary>
/// Read-side provider exposing whether baseline provisioning is currently
/// blocked. Implemented by bake-capable sandbox providers; consumed by queue
/// status reporting and the operator notification condition.
/// </summary>
public interface IBaselineProvisioningBlockedStatusProvider
{
    BaselineProvisioningBlockedStatus? GetProvisioningBlockedStatus();
}

/// <summary>
/// Shared, thread-safe record of consecutive baseline-bake failures.
/// Bake-capable providers record each bake failure and clear on success; queue
/// status and the <c>baseline_provisioning_blocked</c> notification condition
/// read the snapshot. Shared as a singleton so admission-control wrappers —
/// which do not forward provider-specific interfaces — cannot hide it.
/// </summary>
public sealed class BaselineProvisioningBlockedTracker : IBaselineProvisioningBlockedStatusProvider
{
    private readonly TimeProvider _time;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TrackerEntry> _failures = new(StringComparer.Ordinal);

    private sealed record TrackerEntry(
        int ConsecutiveFailures,
        DateTimeOffset FirstBlockedAt,
        DateTimeOffset LastFailureAt,
        string Provider,
        string LastBakeCause);

    public BaselineProvisioningBlockedTracker(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Records one bake failure for <paramref name="baselineName"/>.</summary>
    public void RecordFailure(string provider, string baselineName, string bakeCause)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(baselineName);
        ArgumentException.ThrowIfNullOrWhiteSpace(bakeCause);
        var now = _time.GetUtcNow();
        _failures.AddOrUpdate(
            baselineName,
            static (_, state) => new TrackerEntry(1, state.Now, state.Now, state.Provider, state.Cause),
            static (_, existing, state) => new TrackerEntry(
                existing.ConsecutiveFailures + 1,
                existing.FirstBlockedAt == DateTimeOffset.MinValue ? state.Now : existing.FirstBlockedAt,
                state.Now,
                state.Provider,
                state.Cause),
            (Now: now, Provider: provider, Cause: bakeCause));
    }

    /// <summary>Clears the failure streak for <paramref name="baselineName"/> (bake succeeded).</summary>
    public void RecordSuccess(string baselineName) =>
        _failures.TryRemove(baselineName, out _);

    /// <summary>Consecutive bake failures currently recorded for <paramref name="baselineName"/>.</summary>
    public int GetConsecutiveFailures(string baselineName) =>
        _failures.TryGetValue(baselineName, out var entry) ? entry.ConsecutiveFailures : 0;

    public BaselineProvisioningBlockedStatus? GetProvisioningBlockedStatus()
    {
        if (_failures.IsEmpty)
            return null;
        string? oldestName = null;
        TrackerEntry? oldest = null;
        foreach (var (name, entry) in _failures)
        {
            if (oldest is null || entry.FirstBlockedAt < oldest.FirstBlockedAt)
            {
                oldestName = name;
                oldest = entry;
            }
        }
        if (oldest is null || oldestName is null)
            return null;
        return new BaselineProvisioningBlockedStatus
        {
            BlockedSince = oldest.FirstBlockedAt,
            Provider = oldest.Provider,
            BaselineName = oldestName,
            LastBakeCause = oldest.LastBakeCause,
            ConsecutiveFailures = oldest.ConsecutiveFailures,
            LastFailureAt = oldest.LastFailureAt,
        };
    }
}
