using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Per-process availability tracker for each registered agent. Three signals
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
///     <b>Fast-fail circuit breaker</b> — genuine agent runs that exit non-zero in less than
///     <see cref="AvailabilityOptions.FastFailThresholdSeconds"/> count as
///     "smoke-style" failures after infrastructure-shaped failures have been
///     filtered by the pipeline. After
///     <see cref="AvailabilityOptions.MaxConsecutiveFastFails"/> consecutive
///     fast-fails the agent is excluded. A successful run (or a normal-length
///     failure) resets the counter.
///   </item>
///   <item>
///     <b>No-changes circuit breaker</b> — clean-exit runs that leave the
///     working tree unchanged ("Agent produced no changes to commit"). The
///     fast-fail breaker only counts non-zero exits, so a silently-broken
///     agent (auth collapse, capability collapse, or an unknown failure mode
///     that still exits 0) is never excluded by that path and keeps consuming
///     the backlog. After
///     <see cref="AvailabilityOptions.MaxConsecutiveNoChanges"/> CONSECUTIVE
///     DISTINCT work items produce no changes the agent is excluded. The same
///     work item retried does not advance the counter, so a single hard item
///     can't trip the breaker on its own. Recovery is operator-only via the
///     existing reset path; a real "produced changes" run between no-changes
///     outcomes clears the streak so an isolated no-change does not trip
///     either.
///   </item>
/// </list>
///
/// <para>
/// Distinct from <see cref="IQuotaFailureStore"/>: that store handles
/// rate-limit / quota-shaped failures (exit 1 with provider quota signals);
/// this registry handles smoke exclusions plus genuine fast agent crash loops.
/// Binary-not-found and runner materialisation failures are sandbox/provisioning
/// defects and are filtered before the fast-fail counter is touched.
/// </para>
///
/// <para>Thread-safe; updates use a small per-agent lock so concurrent
/// outcomes from many in-flight items don't corrupt counters.</para>
/// </summary>
public sealed class AgentAvailabilityRegistry : IAgentAvailabilityRegistry, ISmokeAvailabilityRegistry, IAgentAuthAvailabilityRegistry, IAgentAuthRequiredAvailabilityReader, IAgentAvailabilityRecoverySignal, IAgentRestoreSignal
{
    private readonly AvailabilityOptions _opts;
    private readonly TimeProvider _time;
    private readonly ILogger<AgentAvailabilityRegistry> _log;
    private readonly ConcurrentDictionary<AgentKind, AgentAvailabilityEntry> _entries = new();

    public event Action<AgentRestoredEvent>? AgentRestored;

    public AgentAvailabilityRegistry(
        AvailabilityOptions opts,
        TimeProvider? time = null,
        ILogger<AgentAvailabilityRegistry>? log = null)
    {
        _opts = opts;
        _time = time ?? TimeProvider.System;
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentAvailabilityRegistry>.Instance;
    }

    public event Action<AgentKind>? AgentRecovered;

    /// <summary>
    /// Returns whether <paramref name="kind"/> is currently usable. The reason
    /// string is non-null when the agent is excluded — callers can surface it
    /// to operators (audit log, /concurrency, rejection messages).
    /// </summary>
    public AgentAvailability GetAvailability(AgentKind kind)
    {
        return GetAvailability(kind, static _ => true);
    }

    public AgentAvailability GetAvailabilityWithoutSmokeGateExclusions(AgentKind kind)
    {
        return GetAvailability(kind, IsNonSmokeExclusion);
    }

    public AgentAuthRequiredAvailability GetAuthRequiredAvailability(AgentKind kind)
    {
        if (!_entries.TryGetValue(kind, out var entry))
            return new AgentAuthRequiredAvailability(false, null);

        lock (entry.Sync)
        {
            return entry.Exclusions.TryGetValue(SmokeExclusionSource.AuthRequired, out var reason)
                ? new AgentAuthRequiredAvailability(true, reason)
                : new AgentAuthRequiredAvailability(false, null);
        }
    }

    private AgentAvailability GetAvailability(AgentKind kind, Func<SmokeExclusionSource, bool> include)
    {
        if (!_entries.TryGetValue(kind, out var entry))
            return new AgentAvailability(true, null, null);

        lock (entry.Sync)
        {
            var reason = entry.CombinedReason(include);
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
        AvailabilityTransition transition;
        AgentRestoredEvent? restored = null;
        var recovered = false;
        lock (entry.Sync)
        {
            var wasExcluded = entry.IsExcluded;
            var hadSourceExclusion = entry.Exclusions.ContainsKey(source);
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
                {
                    _log.LogInformation(
                        "Agent {Agent} smoke transitioned FAIL -> PASS at {At} (source {Source})",
                        kind.Value, now, source);
                    var outageStart = entry.FirstExcludedAt;
                    entry.FirstExcludedAt = null;
                    restored = new AgentRestoredEvent(kind, outageStart, now);
                }
                recovered = wasExcluded && !stillExcluded;
                transition = new AvailabilityTransition(
                    PreviouslyExcluded: wasExcluded,
                    NowExcluded: stillExcluded,
                    Reason: entry.CombinedReason(),
                    SourceChanged: hadSourceExclusion);
            }
            else
            {
                transition = MarkSmokeFailureLocked(entry, kind, now, source, result, wasExcluded, hadSourceExclusion);
            }
        }

        if (restored is not null)
            FireRestoredEvent(restored);
        if (recovered)
            NotifyAgentRecovered(kind);
        return transition;
    }

    private AvailabilityTransition MarkSmokeFailureLocked(
        AgentAvailabilityEntry entry,
        AgentKind kind,
        DateTimeOffset now,
        SmokeExclusionSource source,
        AgentSmokeResult result,
        bool wasExcluded,
        bool hadSourceExclusion)
    {
        entry.LastSmokeFailedAt = now;
        entry.FirstExcludedAt ??= now;
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
            Reason: entry.CombinedReason(),
            SourceChanged: !hadSourceExclusion);
    }

    private void FireRestoredEvent(AgentRestoredEvent payload)
    {
        var handlers = AgentRestored;
        if (handlers is null) return;
        foreach (var d in handlers.GetInvocationList())
        {
            try
            {
                ((Action<AgentRestoredEvent>)d)(payload);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Agent restore subscriber threw for {Agent}; availability state remains committed", payload.Agent.Value);
            }
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
                entry.FirstExcludedAt ??= now;
                _log.LogWarning(
                    "Agent {Agent} excluded by fast-fail circuit breaker after {Count} consecutive sub-{Threshold}s failures",
                    kind.Value, entry.ConsecutiveFastFails, _opts.FastFailThresholdSeconds);
                return new AvailabilityTransition(wasExcluded, true, entry.CombinedReason(), SourceChanged: true);
            }

            return new AvailabilityTransition(wasExcluded, wasExcluded, entry.CombinedReason());
        }
    }

    /// <summary>
    /// Feeds a "clean exit, working tree unchanged" outcome into the no-changes
    /// circuit breaker. Increments only when <paramref name="itemId"/> is
    /// distinct from items already counted in the current streak, so a single
    /// hard item retried multiple times can't trip the breaker on its own.
    /// Excludes the agent under <see cref="SmokeExclusionSource.NoChangesBreaker"/>
    /// once the count reaches <see cref="AvailabilityOptions.MaxConsecutiveNoChanges"/>;
    /// the exclusion is cleared only by operator <see cref="Reset"/>.
    /// </summary>
    public AvailabilityTransition RecordNoChangesOutcome(AgentKind kind, WorkItemId itemId)
    {
        var entry = _entries.GetOrAdd(kind, _ => new AgentAvailabilityEntry());
        var now = _time.GetUtcNow();

        lock (entry.Sync)
        {
            var wasExcluded = entry.IsExcluded;
            // Disabled when MaxConsecutiveNoChanges <= 0: the operator opted out
            // of the breaker but still wants the rest of the registry behavior.
            if (_opts.MaxConsecutiveNoChanges <= 0)
                return new AvailabilityTransition(wasExcluded, wasExcluded, entry.CombinedReason());

            // Dedup: a retry of the same item must not advance the streak —
            // otherwise one legitimately-empty task could trip the breaker.
            if (!entry.NoChangesItems.Add(itemId))
                return new AvailabilityTransition(wasExcluded, wasExcluded, entry.CombinedReason());

            entry.ConsecutiveNoChanges = entry.NoChangesItems.Count;
            entry.LastNoChangesAt = now;

            if (entry.ConsecutiveNoChanges >= _opts.MaxConsecutiveNoChanges
                && !entry.Exclusions.ContainsKey(SmokeExclusionSource.NoChangesBreaker))
            {
                entry.Exclusions[SmokeExclusionSource.NoChangesBreaker] =
                    $"no-changes circuit breaker: {entry.ConsecutiveNoChanges} consecutive distinct work items produced no changes (silent-failure signature)";
                entry.FirstExcludedAt ??= now;
                _log.LogWarning(
                    "Agent {Agent} excluded by no-changes circuit breaker after {Count} consecutive distinct work items produced no changes — operator action required (reset via /admin/agent/{Agent}/reset after diagnosing)",
                    kind.Value, entry.ConsecutiveNoChanges, kind.Value);
                return new AvailabilityTransition(
                    wasExcluded,
                    true,
                    entry.CombinedReason(),
                    SourceChanged: true);
            }

            return new AvailabilityTransition(wasExcluded, wasExcluded, entry.CombinedReason());
        }
    }

    /// <summary>
    /// Resets the no-changes streak for <paramref name="kind"/> — called when
    /// the agent successfully produces real changes on a work item. Does NOT
    /// lift an existing no-changes exclusion (the breaker recovers only via
    /// <see cref="Reset"/>); in practice an excluded agent is never dispatched
    /// and so never reaches this signal anyway.
    /// </summary>
    public void RecordChangesProduced(AgentKind kind)
    {
        if (!_entries.TryGetValue(kind, out var entry)) return;
        lock (entry.Sync)
        {
            if (entry.NoChangesItems.Count == 0) return;
            entry.NoChangesItems.Clear();
            entry.ConsecutiveNoChanges = 0;
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
        var now = _time.GetUtcNow();
        lock (entry.Sync)
        {
            var wasExcluded = entry.IsExcluded;
            var hadSourceExclusion = entry.Exclusions.ContainsKey(SmokeExclusionSource.MissingProbe);
            entry.Exclusions[SmokeExclusionSource.MissingProbe] = reason;
            entry.FirstExcludedAt ??= now;
            if (!wasExcluded)
                _log.LogWarning("Agent {Agent} benched: {Reason}", kind.Value, reason);
            return new AvailabilityTransition(wasExcluded, true, entry.CombinedReason(), SourceChanged: !hadSourceExclusion);
        }
    }

    /// <summary>
    /// Benches <paramref name="kind"/> because a real runtime invocation was
    /// authoritatively classified as blocked on interactive authentication.
    /// This is not a smoke probe result: it is stored under its own non-smoke
    /// source so dispatch still honors it when the operator disables smoke
    /// gating.
    /// </summary>
    public AvailabilityTransition MarkAuthRequired(AgentKind kind, string reason)
    {
        var entry = _entries.GetOrAdd(kind, _ => new AgentAvailabilityEntry());
        var now = _time.GetUtcNow();

        lock (entry.Sync)
        {
            var wasExcluded = entry.IsExcluded;
            var hadSourceExclusion = entry.Exclusions.ContainsKey(SmokeExclusionSource.AuthRequired);
            entry.Exclusions[SmokeExclusionSource.AuthRequired] = $"auth required: {reason}";
            entry.FirstExcludedAt ??= now;
            if (!wasExcluded)
            {
                _log.LogError(
                    "Agent {Agent} requires interactive authentication at {At}; operator action required: {Reason}",
                    kind.Value, now, reason);
            }

            return new AvailabilityTransition(
                PreviouslyExcluded: wasExcluded,
                NowExcluded: true,
                Reason: entry.CombinedReason(),
                SourceChanged: !hadSourceExclusion);
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
        var recovered = false;
        AgentRestoredEvent? restored = null;
        var now = _time.GetUtcNow();
        lock (entry.Sync)
        {
            var wasExcluded = entry.IsExcluded;
            var outageStart = entry.FirstExcludedAt;
            entry.ConsecutiveFastFails = 0;
            entry.Exclusions.Clear();
            entry.LastFastFailAt = null;
            entry.LastFastFailDuration = null;
            entry.LastSmokePassedAt = null;
            entry.LastSmokeFailedAt = null;
            entry.NoChangesItems.Clear();
            entry.ConsecutiveNoChanges = 0;
            entry.LastNoChangesAt = null;
            recovered = wasExcluded;
            entry.FirstExcludedAt = null;
            if (wasExcluded)
                restored = new AgentRestoredEvent(kind, outageStart, now);
        }
        _log.LogInformation("Agent {Agent} availability reset by operator", kind.Value);
        if (restored is not null)
            FireRestoredEvent(restored);
        if (recovered)
            NotifyAgentRecovered(kind);
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
                    LastFastFailAt: entry.LastFastFailAt,
                    ConsecutiveNoChanges: entry.ConsecutiveNoChanges,
                    LastNoChangesAt: entry.LastNoChangesAt));
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
        /// Distinct work-item IDs in the current no-changes streak. The
        /// no-changes breaker counts distinct items so a single hard task
        /// retried in place can't trip it. Cleared by a real "produced changes"
        /// run or by operator reset.
        /// </summary>
        public readonly HashSet<WorkItemId> NoChangesItems = new();

        public int ConsecutiveNoChanges;
        public DateTimeOffset? LastNoChangesAt;

        /// <summary>
        /// Timestamp at which the agent transitioned from healthy → excluded
        /// for the CURRENT outage streak. Set on the first exclusion under any
        /// source (smoke, fast-fail, no-changes, missing-probe, auth-required)
        /// and cleared the moment the agent returns to fully routable
        /// (last exclusion removed). Pinned across follow-up failures so a
        /// long outage with repeating smoke probes keeps the FIRST failure's
        /// timestamp — not the last — as the outage anchor. The
        /// <see cref="AgentRestoredEvent.OutageStartedAt"/> consumer scopes its
        /// sweep against this value.
        /// </summary>
        public DateTimeOffset? FirstExcludedAt;

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

    private void NotifyAgentRecovered(AgentKind kind)
    {
        var handlers = AgentRecovered;
        if (handlers is null)
            return;

        foreach (Action<AgentKind> handler in handlers.GetInvocationList().Cast<Action<AgentKind>>())
        {
            try
            {
                handler(kind);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Agent availability recovery subscriber threw for {Agent}; continuing", kind.Value);
            }
        }
    }
}

/// <summary>
/// Emits when the aggregate agent availability registry crosses from excluded
/// to available. Consumers use it to wake recovery work without depending on
/// the registry's exclusion taxonomy.
/// </summary>
public interface IAgentAvailabilityRecoverySignal
{
    event Action<AgentKind>? AgentRecovered;
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

    /// <summary>
    /// No-changes circuit breaker over real run outcomes. Catches the
    /// silent-failure signature where an agent exits 0 but leaves the working
    /// tree unchanged; the fast-fail breaker only counts non-zero exits and
    /// so misses this pattern (auth collapse, capability collapse, or a
    /// failure mode whose signature isn't recognised yet). Cleared only by
    /// operator <see cref="AgentAvailabilityRegistry.Reset"/>.
    /// </summary>
    NoChangesBreaker,

    /// <summary>
    /// Runtime auth/login prompt detected from real agent output.
    /// Tracked outside the smoke gate so a deployment with
    /// <c>CodeyBox:Smoke:Enabled=false</c> still benches an unauthenticated
    /// agent when the non-model-controlled stream proves the CLI printed an
    /// OAuth login URL and exited 0.
    /// </summary>
    AuthRequired,
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
public interface ISmokeAvailabilityRegistry : IAgentEffectiveAvailabilityReader
{
    /// <summary>Current routable verdict for an agent (shared with the read port).</summary>
    new AgentAvailability GetAvailability(AgentKind kind);

    /// <summary>
    /// Current verdict with smoke-gate exclusions ignored. Used by the dispatch
    /// smoke policy when the master smoke switch is disabled; non-smoke
    /// exclusions such as the fast-fail circuit breaker still apply.
    /// </summary>
    new AgentAvailability GetAvailabilityWithoutSmokeGateExclusions(AgentKind kind);

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
/// Source-neutral mutator for runtime auth failures. Pipeline code depends on
/// this instead of the smoke registry because the signal is not a probe verdict
/// and should not manufacture <see cref="AgentSmokeResult"/> values.
/// </summary>
public interface IAgentAuthAvailabilityRegistry
{
    /// <summary>
    /// Excludes <paramref name="kind"/> until an operator reset because a
    /// runtime invocation was classified as needing interactive auth.
    /// </summary>
    AvailabilityTransition MarkAuthRequired(AgentKind kind, string reason);
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
    /// fast-fail after the pipeline filters infrastructure-shaped failures.
    /// Default 10.
    /// </summary>
    public int FastFailThresholdSeconds { get; init; } = 10;

    /// <summary>
    /// Number of consecutive fast-fails before an agent is excluded by the
    /// circuit breaker. Default 3.
    /// </summary>
    public int MaxConsecutiveFastFails { get; init; } = 3;

    /// <summary>
    /// Number of consecutive DISTINCT work items that produce no changes
    /// before the no-changes circuit breaker excludes the agent. Default 3.
    /// Catches the silent-failure signature (clean exit, empty diff) the
    /// fast-fail breaker doesn't see because it only counts non-zero exits.
    /// </summary>
    public int MaxConsecutiveNoChanges { get; init; } = 3;

    /// <summary>
    /// Interval between periodic background smoke probe sweeps. Default 5
    /// minutes. Set to <see cref="TimeSpan.Zero"/> (or any non-positive value)
    /// to disable the periodic sweep — startup probes still run.
    /// </summary>
    public TimeSpan PeriodicSweepInterval { get; init; } = TimeSpan.FromMinutes(5);
}
