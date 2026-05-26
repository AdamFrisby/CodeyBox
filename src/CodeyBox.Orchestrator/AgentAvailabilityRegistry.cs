using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Per-process availability tracker for each registered agent. Two signals
/// feed it:
/// <list type="number">
///   <item>
///     <b>Credential smoke probe</b> results — fed in by
///     <see cref="StartupSmokeProbeService"/> and the periodic
///     <see cref="PeriodicSmokeProbeService"/>. A failed probe excludes the
///     agent until a subsequent probe passes or an operator resets it.
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
public sealed class AgentAvailabilityRegistry
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
        if (!_entries.TryGetValue(kind, out var entry))
            return new AgentAvailability(true, null, null);

        lock (entry.Sync)
        {
            if (entry.ExcludedReason is null)
                return new AgentAvailability(true, null, entry.LastSmokePassedAt);
            return new AgentAvailability(false, entry.ExcludedReason, entry.LastSmokePassedAt);
        }
    }

    /// <summary>
    /// Feeds a smoke-probe outcome. Passing transitions the agent to available
    /// and resets the fast-fail counter; failing excludes the agent until a
    /// later probe passes or <see cref="Reset"/> is called.
    /// </summary>
    public AvailabilityTransition MarkSmokeResult(AgentKind kind, AgentSmokeResult result)
    {
        var entry = _entries.GetOrAdd(kind, _ => new AgentAvailabilityEntry());
        var now = _time.GetUtcNow();

        lock (entry.Sync)
        {
            var wasExcluded = entry.ExcludedReason is not null;
            if (result.Ok)
            {
                entry.LastSmokePassedAt = now;
                entry.ConsecutiveFastFails = 0;
                entry.ExcludedReason = null;
                if (wasExcluded)
                    _log.LogInformation(
                        "Agent {Agent} smoke transitioned FAIL -> PASS at {At}",
                        kind.Value, now);
                return new AvailabilityTransition(
                    PreviouslyExcluded: wasExcluded,
                    NowExcluded: false,
                    Reason: null);
            }

            entry.LastSmokeFailedAt = now;
            entry.ExcludedReason = $"smoke probe failed: {result.FailureReason ?? "unknown"}";
            if (!wasExcluded)
                _log.LogWarning(
                    "Agent {Agent} smoke transitioned PASS -> FAIL at {At}: {Reason}",
                    kind.Value, now, entry.ExcludedReason);
            return new AvailabilityTransition(
                PreviouslyExcluded: wasExcluded,
                NowExcluded: true,
                Reason: entry.ExcludedReason);
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
            var wasExcluded = entry.ExcludedReason is not null;
            if (success || duration >= fastFailThreshold)
            {
                entry.ConsecutiveFastFails = 0;
                return new AvailabilityTransition(wasExcluded, wasExcluded, entry.ExcludedReason);
            }

            entry.ConsecutiveFastFails++;
            entry.LastFastFailAt = now;
            entry.LastFastFailDuration = duration;

            if (entry.ConsecutiveFastFails >= _opts.MaxConsecutiveFastFails && entry.ExcludedReason is null)
            {
                entry.ExcludedReason =
                    $"fast-fail circuit breaker: {entry.ConsecutiveFastFails} consecutive sub-{_opts.FastFailThresholdSeconds}s non-zero exits";
                _log.LogWarning(
                    "Agent {Agent} excluded by fast-fail circuit breaker after {Count} consecutive sub-{Threshold}s failures",
                    kind.Value, entry.ConsecutiveFastFails, _opts.FastFailThresholdSeconds);
                return new AvailabilityTransition(wasExcluded, true, entry.ExcludedReason);
            }

            return new AvailabilityTransition(wasExcluded, wasExcluded, entry.ExcludedReason);
        }
    }

    /// <summary>
    /// Clears the exclusion state and fast-fail counter for <paramref name="kind"/>.
    /// Called by the <c>/admin/agent/{name}/reset</c> endpoint after the operator
    /// has corrected the underlying issue (e.g. installed the missing binary).
    /// </summary>
    public void Reset(AgentKind kind)
    {
        if (!_entries.TryGetValue(kind, out var entry)) return;
        lock (entry.Sync)
        {
            entry.ConsecutiveFastFails = 0;
            entry.ExcludedReason = null;
            entry.LastFastFailAt = null;
            entry.LastFastFailDuration = null;
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
                results.Add(new AgentAvailabilitySnapshot(
                    Agent: kvp.Key,
                    Excluded: entry.ExcludedReason is not null,
                    Reason: entry.ExcludedReason,
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
        public string? ExcludedReason;
    }
}

/// <summary>Result of <see cref="AgentAvailabilityRegistry.GetAvailability"/>.</summary>
public sealed record AgentAvailability(bool Available, string? Reason, DateTimeOffset? LastSmokePassedAt);

/// <summary>
/// State transition returned by registry mutators. Callers use
/// <c>!PreviouslyExcluded &amp;&amp; NowExcluded</c> to fire "agent newly
/// excluded" webhook events and <c>PreviouslyExcluded &amp;&amp; !NowExcluded</c>
/// to fire "agent recovered" events without duplicates on steady state.
/// </summary>
public sealed record AvailabilityTransition(bool PreviouslyExcluded, bool NowExcluded, string? Reason);

/// <summary>Per-agent state surfaced via the admin / concurrency endpoints.</summary>
public sealed record AgentAvailabilitySnapshot(
    AgentKind Agent,
    bool Excluded,
    string? Reason,
    int ConsecutiveFastFails,
    DateTimeOffset? LastSmokePassedAt,
    DateTimeOffset? LastSmokeFailedAt,
    DateTimeOffset? LastFastFailAt);

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
