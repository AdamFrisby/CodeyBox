using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Per-process availability tracker for each registered agent. Two signals
/// feed it:
/// <list type="number">
///   <item>
///     <b>Smoke probe</b> results — fed in by the credential probes
///     (<see cref="StartupSmokeProbeService"/> / <see cref="PeriodicSmokeProbeService"/>,
///     host-side API checks) and by <see cref="InVmSmokeProber"/> (in-sandbox
///     CLI checks: binary present, auth materialised). A failed probe excludes
///     the agent until a subsequent probe passes or an operator resets it.
///   </item>
///   <item>
///     <b>Fast-fail circuit breaker</b> — runs that exit non-zero in less than
///     <see cref="AvailabilityOptions.FastFailThresholdSeconds"/> count as
///     "smoke-style" failures. After
///     <see cref="AvailabilityOptions.MaxConsecutiveFastFails"/> consecutive
///     fast-fails the agent is excluded. A successful run (or a normal-length
///     failure) resets the counter.
///   </item>
/// </list>
///
/// <para>
/// Distinct from <see cref="IQuotaFailureStore"/>: that store handles
/// rate-limit / quota-shaped failures (exit 1 with provider quota signals);
/// this registry handles "the binary isn't even working" failures (exit 127,
/// instant non-zero exits, broken credentials). Both excluded paths show up
/// separately in audit and <c>/concurrency</c> so operators can tell them apart.
/// </para>
///
/// <para>Thread-safe; updates use a small per-agent lock so concurrent
/// outcomes from many in-flight items don't corrupt counters.</para>
/// </summary>
public sealed class AgentAvailabilityRegistry : IAgentAvailabilityRegistry, ISmokeAvailabilityRegistry
{
    private readonly AvailabilityOptions _opts;
    private readonly TimeProvider _time;
    private readonly ILogger<AgentAvailabilityRegistry> _log;
    private readonly ConcurrentDictionary<AgentKind, AgentAvailabilityEntry> _entries = new();

    public AgentAvailabilityRegistry(
        AvailabilityOptions opts,
        TimeProvider? time = null,
        ILogger<AgentAvailabilityRegistry>? log = null)
    {
        _opts = opts;
        _time = time ?? TimeProvider.System;
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentAvailabilityRegistry>.Instance;
    }

    /// <summary>
    /// Returns whether <paramref name="kind"/> is currently usable. The reason
    /// string is non-null when the agent is excluded — callers can surface it
    /// to operators (audit log, /concurrency, rejection messages).
    /// </summary>
    public AgentAvailability GetAvailability(AgentKind kind)
    {
        return GetAvailability(kind, AgentAvailabilityReadMode.AllExclusions);
    }

    public AgentAvailability GetAvailability(AgentKind kind, AgentAvailabilityReadMode mode)
    {
        if (!_entries.TryGetValue(kind, out var entry))
            return new AgentAvailability(true, null, null);

        lock (entry.Sync)
        {
            var reason = mode switch
            {
                AgentAvailabilityReadMode.AllExclusions => entry.CombinedReason(),
                AgentAvailabilityReadMode.IgnoreSmokeGateExclusions => entry.CombinedReason(IsNonSmokeExclusion),
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown availability read mode."),
            };
            return new AgentAvailability(reason is null, reason, entry.LastSmokePassedAt);
        }
    }

    /// <summary>
    /// Feeds a smoke-probe outcome from a specific <paramref name="source"/>
    /// (host credential check vs. in-sandbox CLI check). Passing clears that
    /// source's exclusion; failing excludes the agent under that source until a
    /// later probe from the <em>same</em> source passes or <see cref="Reset"/>
    /// is called.
    ///
    /// <para>Exclusions are tracked per source so the over-permissive host
    /// credential probe can never clear an in-VM exclusion (exit 127 / auth
    /// path drift) it cannot itself observe — the agent stays benched until the
    /// in-VM probe that benched it passes again.</para>
    ///
    /// <para>The fast-fail circuit breaker (earned from real sub-threshold
    /// dispatch failures) is only lifted when <paramref name="clearsFastFail"/>
    /// is set — i.e. by a <em>freshly executed</em> in-VM probe that actually
    /// ran the binary in a sandbox. A host credential check (proves nothing
    /// about whether the binary launches) and a <em>cached</em> in-VM verdict
    /// (no CLI was re-executed) must pass <c>false</c>, so a stale or
    /// over-permissive pass can never un-bench a circuit-broken agent without a
    /// fresh run.</para>
    /// </summary>
    public AvailabilityTransition MarkSmokeResult(
        AgentKind kind,
        AgentSmokeResult result,
        SmokeExclusionSource source = SmokeExclusionSource.HostSmoke,
        bool clearsFastFail = false)
    {
        var entry = _entries.GetOrAdd(kind, _ => new AgentAvailabilityEntry());
        var now = _time.GetUtcNow();

        lock (entry.Sync)
        {
            var wasExcluded = entry.IsExcluded;
            if (result.Ok)
            {
                entry.LastSmokePassedAt = now;
                entry.Exclusions.Remove(source);
                if (clearsFastFail)
                {
                    entry.ConsecutiveFastFails = 0;
                    entry.Exclusions.Remove(SmokeExclusionSource.FastFail);
                }
                var stillExcluded = entry.IsExcluded;
                if (wasExcluded && !stillExcluded)
                    _log.LogInformation(
                        "Agent {Agent} smoke transitioned FAIL -> PASS at {At} (source {Source})",
                        kind.Value, now, source);
                return new AvailabilityTransition(
                    PreviouslyExcluded: wasExcluded,
                    NowExcluded: stillExcluded,
                    Reason: entry.CombinedReason());
            }

            entry.LastSmokeFailedAt = now;
            // Encode category in the reason string so downstream consumers
            // (router log, /concurrency, audit log) can distinguish persistent
            // (operator must re-authorize) from transient (will recover on
            // retry) without threading a second field through every layer.
            // Persistent is the load-bearing case: a silent "transient: try
            // later" loop is what benched gemini for hours despite 100% quota.
            var categoryTag = result.Category switch
            {
                SmokeFailureCategory.Persistent => "persistent",
                SmokeFailureCategory.Transient => "transient",
                SmokeFailureCategory.Unknown => "unknown",
                _ => "unknown",
            };
            entry.Exclusions[source] = $"smoke probe failed [{categoryTag}]: {result.FailureReason ?? "unknown"}";
            if (!wasExcluded)
            {
                // Persistent smoke failures are the operator-actionable case:
                // re-authorization (or fixing PATH / installing the binary)
                // unblocks dispatch. Log louder so the bench is not lost in
                // routine noise.
                if (result.Category == SmokeFailureCategory.Persistent)
                {
                    _log.LogError(
                        "Agent {Agent} smoke PERSISTENTLY FAILED at {At} (source {Source}) — operator action required: {Reason}",
                        kind.Value, now, source, entry.Exclusions[source]);
                }
                else
                {
                    _log.LogWarning(
                        "Agent {Agent} smoke transitioned PASS -> FAIL at {At} (source {Source}): {Reason}",
                        kind.Value, now, source, entry.Exclusions[source]);
                }
            }
            return new AvailabilityTransition(
                PreviouslyExcluded: wasExcluded,
                NowExcluded: true,
                Reason: entry.CombinedReason());
        }
    }

    /// <summary>
    /// Feeds a real agent-run outcome. A non-zero exit completed in less than
    /// <see cref="AvailabilityOptions.FastFailThresholdSeconds"/> increments
    /// the fast-fail counter; a successful run or a slow failure (e.g. real
    /// quota timeout) resets it. After
    /// <see cref="AvailabilityOptions.MaxConsecutiveFastFails"/> the agent is
    /// excluded until a successful smoke probe or operator reset.
    /// </summary>
    public AvailabilityTransition RecordRunOutcome(
        AgentKind kind,
        bool success,
        TimeSpan duration)
    {
        var entry = _entries.GetOrAdd(kind, _ => new AgentAvailabilityEntry());
        var now = _time.GetUtcNow();
        var fastFailThreshold = TimeSpan.FromSeconds(_opts.FastFailThresholdSeconds);

        lock (entry.Sync)
        {
            var wasExcluded = entry.IsExcluded;
            if (success || duration >= fastFailThreshold)
            {
                entry.ConsecutiveFastFails = 0;
                return new AvailabilityTransition(wasExcluded, wasExcluded, entry.CombinedReason());
            }

            entry.ConsecutiveFastFails++;
            entry.LastFastFailAt = now;
            entry.LastFastFailDuration = duration;

            if (entry.ConsecutiveFastFails >= _opts.MaxConsecutiveFastFails
                && !entry.Exclusions.ContainsKey(SmokeExclusionSource.FastFail))
            {
                entry.Exclusions[SmokeExclusionSource.FastFail] =
                    $"fast-fail circuit breaker: {entry.ConsecutiveFastFails} consecutive sub-{_opts.FastFailThresholdSeconds}s non-zero exits";
                _log.LogWarning(
                    "Agent {Agent} excluded by fast-fail circuit breaker after {Count} consecutive sub-{Threshold}s failures",
                    kind.Value, entry.ConsecutiveFastFails, _opts.FastFailThresholdSeconds);
                return new AvailabilityTransition(wasExcluded, true, entry.CombinedReason());
            }

            return new AvailabilityTransition(wasExcluded, wasExcluded, entry.CombinedReason());
        }
    }

    /// <summary>
    /// Benches <paramref name="kind"/> because it is named in an
    /// <c>AgentClass</c> but has no registered in-VM smoke probe, so its
    /// in-sandbox CLI can never be verified. Called once at startup by the
    /// coverage validator. The exclusion is tracked under its own
    /// <see cref="SmokeExclusionSource.MissingProbe"/> source so neither a
    /// host- nor an in-VM-smoke pass can clear it (there is no probe to ever
    /// pass) — only an operator <see cref="Reset"/> after a probe is registered
    /// lifts it. Returns the transition so the caller can fire a webhook.
    /// </summary>
    public AvailabilityTransition ExcludeForMissingProbe(AgentKind kind, string reason)
    {
        var entry = _entries.GetOrAdd(kind, _ => new AgentAvailabilityEntry());
        lock (entry.Sync)
        {
            var wasExcluded = entry.IsExcluded;
            entry.Exclusions[SmokeExclusionSource.MissingProbe] = reason;
            if (!wasExcluded)
                _log.LogWarning("Agent {Agent} benched: {Reason}", kind.Value, reason);
            return new AvailabilityTransition(wasExcluded, true, entry.CombinedReason());
        }
    }

    /// <summary>
    /// Clears the exclusion state, fast-fail counter, and prior probe
    /// timestamps for <paramref name="kind"/>. Called by the
    /// <c>/admin/agent/{name}/reset</c> endpoint after the operator has
    /// corrected the underlying issue (e.g. installed the missing binary).
    /// A subsequent probe / run repopulates the timestamps from the new
    /// observation, so the snapshot accurately reflects post-reset state
    /// rather than mixing stale and fresh evidence.
    /// </summary>
    public void Reset(AgentKind kind)
    {
        if (!_entries.TryGetValue(kind, out var entry)) return;
        lock (entry.Sync)
        {
            entry.ConsecutiveFastFails = 0;
            entry.Exclusions.Clear();
            entry.LastFastFailAt = null;
            entry.LastFastFailDuration = null;
            entry.LastSmokePassedAt = null;
            entry.LastSmokeFailedAt = null;
        }
        _log.LogInformation("Agent {Agent} availability reset by operator", kind.Value);
    }

    /// <summary>
    /// Snapshot of every tracked agent's current state. Used by
    /// <c>/admin/agents/availability</c> and <c>/concurrency</c>.
    /// </summary>
    public IReadOnlyList<AgentAvailabilitySnapshot> Snapshot()
    {
        var results = new List<AgentAvailabilitySnapshot>();
        foreach (var kvp in _entries)
        {
            var entry = kvp.Value;
            lock (entry.Sync)
            {
                var reason = entry.CombinedReason();
                results.Add(new AgentAvailabilitySnapshot(
                    Agent: kvp.Key,
                    Excluded: reason is not null,
                    Reason: reason,
                    ConsecutiveFastFails: entry.ConsecutiveFastFails,
                    LastSmokePassedAt: entry.LastSmokePassedAt,
                    LastSmokeFailedAt: entry.LastSmokeFailedAt,
                    LastFastFailAt: entry.LastFastFailAt));
            }
        }
        return results;
    }

    private sealed class AgentAvailabilityEntry
    {
        public readonly object Sync = new();
        public int ConsecutiveFastFails;
        public DateTimeOffset? LastSmokePassedAt;
        public DateTimeOffset? LastSmokeFailedAt;
        public DateTimeOffset? LastFastFailAt;
        public TimeSpan? LastFastFailDuration;

        /// <summary>
        /// Active exclusions keyed by the signal that raised them. The agent is
        /// excluded while any entry is present; each source clears only its own
        /// entry, so a host-smoke pass cannot lift an in-VM-smoke exclusion.
        /// </summary>
        public readonly Dictionary<SmokeExclusionSource, string> Exclusions = new();

        public bool IsExcluded => Exclusions.Count > 0;

        /// <summary>Null when available; the joined reasons across sources otherwise.</summary>
        public string? CombinedReason() => CombinedReason(static _ => true);

        public string? CombinedReason(Func<SmokeExclusionSource, bool> include)
        {
            if (Exclusions.Count == 0)
                return null;

            List<string>? reasons = null;
            foreach (var exclusion in Exclusions)
            {
                if (!include(exclusion.Key))
                    continue;

                reasons ??= [];
                reasons.Add(exclusion.Value);
            }

            return reasons is { Count: > 0 } ? string.Join("; ", reasons) : null;
        }
    }

    private static bool IsNonSmokeExclusion(SmokeExclusionSource source) =>
        source is not SmokeExclusionSource.HostSmoke
            and not SmokeExclusionSource.InVmSmoke
            and not SmokeExclusionSource.MissingProbe;
}

/// <summary>
/// The signal that benched an agent. Tracked separately so a pass from one
/// signal never clears another's exclusion — the over-permissive host
/// credential probe must not be able to un-bench an agent that the in-sandbox
/// CLI probe (or the fast-fail breaker) marked broken.
/// </summary>
public enum SmokeExclusionSource
{
    /// <summary>Host-side credential probe (<see cref="CredentialSmokeGate"/> / periodic sweeps).</summary>
    HostSmoke,

    /// <summary>In-sandbox CLI probe (<see cref="InVmSmokeProber"/>).</summary>
    InVmSmoke,

    /// <summary>Fast-fail circuit breaker over real run outcomes.</summary>
    FastFail,

    /// <summary>
    /// Agent named in an <c>AgentClass</c> but with no registered in-VM smoke
    /// probe, so its sandbox CLI cannot be verified. Set once at startup by the
    /// coverage validator; cleared only by operator <see cref="AgentAvailabilityRegistry.Reset"/>.
    /// </summary>
    MissingProbe,
}

/// <summary>
/// Smoke-subsystem port over <see cref="AgentAvailabilityRegistry"/>. Carries
/// the exclusion-taxonomy mutators that the in-VM prober
/// (<see cref="InVmSmokeProber"/>), the coverage policy
/// (<see cref="InVmSmokeCoveragePolicy"/>), and the host smoke services
/// (<see cref="StartupSmokeProbeService"/> / <see cref="PeriodicSmokeProbeService"/>)
/// need to feed probe verdicts back into availability.
///
/// <para>Deliberately separate from <see cref="IAgentAvailabilityRegistry"/>:
/// routing/dispatch/admin consumers depend on that narrow read/run-outcome port
/// and must not see <see cref="MarkSmokeResult"/> (source + clearsFastFail) or
/// <see cref="ExcludeForMissingProbe"/>, while the smoke services that own the
/// exclusion model depend on this one — so neither side is pinned to the
/// concrete registry type (interface segregation; loose coupling).</para>
/// </summary>
public interface ISmokeAvailabilityRegistry
{
    /// <summary>Current routable verdict for an agent (shared with the read port).</summary>
    AgentAvailability GetAvailability(AgentKind kind);

    /// <summary>Feeds a smoke-probe outcome from a specific source into availability.</summary>
    AvailabilityTransition MarkSmokeResult(
        AgentKind kind,
        AgentSmokeResult result,
        SmokeExclusionSource source = SmokeExclusionSource.HostSmoke,
        bool clearsFastFail = false);

    /// <summary>Benches an agent named in a class but with no registered in-VM probe.</summary>
    AvailabilityTransition ExcludeForMissingProbe(AgentKind kind, string reason);

    /// <summary>
    /// Clears <paramref name="kind"/>'s exclusion state, fast-fail counter, and
    /// prior probe timestamps. Lives on the smoke port (which owns the exclusion
    /// taxonomy) rather than the narrow routing port, so the operator-reset
    /// adapter (<see cref="AgentAvailabilityReset"/>) can pair it with the in-VM
    /// cache invalidation through an abstraction instead of binding to the
    /// concrete registry. Deliberately absent from
    /// <see cref="IAgentAvailabilityRegistry"/> so routing/dispatch consumers
    /// cannot clear the registry without also dropping the cache.
    /// </summary>
    void Reset(AgentKind kind);
}

/// <summary>
/// Tuning for <see cref="AgentAvailabilityRegistry"/> and
/// <see cref="PeriodicSmokeProbeService"/>. Bound from
/// <c>CodeyBox:Smoke:Availability</c>.
/// </summary>
public sealed record AvailabilityOptions
{
    /// <summary>
    /// Runs that exit non-zero in less than this many seconds count as a
    /// fast-fail (the binary failed before it could meaningfully start).
    /// Default 10.
    /// </summary>
    public int FastFailThresholdSeconds { get; init; } = 10;

    /// <summary>
    /// Number of consecutive fast-fails before an agent is excluded by the
    /// circuit breaker. Default 3.
    /// </summary>
    public int MaxConsecutiveFastFails { get; init; } = 3;

    /// <summary>
    /// Interval between periodic background smoke probe sweeps. Default 5
    /// minutes. Set to <see cref="TimeSpan.Zero"/> (or any non-positive value)
    /// to disable the periodic sweep — startup probes still run.
    /// </summary>
    public TimeSpan PeriodicSweepInterval { get; init; } = TimeSpan.FromMinutes(5);
}
