using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class PausedAgentQuotaProbeTests
{
    private static readonly AgentMembership ClaudeMember = new()
    {
        Agent = AgentKind.Claude,
        Billing = AgentBilling.Subscription,
        QualityScore = 100,
    };

    [Fact]
    public async Task PausedMember_UsesPausedCacheTtl_ActiveMemberCadenceIsUnchanged()
    {
        var time = new ManualTimeProvider();
        var inner = new StubQuotaProbe { Next = new AgentQuotaSnapshot { AvailablePct = 80 } };
        var pauses = new TogglePauseController { Paused = true };
        var probe = Build(inner, pauses, time);

        Assert.Equal(80, (await probe.GetAvailabilityAsync(ClaudeMember, CancellationToken.None)).AvailablePct);

        time.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal(80, (await probe.GetAvailabilityAsync(ClaudeMember, CancellationToken.None)).AvailablePct);
        Assert.Equal(1, inner.CallCount);

        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(80, (await probe.GetAvailabilityAsync(ClaudeMember, CancellationToken.None)).AvailablePct);
        Assert.Equal(2, inner.CallCount);

        pauses.Paused = false;
        await probe.GetAvailabilityAsync(ClaudeMember, CancellationToken.None);
        await probe.GetAvailabilityAsync(ClaudeMember, CancellationToken.None);

        Assert.Equal(4, inner.CallCount);
    }

    [Fact]
    public async Task PausedMember_TransientUnknown_ServesRetainedReadingWithinPausedStaleness()
    {
        var time = new ManualTimeProvider();
        var inner = new StubQuotaProbe { Next = new AgentQuotaSnapshot { AvailablePct = 67 } };
        var pauses = new TogglePauseController { Paused = false };
        var probe = BuildWithLastKnownGood(inner, pauses, time);

        Assert.Equal(67, (await probe.GetAvailabilityAsync(ClaudeMember, CancellationToken.None)).AvailablePct);

        pauses.Paused = true;
        inner.Next = AgentQuotaSnapshot.UnknownSnapshot(QuotaUnknownReason.Transient, "HTTP 429");
        time.Advance(TimeSpan.FromMinutes(60));

        var stale = await probe.GetAvailabilityAsync(ClaudeMember, CancellationToken.None);
        Assert.Equal(67, stale.AvailablePct);
        Assert.Contains("stale", stale.Notes!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, inner.CallCount);

        time.Advance(TimeSpan.FromMinutes(20));
        Assert.Equal(67, (await probe.GetAvailabilityAsync(ClaudeMember, CancellationToken.None)).AvailablePct);
        Assert.Equal(2, inner.CallCount);

        time.Advance(TimeSpan.FromMinutes(11));
        var expired = await probe.GetAvailabilityAsync(ClaudeMember, CancellationToken.None);

        Assert.False(expired.IsKnown);
        Assert.Equal(2, inner.CallCount);
    }

    [Fact]
    public async Task PausedMember_PermanentUnknown_IsCachedForPausedTtl()
    {
        var time = new ManualTimeProvider();
        var inner = new StubQuotaProbe
        {
            Next = AgentQuotaSnapshot.UnknownSnapshot(QuotaUnknownReason.Permanent, "HTTP 401"),
        };
        var pauses = new TogglePauseController { Paused = true };
        var probe = Build(inner, pauses, time);

        var first = await probe.GetAvailabilityAsync(ClaudeMember, CancellationToken.None);
        Assert.False(first.IsKnown);
        Assert.Equal(QuotaUnknownReason.Permanent, first.Unknown);

        inner.Next = new AgentQuotaSnapshot { AvailablePct = 88 };
        time.Advance(TimeSpan.FromMinutes(59));
        var cached = await probe.GetAvailabilityAsync(ClaudeMember, CancellationToken.None);

        Assert.False(cached.IsKnown);
        Assert.Equal(QuotaUnknownReason.Permanent, cached.Unknown);
        Assert.Equal(1, inner.CallCount);

        time.Advance(TimeSpan.FromMinutes(2));
        var refreshed = await probe.GetAvailabilityAsync(ClaudeMember, CancellationToken.None);

        Assert.Equal(88, refreshed.AvailablePct);
        Assert.Equal(2, inner.CallCount);
    }

    [Fact]
    public async Task MarkExhaustedAsync_DelegatesAndClearsPausedCacheAndRetainedReading()
    {
        var time = new ManualTimeProvider();
        var inner = new StubQuotaProbe { Next = new AgentQuotaSnapshot { AvailablePct = 80 } };
        var pauses = new TogglePauseController { Paused = true };
        var probe = BuildWithLastKnownGood(inner, pauses, time);

        Assert.Equal(80, (await probe.GetAvailabilityAsync(ClaudeMember, CancellationToken.None)).AvailablePct);

        var resetAt = time.GetUtcNow().AddMinutes(10);
        inner.Next = AgentQuotaSnapshot.UnknownSnapshot(QuotaUnknownReason.Transient, "HTTP 429");

        await probe.MarkExhaustedAsync(
            ClaudeMember,
            TimeSpan.FromMinutes(10),
            resetAt,
            CancellationToken.None);

        Assert.Equal(1, inner.MarkExhaustedCallCount);
        Assert.Equal(ClaudeMember, inner.LastMarkedMember);
        Assert.Equal(TimeSpan.FromMinutes(10), inner.LastMarkedTtl);
        Assert.Equal(resetAt, inner.LastMarkedResetAt);

        var afterExhaustion = await probe.GetAvailabilityAsync(ClaudeMember, CancellationToken.None);

        Assert.False(afterExhaustion.IsKnown);
        Assert.Equal(QuotaUnknownReason.Transient, afterExhaustion.Unknown);
        Assert.Equal(2, inner.CallCount);
    }

    [Fact]
    public async Task InvalidateCredentialState_ClearsPausedAndLastKnownGoodCachesAndPropagatesToInner()
    {
        var time = new ManualTimeProvider();
        var inner = new CachingInvalidatableProbe
        {
            Next = new AgentQuotaSnapshot { AvailablePct = 74 },
        };
        var pauses = new TogglePauseController { Paused = true };
        var paused = new PausedAgentQuotaProbe(
            inner,
            pauses,
            () => new PausedAgentQuotaProbeOptions
            {
                CacheTtl = TimeSpan.FromHours(1),
            },
            NullLogger<PausedAgentQuotaProbe>.Instance,
            time);
        var probe = new LastKnownGoodQuotaProbe(
            paused,
            (_, _) => ValueTask.FromResult(new LastKnownGoodQuotaOptions
            {
                MaxStaleness = TimeSpan.FromMinutes(90),
            }),
            NullLogger<LastKnownGoodQuotaProbe>.Instance,
            time);

        Assert.Equal(74, (await probe.GetAvailabilityAsync(ClaudeMember, CancellationToken.None)).AvailablePct);
        Assert.Equal(1, inner.FetchCount);

        inner.Next = AgentQuotaSnapshot.UnknownSnapshot(QuotaUnknownReason.Transient, "token rotated");

        probe.InvalidateCredentialState();

        var afterInvalidation = await probe.GetAvailabilityAsync(ClaudeMember, CancellationToken.None);

        Assert.False(afterInvalidation.IsKnown);
        Assert.Equal(QuotaUnknownReason.Transient, afterInvalidation.Unknown);
        Assert.Equal(2, inner.FetchCount);
        Assert.Equal(1, inner.InvalidateCount);
    }

    [Fact]
    public async Task PausedMember_ReadsUpdatedOptionsOnLaterCalls()
    {
        var time = new ManualTimeProvider();
        var inner = new StubQuotaProbe { Next = new AgentQuotaSnapshot { AvailablePct = 80 } };
        var pauses = new TogglePauseController { Paused = true };
        var options = new PausedAgentQuotaProbeOptions
        {
            CacheTtl = TimeSpan.FromHours(1),
        };
        var probe = Build(inner, pauses, time, () => options);

        Assert.Equal(80, (await probe.GetAvailabilityAsync(ClaudeMember, CancellationToken.None)).AvailablePct);

        inner.Next = new AgentQuotaSnapshot { AvailablePct = 70 };
        time.Advance(TimeSpan.FromMinutes(30));
        options = options with { CacheTtl = TimeSpan.FromMinutes(20) };

        Assert.Equal(70, (await probe.GetAvailabilityAsync(ClaudeMember, CancellationToken.None)).AvailablePct);
        Assert.Equal(2, inner.CallCount);
    }

    [Fact]
    public async Task PausedMember_NonPositiveCadenceOptionsAreRejected()
    {
        var time = new ManualTimeProvider();
        var inner = new StubQuotaProbe { Next = new AgentQuotaSnapshot { AvailablePct = 80 } };
        var pauses = new TogglePauseController { Paused = true };
        var probe = Build(
            inner,
            pauses,
            time,
            () => new PausedAgentQuotaProbeOptions
            {
                CacheTtl = TimeSpan.Zero,
            });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => probe.GetAvailabilityAsync(ClaudeMember, CancellationToken.None));
    }

    [Fact]
    public async Task PausedMember_ConcurrentColdCacheMisses_CoalesceSingleInnerProbe()
    {
        var time = new ManualTimeProvider();
        var inner = new BlockingQuotaProbe();
        var pauses = new TogglePauseController { Paused = true };
        var probe = new PausedAgentQuotaProbe(
            inner,
            pauses,
            () => new PausedAgentQuotaProbeOptions
            {
                CacheTtl = TimeSpan.FromHours(1),
            },
            NullLogger<PausedAgentQuotaProbe>.Instance,
            time);

        var calls = Enumerable.Range(0, 8)
            .Select(_ => probe.GetAvailabilityAsync(ClaudeMember, CancellationToken.None))
            .ToArray();

        await inner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, inner.CallCount);

        inner.Complete(new AgentQuotaSnapshot { AvailablePct = 64 });
        var snapshots = await Task.WhenAll(calls);

        Assert.All(snapshots, snapshot => Assert.Equal(64, snapshot.AvailablePct));
        Assert.Equal(1, inner.CallCount);
    }

    [Fact]
    public async Task PausedMember_DoesNotReusePausedSnapshotAcrossModelsForSameRoute()
    {
        var time = new ManualTimeProvider();
        var inner = new StubQuotaProbe
        {
            Next = ModelSnapshot("claude-opus-4-7", 80),
        };
        var pauses = new TogglePauseController { Paused = true };
        var probe = Build(inner, pauses, time);
        var opus = ClaudeMember with { ModelId = "claude-opus-4-7" };
        var sonnet = ClaudeMember with { ModelId = "claude-sonnet-4-6" };

        var opusSnapshot = await probe.GetAvailabilityAsync(opus, CancellationToken.None);
        Assert.Equal(80, opusSnapshot.PerModel["claude-opus-4-7"].AvailablePct);

        inner.Next = ModelSnapshot("claude-sonnet-4-6", 70);
        time.Advance(TimeSpan.FromMinutes(30));
        var sonnetSnapshot = await probe.GetAvailabilityAsync(sonnet, CancellationToken.None);

        Assert.Equal(70, sonnetSnapshot.PerModel["claude-sonnet-4-6"].AvailablePct);
        Assert.DoesNotContain("claude-opus-4-7", sonnetSnapshot.PerModel.Keys);
        Assert.Equal(2, inner.CallCount);
    }

    [Fact]
    public async Task PausedMember_MaxCacheEntries_EvictsOldestRouteAndRefetchesIt()
    {
        var time = new ManualTimeProvider();
        var inner = new StubQuotaProbe { Next = new AgentQuotaSnapshot { AvailablePct = 10 } };
        var pauses = new TogglePauseController { Paused = true };
        var options = new PausedAgentQuotaProbeOptions
        {
            CacheTtl = TimeSpan.FromHours(1),
            MaxCacheEntries = 2,
        };
        var probe = Build(inner, pauses, time, () => options);
        var alpha = ClaudeMember with { InstanceId = "alpha" };
        var beta = ClaudeMember with { InstanceId = "beta" };
        var gamma = ClaudeMember with { InstanceId = "gamma" };

        Assert.Equal(10, (await probe.GetAvailabilityAsync(alpha, CancellationToken.None)).AvailablePct);
        time.Advance(TimeSpan.FromSeconds(1));

        inner.Next = new AgentQuotaSnapshot { AvailablePct = 20 };
        Assert.Equal(20, (await probe.GetAvailabilityAsync(beta, CancellationToken.None)).AvailablePct);
        time.Advance(TimeSpan.FromSeconds(1));

        inner.Next = new AgentQuotaSnapshot { AvailablePct = 30 };
        Assert.Equal(30, (await probe.GetAvailabilityAsync(gamma, CancellationToken.None)).AvailablePct);
        Assert.Equal(3, inner.CallCount);

        inner.Next = new AgentQuotaSnapshot { AvailablePct = 21 };
        Assert.Equal(20, (await probe.GetAvailabilityAsync(beta, CancellationToken.None)).AvailablePct);
        Assert.Equal(3, inner.CallCount);

        inner.Next = new AgentQuotaSnapshot { AvailablePct = 11 };
        Assert.Equal(11, (await probe.GetAvailabilityAsync(alpha, CancellationToken.None)).AvailablePct);
        Assert.Equal(4, inner.CallCount);
    }

    private static AgentQuotaSnapshot ModelSnapshot(string modelId, double availablePct) => new()
    {
        AvailablePct = availablePct,
        PerModel = new Dictionary<string, ModelQuota>(StringComparer.OrdinalIgnoreCase)
        {
            [modelId] = new ModelQuota { AvailablePct = availablePct },
        },
    };

    private static PausedAgentQuotaProbe Build(
        StubQuotaProbe inner,
        TogglePauseController pauses,
        TimeProvider time)
        => new(
            inner,
            pauses,
            () => new PausedAgentQuotaProbeOptions
            {
                CacheTtl = TimeSpan.FromHours(1),
            },
            NullLogger<PausedAgentQuotaProbe>.Instance,
            time);

    private static LastKnownGoodQuotaProbe BuildWithLastKnownGood(
        StubQuotaProbe inner,
        TogglePauseController pauses,
        TimeProvider time)
    {
        var paused = Build(inner, pauses, time);
        return new LastKnownGoodQuotaProbe(
            paused,
            (_, _) => ValueTask.FromResult(new LastKnownGoodQuotaOptions
            {
                MaxStaleness = pauses.Paused ? TimeSpan.FromMinutes(90) : TimeSpan.FromMinutes(5),
            }),
            NullLogger<LastKnownGoodQuotaProbe>.Instance,
            time);
    }

    private static PausedAgentQuotaProbe Build(
        StubQuotaProbe inner,
        TogglePauseController pauses,
        TimeProvider time,
        Func<PausedAgentQuotaProbeOptions> optionsProvider)
        => new(
            inner,
            pauses,
            optionsProvider,
            NullLogger<PausedAgentQuotaProbe>.Instance,
            time);

    private sealed class StubQuotaProbe : IAgentQuotaProbe
    {
        public AgentKind Kind => AgentKind.Claude;
        public AgentQuotaSnapshot Next { get; set; } = new() { AvailablePct = 100 };
        public int CallCount { get; private set; }
        public int MarkExhaustedCallCount { get; private set; }
        public AgentMembership? LastMarkedMember { get; private set; }
        public TimeSpan? LastMarkedTtl { get; private set; }
        public DateTimeOffset? LastMarkedResetAt { get; private set; }

        public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(Next);
        }

        public Task MarkExhaustedAsync(
            AgentMembership member,
            TimeSpan ttl,
            DateTimeOffset? resetAt = null,
            CancellationToken ct = default)
        {
            MarkExhaustedCallCount++;
            LastMarkedMember = member;
            LastMarkedTtl = ttl;
            LastMarkedResetAt = resetAt;
            return Task.CompletedTask;
        }
    }

    private sealed class CachingInvalidatableProbe : IAgentQuotaProbe, IAgentQuotaCacheInvalidator
    {
        private AgentQuotaSnapshot? _cached;

        public AgentKind Kind => AgentKind.Claude;
        public AgentQuotaSnapshot Next { get; set; } = new() { AvailablePct = 100 };
        public int FetchCount { get; private set; }
        public int InvalidateCount { get; private set; }

        public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
        {
            if (_cached is not null)
                return Task.FromResult(_cached);

            FetchCount++;
            _cached = Next;
            return Task.FromResult(_cached);
        }

        public void InvalidateCache() => InvalidateCredentialState();

        public void InvalidateCredentialState()
        {
            InvalidateCount++;
            _cached = null;
        }
    }

    private sealed class BlockingQuotaProbe : IAgentQuotaProbe
    {
        private readonly TaskCompletionSource<AgentQuotaSnapshot> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public AgentKind Kind => AgentKind.Claude;
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount => Volatile.Read(ref _callCount);

        public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
        {
            Interlocked.Increment(ref _callCount);
            Started.TrySetResult();
            return _completion.Task.WaitAsync(ct);
        }

        public void Complete(AgentQuotaSnapshot snapshot) => _completion.SetResult(snapshot);
    }

    private sealed class TogglePauseController : IAgentPauseController
    {
        public bool Paused { get; set; }

        public Task<AgentPauseState> PauseAsync(
            AgentKind agent,
            string reason,
            string pausedBy,
            DateTimeOffset? expiresAt = null,
            CancellationToken ct = default,
            string? agentInstanceId = null)
        {
            Paused = true;
            return Task.FromResult(State(agent, reason, pausedBy, expiresAt, agentInstanceId));
        }

        public Task<bool> ResumeAsync(
            AgentKind agent,
            string resumedBy,
            string? reason = null,
            CancellationToken ct = default,
            string? agentInstanceId = null)
        {
            var wasPaused = Paused;
            Paused = false;
            return Task.FromResult(wasPaused);
        }

        public Task<AgentPauseState?> GetAgentStateAsync(
            AgentKind agent,
            CancellationToken ct = default,
            string? agentInstanceId = null)
            => Task.FromResult(Paused
                ? State(agent, "maintenance", "test", null, agentInstanceId)
                : null);

        public Task<IReadOnlyList<AgentPauseState>> ListPausedAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AgentPauseState>>(
                Paused ? [State(AgentKind.Claude, "maintenance", "test", null, null)] : []);

        private static AgentPauseState State(
            AgentKind agent,
            string reason,
            string pausedBy,
            DateTimeOffset? expiresAt,
            string? agentInstanceId)
            => new(
                agent,
                true,
                DateTimeOffset.UnixEpoch,
                reason,
                pausedBy,
                expiresAt,
                DateTimeOffset.UnixEpoch,
                agentInstanceId);
    }
}
