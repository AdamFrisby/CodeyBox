using CodeyBox.Core;
using Microsoft.Extensions.Options;

namespace CodeyBox.AdminSeed;

/// <summary>
/// Deterministic quota probe for the seeded fake agent. Returns fixed,
/// seed-derived availability snapshots — one healthy member, one exhausted
/// member — so the admin quota/capacity pages render rich content without
/// touching any provider API. Pure and cheap (no I/O, no throwing on the
/// hot path), as <see cref="IAgentQuotaProbe"/> requires.
/// </summary>
public sealed class SeededFakeQuotaProbe : IAgentQuotaProbe
{
    private readonly IOptionsMonitor<SeededFakeAgentOptions> _options;
    private readonly TimeProvider _timeProvider;

    public SeededFakeQuotaProbe(
        IOptionsMonitor<SeededFakeAgentOptions> options,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public AgentKind Kind => SeededFakeAgentRunner.FakeKind;

    public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(member);
        var seed = _options.CurrentValue.Seed;
        var successBuckets = Math.Clamp(_options.CurrentValue.DefaultSuccessBuckets, 0, 100);
        var bucket = SeededFakeBehaviorSelector.Fnv1a32($"{seed}:{member.RouteKey}") % 100;
        var healthy = bucket < successBuckets;
        var snapshot = healthy
            ? new AgentQuotaSnapshot
            {
                AvailablePct = 60 + (bucket % 35),
                ResetAt = _timeProvider.GetUtcNow().AddHours(5),
                Notes = $"seeded-fake healthy member (seed {seed})",
            }
            : new AgentQuotaSnapshot
            {
                AvailablePct = 0,
                ResetAt = _timeProvider.GetUtcNow().AddMinutes(30),
                Notes = $"seeded-fake exhausted member (seed {seed})",
            };
        return Task.FromResult(snapshot);
    }
}
