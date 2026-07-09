using System.Collections.Concurrent;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Shared transition tracker for quota availability observations. It publishes
/// the existing wake-up signal only on unusable -> usable transitions; first
/// observations and usable -> unusable changes are just recorded.
/// </summary>
public sealed class AgentQuotaAvailabilityBroadcaster : IAgentQuotaAvailabilitySignal, IAgentQuotaAvailabilityPublisher, IAgentQuotaAvailabilityObservationSource
{
    private readonly ConcurrentDictionary<AgentQuotaMemberKey, bool> _lastUsable = new();
    private readonly ILogger<AgentQuotaAvailabilityBroadcaster> _log;

    public AgentQuotaAvailabilityBroadcaster(ILogger<AgentQuotaAvailabilityBroadcaster>? log = null)
        => _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentQuotaAvailabilityBroadcaster>.Instance;

    public event Action? QuotaUsableThresholdCrossed;
    public event Action<AgentQuotaMemberKey>? QuotaMemberUsableThresholdCrossed;
    public event Action<AgentQuotaUsabilityObservation>? QuotaUsabilityObserved;

    public bool RecordQuotaUsability(
        AgentMembership member,
        bool isUsable,
        bool publishRecoverySignal = true,
        DateTimeOffset? resetAt = null)
    {
        var key = AgentQuotaMemberKey.From(member);
        var observation = new AgentQuotaUsabilityObservation(member, isUsable, publishRecoverySignal, resetAt);
        if (!publishRecoverySignal)
            return NotifyQuotaUsabilityObserved(observation);

        var transition = RecordTransition(key, isUsable);
        if (!transition.CrossedToUsable)
        {
            var observed = NotifyQuotaUsabilityObserved(observation);
            if (!observed)
                RollBackTransition(key, transition);
            return observed;
        }

        _log.LogInformation(
            "Quota availability for {Agent}/{Model} crossed from unusable to usable; triggering parked-item recovery sweep",
            member.Agent.Value,
            member.ModelId ?? "(default)");
        var delivered = NotifyQuotaUsableThresholdCrossed(key);
        if (!delivered)
        {
            RollBackTransition(key, transition);
            return false;
        }

        var observationDelivered = NotifyQuotaUsabilityObserved(observation);
        if (!observationDelivered)
            RollBackTransition(key, transition);
        return observationDelivered;
    }

    private QuotaUsabilityTransitionRecord RecordTransition(AgentQuotaMemberKey key, bool isUsable)
    {
        while (true)
        {
            if (!_lastUsable.TryGetValue(key, out var previous))
            {
                if (_lastUsable.TryAdd(key, isUsable))
                    return new QuotaUsabilityTransitionRecord(null, isUsable, Changed: true, CrossedToUsable: false);
                continue;
            }

            if (previous == isUsable)
                return new QuotaUsabilityTransitionRecord(previous, isUsable, Changed: false, CrossedToUsable: false);

            if (_lastUsable.TryUpdate(key, isUsable, previous))
                return new QuotaUsabilityTransitionRecord(previous, isUsable, Changed: true, CrossedToUsable: !previous && isUsable);
        }
    }

    private bool NotifyQuotaUsabilityObserved(AgentQuotaUsabilityObservation observation)
        => NotifySubscribers(
            QuotaUsabilityObserved,
            handler => handler(observation),
            ex => _log.LogWarning(ex, "Quota usability-observation subscriber threw; continuing"));

    private bool NotifyQuotaUsableThresholdCrossed(AgentQuotaMemberKey key)
    {
        var memberDelivered = NotifySubscribers(
            QuotaMemberUsableThresholdCrossed,
            handler => handler(key),
            ex => _log.LogWarning(ex, "Quota member usable-threshold subscriber threw; continuing"));
        var legacyDelivered = NotifySubscribers(
            QuotaUsableThresholdCrossed,
            handler => handler(),
            ex => _log.LogWarning(ex, "Quota usable threshold subscriber threw; continuing"));
        return memberDelivered && legacyDelivered;
    }

    private void RollBackTransition(AgentQuotaMemberKey key, QuotaUsabilityTransitionRecord transition)
    {
        if (!transition.Changed)
            return;

        if (transition.Previous is { } previous)
        {
            _lastUsable.TryUpdate(key, previous, transition.Current);
        }
        else
        {
            _lastUsable.TryRemove(new KeyValuePair<AgentQuotaMemberKey, bool>(key, transition.Current));
        }
    }

    private bool NotifySubscribers<TDelegate>(
        TDelegate? handlers,
        Action<TDelegate> notify,
        Action<Exception> logFailure)
        where TDelegate : Delegate
    {
        if (handlers is null)
            return true;

        var delivered = true;
        foreach (TDelegate handler in handlers.GetInvocationList().Cast<TDelegate>())
        {
            try
            {
                notify(handler);
            }
            catch (Exception ex)
            {
                logFailure(ex);
                delivered = false;
            }
        }

        return delivered;
    }
}

public sealed record AgentQuotaUsabilityObservation(
    AgentMembership Member,
    bool IsUsable,
    bool PublishRecoverySignal,
    DateTimeOffset? ResetAt = null);

internal readonly record struct QuotaUsabilityTransitionRecord(
    bool? Previous,
    bool Current,
    bool Changed,
    bool CrossedToUsable);

/// <summary>
/// Publisher side of <see cref="IAgentQuotaAvailabilitySignal"/>.
/// </summary>
public interface IAgentQuotaAvailabilityPublisher
{
    /// <summary>
    /// Records a quota-usability observation for <paramref name="member"/>.
    /// Implementations publish only after a signal-producing observation has
    /// previously recorded the same member as unusable, so command-style
    /// dispatch/retry/fallback paths must record both the denied observation and
    /// the later allowed observation. Set <paramref name="publishRecoverySignal"/>
    /// to <c>false</c> for read-only probes such as readiness checks; those
    /// observations may be forwarded to subscribers for telemetry, but must not
    /// advance the signal-producing transition memory. Calls may arrive
    /// concurrently from independent router/probe paths and must be handled
    /// without relying on single-threaded ordering.
    /// </summary>
    /// <returns>
    /// <c>true</c> when every required recovery-signal and observation
    /// subscriber completed; <c>false</c> when any such subscriber threw and the
    /// caller should keep any best-effort recovery tracking.
    /// </returns>
    bool RecordQuotaUsability(
        AgentMembership member,
        bool isUsable,
        bool publishRecoverySignal = true,
        DateTimeOffset? resetAt = null);
}

public interface IAgentQuotaAvailabilityObservationSource
{
    event Action<AgentQuotaUsabilityObservation>? QuotaUsabilityObserved;
}
