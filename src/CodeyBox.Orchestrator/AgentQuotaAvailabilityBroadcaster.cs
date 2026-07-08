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
    public event Action<AgentQuotaUsabilityObservation>? QuotaUsabilityObserved;

    public bool RecordQuotaUsability(
        AgentMembership member,
        bool isUsable,
        bool publishRecoverySignal = true)
    {
        var key = AgentQuotaMemberKey.From(member);
        if (!publishRecoverySignal)
        {
            NotifyQuotaUsabilityObserved(new AgentQuotaUsabilityObservation(member, isUsable, publishRecoverySignal));
            return true;
        }

        var crossedToUsable = RecordTransition(key, isUsable);
        if (!crossedToUsable)
        {
            NotifyQuotaUsabilityObserved(new AgentQuotaUsabilityObservation(member, isUsable, publishRecoverySignal));
            return true;
        }

        _log.LogInformation(
            "Quota availability for {Agent}/{Model} crossed from unusable to usable; triggering parked-item recovery sweep",
            member.Agent.Value,
            member.ModelId ?? "(default)");
        var delivered = NotifyQuotaUsableThresholdCrossed();
        if (!delivered)
        {
            RollBackUsableTransition(key);
            return false;
        }

        NotifyQuotaUsabilityObserved(new AgentQuotaUsabilityObservation(member, isUsable, publishRecoverySignal));
        return true;
    }

    private bool RecordTransition(AgentQuotaMemberKey key, bool isUsable)
    {
        while (true)
        {
            if (!_lastUsable.TryGetValue(key, out var previous))
            {
                if (_lastUsable.TryAdd(key, isUsable))
                    return false;
                continue;
            }

            if (previous == isUsable)
                return false;

            if (_lastUsable.TryUpdate(key, isUsable, previous))
                return !previous && isUsable;
        }
    }

    private bool NotifyQuotaUsabilityObserved(AgentQuotaUsabilityObservation observation)
        => NotifySubscribers(
            QuotaUsabilityObserved,
            handler => handler(observation),
            ex => _log.LogWarning(ex, "Quota usability-observation subscriber threw; continuing"));

    private bool NotifyQuotaUsableThresholdCrossed()
        => NotifySubscribers(
            QuotaUsableThresholdCrossed,
            handler => handler(),
            ex => _log.LogWarning(ex, "Quota usable threshold subscriber threw; continuing"));

    private void RollBackUsableTransition(AgentQuotaMemberKey key)
    {
        while (_lastUsable.TryGetValue(key, out var current) && current)
        {
            if (_lastUsable.TryUpdate(key, false, current))
                return;
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
    bool PublishRecoverySignal);

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
    /// <c>true</c> when no recovery threshold signal was needed or every
    /// recovery-signal subscriber completed; <c>false</c> when a subscriber
    /// threw and the caller should keep any best-effort recovery tracking.
    /// </returns>
    bool RecordQuotaUsability(
        AgentMembership member,
        bool isUsable,
        bool publishRecoverySignal = true);
}

public interface IAgentQuotaAvailabilityObservationSource
{
    event Action<AgentQuotaUsabilityObservation>? QuotaUsabilityObserved;
}
