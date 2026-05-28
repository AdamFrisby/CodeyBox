namespace CodeyBox.Core;

/// <summary>
/// In-process cache for in-VM smoke results, keyed by
/// <c>(AgentKind, baselineImageRef)</c>. The baseline ref is a content hash of
/// the sandbox image, so a rebake produces a new ref and the prior entry is
/// never read again — that is the cache-invalidation mechanism for
/// AC#3 (baseline rebake re-runs the probes). A short TTL bounds staleness for
/// non-baseline providers (process / bubblewrap) whose ref is a fixed sentinel.
/// Thread-safe. Not persisted — cleared on orchestrator restart.
/// </summary>
public interface IInVmSmokeCache
{
    /// <summary>Returns a cached result if still within TTL, or null if expired or absent.</summary>
    AgentSmokeResult? TryGet(AgentKind kind, string baselineRef);

    /// <summary>Stores a smoke result with the configured TTL.</summary>
    void Set(AgentKind kind, string baselineRef, AgentSmokeResult result);

    /// <summary>
    /// Drops every cached entry for <paramref name="kind"/> across all baseline
    /// refs. Called when an operator resets an agent (<c>/admin/agent/{name}/reset</c>)
    /// so the next sweep / dispatch re-execs the CLI from scratch instead of
    /// replaying a verdict captured before the operator's fix.
    /// </summary>
    void Invalidate(AgentKind kind);
}
