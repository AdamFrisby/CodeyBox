using System.Collections.Concurrent;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Shared transition tracker for quota availability observations. It publishes
/// the existing wake-up signal only on unusable -> usable transitions; first
/// observations and usable -> unusable changes are just recorded.
/// </summary>
public sealed class AgentQuotaAvailabilityBroadcaster : IAgentQuotaAvailabilitySignal, IAgentQuotaAvailabilityPublisher
{
    private readonly ConcurrentDictionary<MemberQuotaKey, bool> _lastUsable = new();
    private readonly ILogger<AgentQuotaAvailabilityBroadcaster> _log;

    public AgentQuotaAvailabilityBroadcaster(ILogger<AgentQuotaAvailabilityBroadcaster>? log = null)
        => _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentQuotaAvailabilityBroadcaster>.Instance;

    public event Action? QuotaUsableThresholdCrossed;

    public void RecordQuotaUsability(AgentMembership member, bool isUsable)
    {
        var key = MemberQuotaKey.From(member);
        if (!RecordTransition(key, isUsable))
            return;

        _log.LogInformation(
            "Quota availability for {Agent}/{Model} crossed from unusable to usable; triggering parked-item recovery sweep",
            member.Agent.Value,
            member.ModelId ?? "(default)");
        NotifyQuotaUsableThresholdCrossed();
    }

    private bool RecordTransition(MemberQuotaKey key, bool isUsable)
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

    private readonly record struct MemberQuotaKey(string RouteKey, AgentKind Agent, string ModelId)
    {
        public static MemberQuotaKey From(AgentMembership member) =>
            new(member.RouteKey, member.Agent, member.ModelId ?? string.Empty);
    }
}

/// <summary>
/// Publisher side of <see cref="IAgentQuotaAvailabilitySignal"/>. Quota
/// observers call this after evaluating a member against the effective floor.
/// </summary>
public interface IAgentQuotaAvailabilityPublisher
{
    void RecordQuotaUsability(AgentMembership member, bool isUsable);
}
