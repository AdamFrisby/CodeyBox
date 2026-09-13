using CodeyBox.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Verification tests for the banked reset-credit consume trigger (5/5).
/// Every test drives the real <see cref="ResetCreditConsumeTrigger"/> against a
/// fake transport and a file-backed store — no test can reach the live endpoint.
/// </summary>
public sealed class ResetCreditTriggerTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-01T10:00:00Z");
    private static readonly DateTimeOffset Deadline = DateTimeOffset.Parse("2026-09-10T00:00:00Z");

    private readonly string _tempDir;
    private bool _disposed;

    public ResetCreditTriggerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rc-trigger-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task FlagOff_NoRequestIssuedForSpendAdvice()
    {
        var clock = new FakeTriggerClock(Now);
        var transport = new FakeConsumeTransport { Balance = new ResetCreditBalance(2, Now) };
        var options = new ResetCreditTriggerOptions { Enabled = false, AllowLiveSpend = true };
        await using var trigger = BuildTrigger(() => options, transport, clock, out var store);

        var outcome = await trigger.TryTriggerAsync(SpendAdvice("codex", Now, Deadline), CancellationToken.None);

        Assert.Equal(ResetCreditTriggerDecision.RefusedFeatureDisabled, outcome.Decision);
        Assert.False(outcome.RequestIssued);
        Assert.False(outcome.Consumed);
        Assert.Equal(0, transport.ConsumeCalls);
        await AssertAuditAsync(store, outcome.RedeemRequestId, expectKey: true);
    }

    [Fact]
    public async Task FlagAbsent_DefaultsOff_NoRequestIssued()
    {
        var defaults = new ResetCreditTriggerOptions();
        Assert.False(defaults.Enabled);
        Assert.False(defaults.AllowLiveSpend);

        var clock = new FakeTriggerClock(Now);
        var transport = new FakeConsumeTransport { Balance = new ResetCreditBalance(3, Now) };
        await using var trigger = BuildTrigger(() => defaults, transport, clock, out var store);

        var outcome = await trigger.TryTriggerAsync(SpendAdvice("codex", Now, Deadline), CancellationToken.None);

        Assert.Equal(ResetCreditTriggerDecision.RefusedFeatureDisabled, outcome.Decision);
        Assert.Equal(0, transport.ConsumeCalls);
    }

    [Fact]
    public async Task EnabledWithoutLiveSpend_LogsIntendedRequestWithoutIssuing()
    {
        var clock = new FakeTriggerClock(Now);
        var transport = new FakeConsumeTransport { Balance = new ResetCreditBalance(2, Now) };
        var options = new ResetCreditTriggerOptions { Enabled = true, AllowLiveSpend = false };
        await using var trigger = BuildTrigger(() => options, transport, clock, out var store);

        var outcome = await trigger.TryTriggerAsync(SpendAdvice("codex", Now, Deadline), CancellationToken.None);

        Assert.Equal(ResetCreditTriggerDecision.DryRun, outcome.Decision);
        Assert.False(outcome.RequestIssued);
        Assert.False(outcome.Consumed);
        Assert.Equal(0, transport.ConsumeCalls);
        Assert.False(string.IsNullOrWhiteSpace(outcome.RedeemRequestId));
        Assert.Contains(outcome.RedeemRequestId, outcome.Reason, StringComparison.Ordinal);
        var audits = await store.ListAuditsAsync(CancellationToken.None);
        var audit = Assert.Single(audits);
        Assert.Equal(nameof(ResetCreditTriggerDecision.DryRun), audit.Gate);
        Assert.Equal(outcome.RedeemRequestId, audit.RedeemRequestId);
    }

    [Fact]
    public void ConsumePath_HasNoRouteToRealTransportUnderTest()
    {
        var triggerType = typeof(ResetCreditConsumeTrigger);
        var httpFields = triggerType
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
            .Where(f => f.FieldType == typeof(HttpClient));
        Assert.Empty(httpFields);

        var ctors = triggerType.GetConstructors();
        var ctor = Assert.Single(ctors);
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();
        Assert.Contains(typeof(IResetCreditConsumeTransport), paramTypes);
        Assert.DoesNotContain(typeof(HttpClient), paramTypes);
    }

    [Fact]
    public void LiveTransport_RefusesNonProviderHost()
    {
        using var http = new HttpClient();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new HttpResetCreditConsumeTransport(http, () => "token", "https://evil.example/api"));
    }

    [Fact]
    public void LiveTransport_DefaultsAreMisconfigurationSafe()
    {
        var options = new ResetCreditTriggerOptions();
        Assert.False(options.Enabled);
        Assert.False(options.AllowLiveSpend);
        Assert.False(options.KillSwitchEngaged);
        Assert.Equal(1, options.MaxCreditsPerPeriod);
        Assert.Contains("$80", options.CapDescription, StringComparison.Ordinal);
        Assert.Contains("1 credit", options.CapDescription, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepeatedTriggerForOneDecision_ReusesKeyAndConsumesOnce()
    {
        var clock = new FakeTriggerClock(Now);
        var transport = new FakeConsumeTransport { Balance = new ResetCreditBalance(2, Now) };
        var options = new ResetCreditTriggerOptions { Enabled = true, AllowLiveSpend = true };
        await using var trigger = BuildTrigger(() => options, transport, clock, out var store);
        var advice = SpendAdvice("codex", Now, Deadline);

        var first = await trigger.TryTriggerAsync(advice, CancellationToken.None);
        var second = await trigger.TryTriggerAsync(advice, CancellationToken.None);

        Assert.Equal(ResetCreditTriggerDecision.Consumed, first.Decision);
        Assert.Equal(ResetCreditTriggerDecision.AlreadyConsumed, second.Decision);
        Assert.Equal(first.RedeemRequestId, second.RedeemRequestId);
        Assert.Equal(1, transport.ConsumeCalls);
        Assert.Equal(1, transport.ServerConsumptions);
    }

    [Fact]
    public async Task TransportFailure_ResultsInAtMostOneConsumption()
    {
        var clock = new FakeTriggerClock(Now);
        var transport = new FakeConsumeTransport
        {
            Balance = new ResetCreditBalance(2, Now),
            ConsumeBehaviour = FakeConsumeTransport.Behaviour.Throw,
        };
        var options = new ResetCreditTriggerOptions { Enabled = true, AllowLiveSpend = true };
        await using var trigger = BuildTrigger(() => options, transport, clock, out var store);
        var advice = SpendAdvice("codex", Now, Deadline);

        var outcome = await trigger.TryTriggerAsync(advice, CancellationToken.None);

        Assert.True(outcome.Decision is ResetCreditTriggerDecision.ReconciledConsumed or ResetCreditTriggerDecision.RefusedTransportFailed);
        Assert.True(transport.ServerConsumptions <= 1);
        var retry = await trigger.TryTriggerAsync(advice, CancellationToken.None);
        Assert.True(transport.ServerConsumptions <= 1);
        Assert.Equal(outcome.RedeemRequestId, retry.RedeemRequestId);
    }

    [Fact]
    public async Task Timeout_ResultsInAtMostOneConsumption()
    {
        var clock = new FakeTriggerClock(Now);
        var transport = new FakeConsumeTransport
        {
            Balance = new ResetCreditBalance(2, Now),
            ConsumeBehaviour = FakeConsumeTransport.Behaviour.Timeout,
        };
        var options = new ResetCreditTriggerOptions { Enabled = true, AllowLiveSpend = true };
        await using var trigger = BuildTrigger(() => options, transport, clock, out var store);
        var advice = SpendAdvice("codex", Now, Deadline);

        var outcome = await trigger.TryTriggerAsync(advice, CancellationToken.None);

        Assert.True(outcome.Decision is ResetCreditTriggerDecision.ReconciledConsumed or ResetCreditTriggerDecision.RefusedTransportFailed);
        Assert.True(transport.ServerConsumptions <= 1);
    }

    [Fact]
    public async Task CrashBetweenDispatchAndResponse_ResultsInAtMostOneConsumption()
    {
        var clock = new FakeTriggerClock(Now);
        var server = new FakeConsumeServer();
        var crashing = new FakeConsumeTransport(server) { Balance = new ResetCreditBalance(2, Now), ConsumeBehaviour = FakeConsumeTransport.Behaviour.CrashAfterServerConsume };
        var options = new ResetCreditTriggerOptions { Enabled = true, AllowLiveSpend = true };
        var storePath = StorePath();
        var store1 = new FileResetCreditTriggerStore(storePath);
        var trigger1 = new ResetCreditConsumeTrigger(() => options, crashing, store1, clock, NullLogger.Instance);
        var advice = SpendAdvice("codex", Now, Deadline);

        var crashed = await trigger1.TryTriggerAsync(advice, CancellationToken.None);

        server.ApplyConsumptionToBalance(crashing);
        var restarted = new FakeConsumeTransport(server) { Balance = crashing.Balance };
        var store2 = new FileResetCreditTriggerStore(storePath);
        var trigger2 = new ResetCreditConsumeTrigger(() => options, restarted, store2, clock, NullLogger.Instance);
        var retried = await trigger2.TryTriggerAsync(advice, CancellationToken.None);

        Assert.Equal(crashed.RedeemRequestId, retried.RedeemRequestId);
        Assert.Equal(1, server.Consumptions);
        Assert.Equal(ResetCreditTriggerDecision.Consumed, retried.Decision);
        Assert.True(retried.Consumed);
    }

    [Fact]
    public async Task CapEnforced_AcrossProcessRestart()
    {
        var clock = new FakeTriggerClock(Now);
        var server = new FakeConsumeServer();
        var options = new ResetCreditTriggerOptions { Enabled = true, AllowLiveSpend = true, MaxCreditsPerPeriod = 1, Period = TimeSpan.FromDays(30) };
        var storePath = StorePath();

        var t1 = new FakeConsumeTransport(server) { Balance = new ResetCreditBalance(5, Now) };
        var trigger1 = new ResetCreditConsumeTrigger(() => options, t1, new FileResetCreditTriggerStore(storePath), clock, NullLogger.Instance);
        var first = await trigger1.TryTriggerAsync(SpendAdvice("codex", Now, Deadline), CancellationToken.None);
        Assert.Equal(ResetCreditTriggerDecision.Consumed, first.Decision);

        var t2 = new FakeConsumeTransport(server) { Balance = new ResetCreditBalance(4, Now) };
        var store2 = new FileResetCreditTriggerStore(storePath);
        var trigger2 = new ResetCreditConsumeTrigger(() => options, t2, store2, clock, NullLogger.Instance);
        var second = await trigger2.TryTriggerAsync(SpendAdvice("codex", Now, Deadline.AddDays(1)), CancellationToken.None);

        Assert.Equal(ResetCreditTriggerDecision.RefusedCapExceeded, second.Decision);
        Assert.False(second.RequestIssued);
        Assert.Equal(0, t2.ConsumeCalls);
        Assert.Equal(1, server.Consumptions);
        Assert.Contains("$80", second.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentAttempts_IssueAtMostOneRequest()
    {
        var clock = new FakeTriggerClock(Now);
        var server = new FakeConsumeServer();
        var transport = new FakeConsumeTransport(server) { Balance = new ResetCreditBalance(5, Now), ConsumeDelay = TimeSpan.FromMilliseconds(50) };
        var options = new ResetCreditTriggerOptions { Enabled = true, AllowLiveSpend = true, MaxCreditsPerPeriod = 8, Period = TimeSpan.FromDays(30) };
        await using var trigger = BuildTrigger(() => options, transport, clock, out _);
        var advice = SpendAdvice("codex", Now, Deadline);

        var tasks = Enumerable.Range(0, 16)
            .Select(_ => trigger.TryTriggerAsync(advice, CancellationToken.None))
            .ToList();
        var outcomes = await Task.WhenAll(tasks);

        Assert.Equal(1, server.Consumptions);
        Assert.Equal(1, transport.ConsumeCalls);
        var keys = outcomes.Select(o => o.RedeemRequestId).Distinct(StringComparer.Ordinal).ToList();
        Assert.Single(keys);
        Assert.Contains(outcomes, o => o.Decision == ResetCreditTriggerDecision.Consumed);
        Assert.All(outcomes.Where(o => o.Decision != ResetCreditTriggerDecision.Consumed), o => Assert.Equal(ResetCreditTriggerDecision.AlreadyConsumed, o.Decision));
    }

    [Fact]
    public async Task KillSwitch_TakesEffectWithoutRestart()
    {
        var clock = new FakeTriggerClock(Now);
        var transport = new FakeConsumeTransport { Balance = new ResetCreditBalance(2, Now) };
        var options = new ResetCreditTriggerOptions { Enabled = true, AllowLiveSpend = true, KillSwitchEngaged = false };
        await using var trigger = BuildTrigger(() => options, transport, clock, out var store);

        options = options with { KillSwitchEngaged = true };
        var blocked = await trigger.TryTriggerAsync(SpendAdvice("codex", Now, Deadline), CancellationToken.None);

        Assert.Equal(ResetCreditTriggerDecision.RefusedKillSwitch, blocked.Decision);
        Assert.Equal(0, transport.ConsumeCalls);

        options = options with { KillSwitchEngaged = false };
        var allowed = await trigger.TryTriggerAsync(SpendAdvice("codex", Now, Deadline), CancellationToken.None);

        Assert.Equal(ResetCreditTriggerDecision.Consumed, allowed.Decision);
        Assert.Equal(1, transport.ConsumeCalls);
        var audits = await store.ListAuditsAsync(CancellationToken.None);
        Assert.Contains(audits, a => a.Gate == nameof(ResetCreditTriggerDecision.RefusedKillSwitch));
    }

    [Theory]
    [InlineData(null, "unknown")]
    [InlineData(0, "empty")]
    [InlineData(-1, "empty")]
    public async Task BadBalance_RefusesWithRecordedReason(int? count, string _)
    {
        var clock = new FakeTriggerClock(Now);
        var transport = new FakeConsumeTransport { Balance = new ResetCreditBalance(count, Now) };
        var options = new ResetCreditTriggerOptions { Enabled = true, AllowLiveSpend = true };
        await using var trigger = BuildTrigger(() => options, transport, clock, out var store);

        var outcome = await trigger.TryTriggerAsync(SpendAdvice("codex", Now, Deadline), CancellationToken.None);

        Assert.True(
            outcome.Decision is ResetCreditTriggerDecision.RefusedBalanceUnknown or ResetCreditTriggerDecision.RefusedBalanceEmpty,
            $"unexpected {outcome.Decision}");
        Assert.False(outcome.RequestIssued);
        Assert.Equal(0, transport.ConsumeCalls);
        await AssertAuditAsync(store, outcome.RedeemRequestId, expectKey: true);
    }

    [Fact]
    public async Task StaleBalance_RefusesWithRecordedReason()
    {
        var clock = new FakeTriggerClock(Now);
        var transport = new FakeConsumeTransport { Balance = new ResetCreditBalance(2, Now - TimeSpan.FromHours(2)) };
        var options = new ResetCreditTriggerOptions { Enabled = true, AllowLiveSpend = true, MaxBalanceAge = TimeSpan.FromMinutes(15) };
        await using var trigger = BuildTrigger(() => options, transport, clock, out var store);

        var outcome = await trigger.TryTriggerAsync(SpendAdvice("codex", Now, Deadline), CancellationToken.None);

        Assert.Equal(ResetCreditTriggerDecision.RefusedBalanceStale, outcome.Decision);
        Assert.False(outcome.RequestIssued);
        Assert.Equal(0, transport.ConsumeCalls);
        await AssertAuditAsync(store, outcome.RedeemRequestId, expectKey: true);
    }

    [Fact]
    public async Task AdvisorHold_RefusesAndAudits()
    {
        var clock = new FakeTriggerClock(Now);
        var transport = new FakeConsumeTransport { Balance = new ResetCreditBalance(2, Now) };
        var options = new ResetCreditTriggerOptions { Enabled = true, AllowLiveSpend = true };
        await using var trigger = BuildTrigger(() => options, transport, clock, out var store);

        var outcome = await trigger.TryTriggerAsync(HoldAdvice("codex", Now), CancellationToken.None);

        Assert.Equal(ResetCreditTriggerDecision.RefusedAdvisorHold, outcome.Decision);
        Assert.Equal(0, transport.ConsumeCalls);
        await AssertAuditAsync(store, outcome.RedeemRequestId, expectKey: true);
    }

    [Fact]
    public async Task EveryDecision_ProducesAuditWithKeyAndSpend()
    {
        var clock = new FakeTriggerClock(Now);
        var transport = new FakeConsumeTransport { Balance = new ResetCreditBalance(2, Now) };
        var options = new ResetCreditTriggerOptions { Enabled = false, AllowLiveSpend = false };
        await using var trigger = BuildTrigger(() => options, transport, clock, out var store);

        var refused = await trigger.TryTriggerAsync(SpendAdvice("codex", Now, Deadline), CancellationToken.None);
        options = options with { Enabled = true };
        var dryRun = await trigger.TryTriggerAsync(SpendAdvice("codex", Now, Deadline), CancellationToken.None);

        var audits = await store.ListAuditsAsync(CancellationToken.None);
        Assert.Equal(2, audits.Count);
        foreach (var audit in audits)
        {
            Assert.False(string.IsNullOrWhiteSpace(audit.RedeemRequestId));
            Assert.False(string.IsNullOrWhiteSpace(audit.Gate));
            Assert.True(audit.PeriodSpend >= 0);
            Assert.True(audit.PeriodCap >= 0);
            Assert.False(string.IsNullOrWhiteSpace(audit.Detail));
        }
        Assert.Equal(refused.RedeemRequestId, audits[0].RedeemRequestId);
        Assert.Equal(dryRun.RedeemRequestId, audits[1].RedeemRequestId);
        Assert.Equal(refused.PeriodSpend, audits[0].PeriodSpend);
    }

    [Fact]
    public async Task CorruptStateFile_FailsClosedWithoutSpending()
    {
        var clock = new FakeTriggerClock(Now);
        var transport = new FakeConsumeTransport { Balance = new ResetCreditBalance(2, Now) };
        var options = new ResetCreditTriggerOptions { Enabled = true, AllowLiveSpend = true };
        var path = StorePath();
        await File.WriteAllTextAsync(path, "{not valid json", CancellationToken.None);
        var trigger = new ResetCreditConsumeTrigger(() => options, transport, new FileResetCreditTriggerStore(path), clock, NullLogger.Instance);

        await Assert.ThrowsAnyAsync<Exception>(() => trigger.TryTriggerAsync(SpendAdvice("codex", Now, Deadline), CancellationToken.None));

        Assert.Equal(0, transport.ConsumeCalls);
    }

    [Fact]
    public void DecisionFingerprint_IsDeterministicPerDecision()
    {
        var a = SpendAdvice("codex", Now, Deadline);
        var b = SpendAdvice("codex", Now, Deadline);
        var c = SpendAdvice("codex", Now, Deadline.AddDays(1));

        Assert.Equal(
            ResetCreditConsumeTrigger.ComputeDecisionFingerprint(a),
            ResetCreditConsumeTrigger.ComputeDecisionFingerprint(b));
        Assert.NotEqual(
            ResetCreditConsumeTrigger.ComputeDecisionFingerprint(a),
            ResetCreditConsumeTrigger.ComputeDecisionFingerprint(c));
        Assert.Equal(
            ResetCreditConsumeTrigger.ComputeRedeemRequestId(ResetCreditConsumeTrigger.ComputeDecisionFingerprint(a)),
            ResetCreditConsumeTrigger.ComputeRedeemRequestId(ResetCreditConsumeTrigger.ComputeDecisionFingerprint(b)));
    }

    private static ResetSpendAdvice SpendAdvice(string agent, DateTimeOffset now, DateTimeOffset closesAt) => new()
    {
        Agent = agent,
        EvaluatedAt = now,
        ShouldSpend = true,
        Reason = ResetAdviceReason.SpendBeforeDeadline,
        Rationale = "test spend",
        OptimalWindow = new ResetSpendWindow(now, closesAt),
        DecisionDeadline = closesAt,
        NextCreditExpiresAt = closesAt,
    };

    private static ResetSpendAdvice HoldAdvice(string agent, DateTimeOffset now) => new()
    {
        Agent = agent,
        EvaluatedAt = now,
        ShouldSpend = false,
        Reason = ResetAdviceReason.BurnFirst,
        Rationale = "test hold",
        UsableQuotaPct = 42,
    };

    private static async Task AssertAuditAsync(IResetCreditTriggerStore store, string redeemKey, bool expectKey)
    {
        var audits = await store.ListAuditsAsync(CancellationToken.None);
        var audit = Assert.Single(audits);
        if (expectKey)
            Assert.False(string.IsNullOrWhiteSpace(audit.RedeemRequestId));
        Assert.Equal(redeemKey, audit.RedeemRequestId);
        Assert.True(audit.PeriodSpend >= 0);
    }

    private string StorePath() => Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".json");

    private TriggerHandle BuildTrigger(
        Func<ResetCreditTriggerOptions> provider,
        FakeConsumeTransport transport,
        TimeProvider clock,
        out IResetCreditTriggerStore store)
    {
        store = new FileResetCreditTriggerStore(StorePath());
        return new TriggerHandle(new ResetCreditConsumeTrigger(provider, transport, store, clock, NullLogger.Instance));
    }

    private sealed class TriggerHandle(ResetCreditConsumeTrigger inner) : IAsyncDisposable
    {
        public Task<ResetCreditTriggerOutcome> TryTriggerAsync(ResetSpendAdvice? advice, CancellationToken ct)
            => inner.TryTriggerAsync(advice, ct);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeTriggerClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }

    /// <summary>
    /// Fake provider transport. The embedded <see cref="FakeConsumeServer"/> is the
    /// fake provider: it dedupes by redeem key, so a retry with the same key can
    /// never consume twice — mirroring the real endpoint's idempotency contract.
    /// </summary>
    private sealed class FakeConsumeTransport : IResetCreditConsumeTransport
    {
        public enum Behaviour { Success, Throw, Timeout, CrashAfterServerConsume }

        private readonly FakeConsumeServer _server;

        public FakeConsumeTransport() => _server = new FakeConsumeServer();
        public FakeConsumeTransport(FakeConsumeServer server) => _server = server;

        public ResetCreditBalance Balance { get; set; } = new(null, null);
        public Behaviour ConsumeBehaviour { get; set; } = Behaviour.Success;
        public TimeSpan ConsumeDelay { get; set; } = TimeSpan.Zero;
        public int ConsumeCalls { get; private set; }
        public int ServerConsumptions => _server.Consumptions;

        public Task<ResetCreditBalance> ReadBalanceAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (ReadThrows)
                throw new ResetCreditTransportException("fake balance read failed");
            return Task.FromResult(Balance);
        }

        public bool ReadThrows { get; set; }

        public async Task<ResetCreditConsumeResult> ConsumeAsync(ResetCreditConsumeRequest request, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(request);
            ConsumeCalls++;
            if (ConsumeDelay > TimeSpan.Zero)
                await Task.Delay(ConsumeDelay, ct).ConfigureAwait(false);
            switch (ConsumeBehaviour)
            {
                case Behaviour.Throw:
                    throw new ResetCreditTransportException("fake transport failure");
                case Behaviour.Timeout:
                    throw new TimeoutException("fake timeout");
                case Behaviour.CrashAfterServerConsume:
                    _server.Consume(request.RedeemRequestId);
                    throw new ResetCreditTransportException("fake crash between dispatch and response");
                default:
                    _server.Consume(request.RedeemRequestId);
                    return new ResetCreditConsumeResult { Consumed = true, CreditId = request.CreditId };
            }
        }
    }

    /// <summary>Fake provider side: idempotent consumption ledger keyed by redeem key.
    /// A retry with an already-consumed key replays success without a second
    /// consumption — the real endpoint's idempotency contract.</summary>
    private sealed class FakeConsumeServer
    {
        private readonly HashSet<string> _consumedKeys = new(StringComparer.Ordinal);
        private readonly object _lock = new();
        public int Consumptions
        {
            get { lock (_lock) return _consumedKeys.Count; }
        }

        public void Consume(string redeemKey)
        {
            lock (_lock) _ = _consumedKeys.Add(redeemKey);
        }

        public void ApplyConsumptionToBalance(FakeConsumeTransport transport)
        {
            if (transport.Balance.AvailableCount is { } count && transport.Balance.SampledAt is { } sampled)
                transport.Balance = new ResetCreditBalance(Math.Max(0, count - _consumedKeys.Count), sampled);
        }
    }
}
