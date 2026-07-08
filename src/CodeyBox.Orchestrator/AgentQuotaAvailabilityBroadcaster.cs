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

    public void RecordQuotaUsability(
        AgentMembership member,
        bool isUsable,
        bool publishRecoverySignal = true)
    {
        var key = AgentQuotaMemberKey.From(member);
        var crossedToUsable = RecordTransition(key, isUsable);
        NotifyQuotaUsabilityObserved(new AgentQuotaUsabilityObservation(member, isUsable, publishRecoverySignal));

        if (!publishRecoverySignal || !crossedToUsable)
            return;

        _log.LogInformation(
            "Quota availability for {Agent}/{Model} crossed from unusable to usable; triggering parked-item recovery sweep",
            member.Agent.Value,
            member.ModelId ?? "(default)");
        NotifyQuotaUsableThresholdCrossed();
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

    private void NotifyQuotaUsabilityObserved(AgentQuotaUsabilityObservation observation)
    {
        var handlers = QuotaUsabilityObserved;
        if (handlers is null)
            return;

        foreach (Action<AgentQuotaUsabilityObservation> handler in handlers.GetInvocationList().Cast<Action<AgentQuotaUsabilityObservation>>())
        {
            try
            {
                handler(observation);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Quota usability-observation subscriber threw; continuing");
            }
        }
    }

    private void NotifyQuotaUsableThresholdCrossed()
    {
        var handlers = QuotaUsableThresholdCrossed;
        if (handlers is null)
            return;

        foreach (Action handler in handlers.GetInvocationList().Cast<Action>())
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Quota usable threshold subscriber threw; continuing");
            }
        }
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
    /// Records the caller's fully evaluated routing verdict for
    /// <paramref name="member"/>. Implementations publish only after they have
    /// previously observed the same member as unusable, so callers must record
    /// both the denied observation and the later allowed observation. Set
    /// <paramref name="publishRecoverySignal"/> to <c>false</c> for read-only
    /// probes such as readiness checks; command-style dispatch/retry/fallback
    /// paths should leave it enabled so parked-item recovery wakes deliberately.
    /// Calls may arrive concurrently from independent router/probe paths and
    /// must be handled without relying on single-threaded ordering.
    /// </summary>
    void RecordQuotaUsability(
        AgentMembership member,
        bool isUsable,
        bool publishRecoverySignal = true);
}

public interface IAgentQuotaAvailabilityObservationSource
{
    event Action<AgentQuotaUsabilityObservation>? QuotaUsabilityObserved;
}
