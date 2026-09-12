using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.QuotaResetNotifier;
using CodeyBox.Webhooks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Acceptance tests for the quota-reset notifier plugin: a simulated optimal
/// transition fires exactly one <c>quota.reset_optimal</c> webhook with the
/// expected payload, a non-optimal cycle fires none, and the cooldown
/// suppresses repeats. Each test drives the plugin's real
/// <see cref="IMetricSampler.SampleOnceAsync"/> path against stubbed advisor /
/// estimator / dispatcher collaborators and asserts on the events the plugin
/// itself published.
/// </summary>
public sealed class QuotaResetNotifierTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-20T10:00:00Z");
    private static readonly DateTimeOffset OptimalUntil = DateTimeOffset.Parse("2026-08-01T00:00:00Z");

    [Fact]
    public async Task OptimalTransition_FiresExactlyOneWebhookWithExpectedPayload()
    {
        var clock = new FakeClock(Now);
        var advisor = new StubAdvisor(_ => SpendAdvice("codex", Now, OptimalUntil));
        var dispatcher = new StubDispatcher();
        await using var plugin = await BuildPluginAsync(advisor, dispatcher, clock, creditCount: 2);

        await plugin.SampleOnceAsync(CancellationToken.None);

        var evt = Assert.Single(dispatcher.Events);
        Assert.Equal(QuotaResetOptimalEvents.ResetOptimal, evt.Event);
        Assert.Equal("quota.reset_optimal", evt.Event);
        Assert.Null(EventSchema.ValidateEnvelope(evt));

        var details = Assert.IsType<QuotaResetOptimalDetails>(evt.Details);
        Assert.Equal("codex", details.Agent);
        Assert.Equal(nameof(ResetAdviceReason.SpendBeforeDeadline), details.Reason);
        Assert.Equal(2, details.BankedCredits);
        Assert.Equal(OptimalUntil, details.OptimalUntil);
        Assert.Equal(Now, details.OptimalFrom);
        Assert.Equal(OptimalUntil, details.DecisionDeadline);
        Assert.Equal(OptimalUntil, details.NextCreditExpiresAt);
    }

    [Fact]
    public async Task NonOptimalCycle_FiresNoWebhook()
    {
        var clock = new FakeClock(Now);
        var advisor = new StubAdvisor(_ => HoldAdvice("codex", Now));
        var dispatcher = new StubDispatcher();
        await using var plugin = await BuildPluginAsync(advisor, dispatcher, clock, creditCount: 2);

        await plugin.SampleOnceAsync(CancellationToken.None);

        Assert.Empty(dispatcher.Events);
    }

    [Fact]
    public async Task Cooldown_SuppressesRepeatForSameWindow()
    {
        var clock = new FakeClock(Now);
        var advisor = new StubAdvisor(_ => SpendAdvice("codex", clock.GetUtcNow(), OptimalUntil));
        var dispatcher = new StubDispatcher();
        await using var plugin = await BuildPluginAsync(advisor, dispatcher, clock, creditCount: 1);

        await plugin.SampleOnceAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(15));
        await plugin.SampleOnceAsync(CancellationToken.None);

        Assert.Single(dispatcher.Events);
    }

    [Fact]
    public async Task NewWindow_AfterCooldown_FiresAgain()
    {
        var clock = new FakeClock(Now);
        var closesAt = OptimalUntil;
        var advisor = new StubAdvisor(_ => SpendAdvice("codex", clock.GetUtcNow(), closesAt));
        var dispatcher = new StubDispatcher();
        await using var plugin = await BuildPluginAsync(advisor, dispatcher, clock, creditCount: 1);

        await plugin.SampleOnceAsync(CancellationToken.None);

        clock.Advance(TimeSpan.FromHours(25));
        closesAt = OptimalUntil + TimeSpan.FromDays(7);
        await plugin.SampleOnceAsync(CancellationToken.None);

        Assert.Equal(2, dispatcher.Events.Count);
        Assert.Equal(closesAt, Assert.IsType<QuotaResetOptimalDetails>(dispatcher.Events[1].Details).OptimalUntil);
    }

    [Fact]
    public async Task NewWindow_WithinCooldown_IsSuppressed()
    {
        var clock = new FakeClock(Now);
        var closesAt = OptimalUntil;
        var advisor = new StubAdvisor(_ => SpendAdvice("codex", clock.GetUtcNow(), closesAt));
        var dispatcher = new StubDispatcher();
        await using var plugin = await BuildPluginAsync(advisor, dispatcher, clock, creditCount: 1);

        await plugin.SampleOnceAsync(CancellationToken.None);

        clock.Advance(TimeSpan.FromHours(1));
        closesAt = OptimalUntil + TimeSpan.FromDays(7);
        await plugin.SampleOnceAsync(CancellationToken.None);

        Assert.Single(dispatcher.Events);
    }

    [Fact]
    public async Task HoldAfterOptimal_DoesNotRepingSameWindow()
    {
        var clock = new FakeClock(Now);
        var spend = true;
        var advisor = new StubAdvisor(_ => spend
            ? SpendAdvice("codex", clock.GetUtcNow(), OptimalUntil)
            : HoldAdvice("codex", clock.GetUtcNow()));
        var dispatcher = new StubDispatcher();
        await using var plugin = await BuildPluginAsync(advisor, dispatcher, clock, creditCount: 1);

        await plugin.SampleOnceAsync(CancellationToken.None);
        spend = false;
        clock.Advance(TimeSpan.FromMinutes(15));
        await plugin.SampleOnceAsync(CancellationToken.None);
        spend = true;
        clock.Advance(TimeSpan.FromMinutes(15));
        await plugin.SampleOnceAsync(CancellationToken.None);

        Assert.Single(dispatcher.Events);
    }

    [Fact]
    public async Task MissingAdvisor_SkipsTickWithoutThrowing()
    {
        var clock = new FakeClock(Now);
        var dispatcher = new StubDispatcher();
        var configRoot = BuildConfig(null);
        await using var plugin = new QuotaResetNotifierPlugin(
            advisors: [],
            dispatcher: dispatcher,
            configuration: configRoot,
            estimators: null,
            timeProvider: clock);
        await plugin.InitializeAsync(BuildPluginContext(configRoot));

        await plugin.SampleOnceAsync(CancellationToken.None);

        Assert.Empty(dispatcher.Events);
    }

    [Fact]
    public async Task DisabledPlugin_FiresNothing()
    {
        var clock = new FakeClock(Now);
        var advisor = new StubAdvisor(_ => SpendAdvice("codex", Now, OptimalUntil));
        var dispatcher = new StubDispatcher();
        await using var plugin = await BuildPluginAsync(
            advisor, dispatcher, clock, creditCount: 1,
            extraConfig: new Dictionary<string, string?> { ["Enabled"] = "false" });

        Assert.False(plugin.Enabled);
        await plugin.SampleOnceAsync(CancellationToken.None);

        Assert.Empty(dispatcher.Events);
    }

    [Fact]
    public async Task EmptyAgents_WatchesNone()
    {
        var clock = new FakeClock(Now);
        var advisor = new StubAdvisor(_ => SpendAdvice("codex", Now, OptimalUntil));
        var dispatcher = new StubDispatcher();
        // A present-but-empty Agents section is an explicit "watch none".
        await using var plugin = await BuildPluginAsync(
            advisor, dispatcher, clock, creditCount: 1,
            extraConfig: new Dictionary<string, string?> { ["Agents:0"] = "   " });

        await plugin.SampleOnceAsync(CancellationToken.None);

        Assert.Empty(dispatcher.Events);
        Assert.Equal(0, advisor.Calls);
    }

    [Fact]
    public void Options_Defaults_WatchCodexWithCooldown()
    {
        var configRoot = BuildConfig(null);
        var options = QuotaResetNotifierOptions.FromConfiguration(
            configRoot.GetSection($"CodeyBox:Plugins:{QuotaResetNotifierPlugin.PluginId}"));

        Assert.True(options.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(15), options.Interval);
        Assert.Equal(new[] { "codex" }, options.Agents);
        Assert.Equal(TimeSpan.FromHours(24), options.Cooldown);
    }

    [Fact]
    public void Options_BindsAgentsIntervalAndCooldown()
    {
        var configRoot = BuildConfig(new Dictionary<string, string?>
        {
            ["IntervalSeconds"] = "60",
            ["Agents:0"] = "codex",
            ["Agents:1"] = "claude",
            ["CooldownSeconds"] = "3600",
        });
        var options = QuotaResetNotifierOptions.FromConfiguration(
            configRoot.GetSection($"CodeyBox:Plugins:{QuotaResetNotifierPlugin.PluginId}"));

        Assert.Equal(TimeSpan.FromSeconds(60), options.Interval);
        Assert.Equal(new[] { "codex", "claude" }, options.Agents);
        Assert.Equal(TimeSpan.FromHours(1), options.Cooldown);
    }

    [Fact]
    public void NotifyPolicy_HoldAdvice_NeverNotifies()
    {
        var advice = HoldAdvice("codex", Now);

        Assert.False(ResetOptimalNotifyPolicy.ShouldNotify(advice, null, Now, TimeSpan.FromHours(24)));
    }

    [Fact]
    public void NotifyPolicy_SpendWithoutWindow_NeverNotifies()
    {
        var advice = new ResetSpendAdvice
        {
            Agent = "codex",
            EvaluatedAt = Now,
            ShouldSpend = true,
            Reason = ResetAdviceReason.SpendBeforeDeadline,
            Rationale = "windowless",
        };

        Assert.False(ResetOptimalNotifyPolicy.ShouldNotify(advice, null, Now, TimeSpan.Zero));
    }

    [Fact]
    public void Payload_FromHoldAdvice_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => QuotaResetOptimalDetails.FromAdvice(HoldAdvice("codex", Now), 1));
    }

    [Fact]
    public void Payload_MissingCreditCount_IsNullNotZero()
    {
        var details = QuotaResetOptimalDetails.FromAdvice(SpendAdvice("codex", Now, OptimalUntil), null);

        Assert.Null(details.BankedCredits);
    }

    private static ResetSpendAdvice SpendAdvice(string agent, DateTimeOffset now, DateTimeOffset closesAt)
        => new()
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

    private static ResetSpendAdvice HoldAdvice(string agent, DateTimeOffset now)
        => new()
        {
            Agent = agent,
            EvaluatedAt = now,
            ShouldSpend = false,
            Reason = ResetAdviceReason.BurnFirst,
            Rationale = "test hold",
            UsableQuotaPct = 42,
        };

    private async Task<QuotaResetNotifierPlugin> BuildPluginAsync(
        StubAdvisor advisor,
        StubDispatcher dispatcher,
        TimeProvider clock,
        int creditCount,
        IReadOnlyDictionary<string, string?>? extraConfig = null)
    {
        var configRoot = BuildConfig(extraConfig);
        var estimator = new StubEstimator(creditCount);
        var plugin = new QuotaResetNotifierPlugin(
            advisors: [advisor],
            dispatcher: dispatcher,
            configuration: configRoot,
            estimators: [estimator],
            timeProvider: clock);
        await plugin.InitializeAsync(BuildPluginContext(configRoot));
        return plugin;
    }

    private IConfigurationRoot BuildConfig(
        IReadOnlyDictionary<string, string?>? extraConfig)
    {
        var prefix = $"CodeyBox:Plugins:{QuotaResetNotifierPlugin.PluginId}";
        var settings = new Dictionary<string, string?>();
        if (extraConfig is not null)
        {
            foreach (var (key, value) in extraConfig)
                settings[$"{prefix}:{key}"] = value;
        }
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    private static PluginContext BuildPluginContext(IConfiguration configRoot)
    {
        var host = new TestPluginHost(configRoot.GetSection($"CodeyBox:Plugins:{QuotaResetNotifierPlugin.PluginId}"));
        return new PluginContext(
            HostApiVersion: CodeyBoxApiVersion.Current,
            PluginId: QuotaResetNotifierPlugin.PluginId,
            PluginDisplayName: "CodeyBox: Quota Reset Notifier",
            Host: host);
    }

    private sealed class StubAdvisor : IResetOptimalityAdvisor
    {
        private readonly Func<ResetAdviceRequest, ResetSpendAdvice> _next;
        public StubAdvisor(Func<ResetAdviceRequest, ResetSpendAdvice> next) => _next = next;
        public int Calls { get; private set; }
        public Task<ResetSpendAdvice> AdviseAsync(ResetAdviceRequest request, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(_next(request));
        }
    }

    private sealed class StubEstimator : IResetCreditExpiryEstimator
    {
        private readonly int _count;
        public StubEstimator(int count) => _count = count;
        public Task<ResetCreditExpiryReport> EstimateAsync(ResetCreditExpiryQuery query, CancellationToken ct = default)
        {
            var credits = Enumerable.Range(0, _count)
                .Select(i => new BankedResetCredit
                {
                    GrantedAt = Now.AddDays(-30 + i),
                    ExpiresAt = Now.AddDays(i),
                    AdvisedSpendByAt = Now.AddDays(i) - TimeSpan.FromHours(24),
                    IsEstimated = false,
                })
                .ToList();
            return Task.FromResult(new ResetCreditExpiryReport { Credits = credits });
        }
    }

    private sealed class StubDispatcher : IWebhookDispatcher
    {
        public List<WebhookEvent> Events { get; } = new();
        public Task PublishAsync(WebhookEvent evt, CancellationToken ct)
        {
            Events.Add(evt);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeClock(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }

    private sealed class TestPluginHost : IPluginHost
    {
        public TestPluginHost(IConfigurationSection scoped) => ScopedConfig = scoped;
        public Microsoft.Extensions.Logging.ILogger Logger { get; } = NullLogger.Instance;
        public IConfigurationSection ScopedConfig { get; }
    }
}
