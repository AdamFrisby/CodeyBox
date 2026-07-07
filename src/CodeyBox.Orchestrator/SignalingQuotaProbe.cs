using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Decorates a quota probe and turns known below/above-floor readings into
/// shared quota-availability transition events. This lets any probe caller
/// wake quota-parked work, not just the dispatch router.
/// </summary>
public sealed class SignalingQuotaProbe : IAgentQuotaProbe
{
    private readonly IAgentQuotaProbe _inner;
    private readonly IAgentQuotaAvailabilityPublisher _publisher;
    private readonly QuotaGatePolicy _quotaGatePolicy;
    private readonly TimeProvider _time;

    public SignalingQuotaProbe(
        IAgentQuotaProbe inner,
        IAgentQuotaAvailabilityPublisher publisher,
        QuotaGatePolicy quotaGatePolicy,
        TimeProvider? timeProvider = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _quotaGatePolicy = quotaGatePolicy ?? throw new ArgumentNullException(nameof(quotaGatePolicy));
        _time = timeProvider ?? TimeProvider.System;
    }

    public AgentKind Kind => _inner.Kind;

    public async Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
    {
        var snapshot = await _inner.GetAvailabilityAsync(member, ct).ConfigureAwait(false);
        RecordKnownQuotaUsability(member, snapshot);
        return snapshot;
    }

    public async Task MarkExhaustedAsync(
        AgentMembership member,
        TimeSpan ttl,
        DateTimeOffset? resetAt = null,
        CancellationToken ct = default)
    {
        await _inner.MarkExhaustedAsync(member, ttl, resetAt, ct).ConfigureAwait(false);

        if (ttl <= TimeSpan.Zero)
            return;
        if (resetAt is { } reset && reset <= _time.GetUtcNow())
            return;

        _publisher.RecordQuotaUsability(member, isUsable: false);
    }

    private void RecordKnownQuotaUsability(AgentMembership member, AgentQuotaSnapshot snapshot)
    {
        if (member.Billing != AgentBilling.Subscription)
            return;

        var quota = QuotaGatePolicy.ResolveMemberQuota(snapshot, member);
        if (!quota.IsKnown)
            return;

        var gate = _quotaGatePolicy.Evaluate(member, quota, _time.GetUtcNow());
        _publisher.RecordQuotaUsability(member, gate.Allow);
    }
}
