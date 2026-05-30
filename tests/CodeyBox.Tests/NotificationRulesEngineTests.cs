using System.Collections.Concurrent;
using CodeyBox.Core;
using CodeyBox.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeyBox.Tests;

public sealed class NotificationRulesEngineTests
{
    private static NotificationRulesEngine BuildEngine(
        IEnumerable<ICondition> conditions,
        IEnumerable<INotificationBuilder> builders,
        IEnumerable<INotificationProvider> providers,
        List<NotificationRuleOptions> rules)
    {
        var opts = new NotificationsOptions
        {
            Enabled = true,
            Rules = rules,
        };
        var monitor = new StaticOptionsMonitor<NotificationsOptions>(opts);
        return new NotificationRulesEngine(
            monitor,
            conditions,
            builders,
            providers,
            NullLogger<NotificationRulesEngine>.Instance,
            TimeSpan.FromMilliseconds(1));
    }

    private static (CountingProvider provider, NotificationRulesEngine engine) BuildEngineWithCounter(
        ICondition condition,
        INotificationBuilder builder,
        List<NotificationRuleOptions> rules)
    {
        var provider = new CountingProvider();
        var engine = BuildEngine(
            [condition],
            [builder],
            [provider],
            rules);
        return (provider, engine);
    }

    private static async Task PrimeAndSweepAsync(NotificationRulesEngine engine)
    {
        await engine.PrimeInitialStateAsync(CancellationToken.None);
        await engine.RunSweepAsync(CancellationToken.None);
    }

    // ── Edge-triggered: fires once on false→true ──────────────────────────

    [Fact]
    public async Task EdgeTriggered_FiresOnceWhenConditionBecomesTrue()
    {
        var condition = new ToggleCondition("test_cond", initial: false);
        var builder = new StaticBuilder("test_cond", "Test notification");
        var rules = new List<NotificationRuleOptions>
        {
            new() { Condition = "test_cond", Providers = ["counting"] },
        };
        var (provider, engine) = BuildEngineWithCounter(condition, builder, rules);

        // Prime: condition is false, captures initial state.
        await engine.PrimeInitialStateAsync(CancellationToken.None);

        // Set condition true, sweep: edge transition fires.
        condition.Set(true);
        await engine.RunSweepAsync(CancellationToken.None);
        Assert.Equal(1, provider.CallCount);
        Assert.Equal("Test notification", provider.Notifications.First().Title);

        // Second sweep: still true, no new edge.
        await engine.RunSweepAsync(CancellationToken.None);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task EdgeTriggered_RefiresAfterConditionClearsAndRetriggers()
    {
        var condition = new ToggleCondition("test_cond", initial: false);
        var builder = new StaticBuilder("test_cond", "Re-fire test");
        var rules = new List<NotificationRuleOptions>
        {
            new() { Condition = "test_cond", Providers = ["counting"] },
        };
        var (provider, engine) = BuildEngineWithCounter(condition, builder, rules);

        await engine.PrimeInitialStateAsync(CancellationToken.None);

        // Fire 1.
        condition.Set(true);
        await engine.RunSweepAsync(CancellationToken.None);
        Assert.Equal(1, provider.CallCount);

        // Clear.
        condition.Set(false);
        await engine.RunSweepAsync(CancellationToken.None);
        Assert.Equal(1, provider.CallCount);

        // Re-trigger.
        condition.Set(true);
        await engine.RunSweepAsync(CancellationToken.None);
        Assert.Equal(2, provider.CallCount);
    }

    // ── Cooldown ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Cooldown_SuppressesRefireWithinWindow()
    {
        var condition = new ToggleCondition("test_cond", initial: false);
        var builder = new StaticBuilder("test_cond", "Cooldown test");
        var rules = new List<NotificationRuleOptions>
        {
            new() { Condition = "test_cond", Providers = ["counting"], Cooldown = "01:00:00" },
        };
        var (provider, engine) = BuildEngineWithCounter(condition, builder, rules);

        await engine.PrimeInitialStateAsync(CancellationToken.None);

        // Fire 1.
        condition.Set(true);
        await engine.RunSweepAsync(CancellationToken.None);
        Assert.Equal(1, provider.CallCount);

        // Clear and immediately re-trigger — cooldown blocks.
        condition.Set(false);
        await engine.RunSweepAsync(CancellationToken.None);
        condition.Set(true);
        await engine.RunSweepAsync(CancellationToken.None);

        // Still only 1 fire.
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task Cooldown_AllowsFireAfterWindowElapses()
    {
        var condition = new ToggleCondition("test_cond", initial: false);
        var builder = new StaticBuilder("test_cond", "Cooldown elapsed");
        var rules = new List<NotificationRuleOptions>
        {
            new() { Condition = "test_cond", Providers = ["counting"], Cooldown = "00:00:00.050" },
        };
        var (provider, engine) = BuildEngineWithCounter(condition, builder, rules);

        await engine.PrimeInitialStateAsync(CancellationToken.None);

        condition.Set(true);
        await engine.RunSweepAsync(CancellationToken.None);
        Assert.Equal(1, provider.CallCount);

        condition.Set(false);
        await engine.RunSweepAsync(CancellationToken.None);

        await Task.Delay(TimeSpan.FromMilliseconds(150));

        condition.Set(true);
        await engine.RunSweepAsync(CancellationToken.None);
        Assert.Equal(2, provider.CallCount);
    }

    // ── Persistently-true: does not re-fire ────────────────────────────────

    [Fact]
    public async Task PersistentlyTrue_DoesNotRefire()
    {
        var condition = new ToggleCondition("test_cond", initial: true);
        var builder = new StaticBuilder("test_cond", "No re-fire");
        var rules = new List<NotificationRuleOptions>
        {
            new() { Condition = "test_cond", Providers = ["counting"] },
        };
        var (provider, engine) = BuildEngineWithCounter(condition, builder, rules);

        // Condition starts true, prime captures that state → no edge.
        await engine.PrimeInitialStateAsync(CancellationToken.None);
        Assert.Equal(0, provider.CallCount);

        // Sweep while still true → no fire.
        await engine.RunSweepAsync(CancellationToken.None);
        Assert.Equal(0, provider.CallCount);
    }

    // ── Multiple providers ─────────────────────────────────────────────────

    [Fact]
    public async Task RoutesToMultipleProviders()
    {
        var condition = new ToggleCondition("test_cond", initial: false);
        var builder = new StaticBuilder("test_cond", "Multi-provider");
        var rules = new List<NotificationRuleOptions>
        {
            new() { Condition = "test_cond", Providers = ["email", "slack"] },
        };

        var emailProvider = new CountingProvider("email");
        var slackProvider = new CountingProvider("slack");
        var opts = new NotificationsOptions { Enabled = true, Rules = rules };
        var engine = new NotificationRulesEngine(
            new StaticOptionsMonitor<NotificationsOptions>(opts),
            [condition],
            [builder],
            [emailProvider, slackProvider],
            NullLogger<NotificationRulesEngine>.Instance);

        await engine.PrimeInitialStateAsync(CancellationToken.None);
        condition.Set(true);
        await engine.RunSweepAsync(CancellationToken.None);

        Assert.Equal(1, emailProvider.CallCount);
        Assert.Equal(1, slackProvider.CallCount);
    }

    // ── Unknown condition skipped ──────────────────────────────────────────

    [Fact]
    public async Task UnknownCondition_SkippedGracefully()
    {
        var condition = new ToggleCondition("known", initial: false);
        var builder = new StaticBuilder("known", "Known");
        var rules = new List<NotificationRuleOptions>
        {
            new() { Condition = "unknown_cond", Providers = ["counting"] },
        };
        var (provider, engine) = BuildEngineWithCounter(condition, builder, rules);

        await engine.PrimeInitialStateAsync(CancellationToken.None);
        condition.Set(true);
        await engine.RunSweepAsync(CancellationToken.None);

        Assert.Equal(0, provider.CallCount);
    }

    // ── Severity override ──────────────────────────────────────────────────

    [Fact]
    public async Task SeverityOverride_AppliedToNotification()
    {
        var condition = new ToggleCondition("test_cond", initial: false);
        var builder = new StaticBuilder("test_cond", "Severity test", NotificationSeverity.Information);
        var rules = new List<NotificationRuleOptions>
        {
            new() { Condition = "test_cond", Providers = ["counting"], Severity = "Critical" },
        };
        var (provider, engine) = BuildEngineWithCounter(condition, builder, rules);

        await engine.PrimeInitialStateAsync(CancellationToken.None);
        condition.Set(true);
        await engine.RunSweepAsync(CancellationToken.None);

        Assert.Equal(1, provider.CallCount);
        Assert.Equal(NotificationSeverity.Critical, provider.Notifications.First().Severity);
    }

    // ── Recipients propagation ──────────────────────────────────────────────

    [Fact]
    public async Task Recipients_PropagatedToNotification()
    {
        var condition = new ToggleCondition("test_cond", initial: false);
        var builder = new StaticBuilder("test_cond", "Recipients test");
        var rules = new List<NotificationRuleOptions>
        {
            new() { Condition = "test_cond", Providers = ["counting"], Recipients = ["alice@test", "bob@test"] },
        };
        var (provider, engine) = BuildEngineWithCounter(condition, builder, rules);

        await engine.PrimeInitialStateAsync(CancellationToken.None);
        condition.Set(true);
        await engine.RunSweepAsync(CancellationToken.None);

        Assert.Equal(1, provider.CallCount);
        Assert.NotNull(provider.Notifications.First().Recipients);
        Assert.Equal(2, provider.Notifications.First().Recipients!.Count);
        Assert.Contains("alice@test", provider.Notifications.First().Recipients!);
        Assert.Contains("bob@test", provider.Notifications.First().Recipients!);
    }

    // ── Disabled / zero-rules short-circuit ─────────────────────────────────

    [Fact]
    public async Task Disabled_ShortCircuitsWithoutEvaluating()
    {
        var condition = new ToggleCondition("test_cond", initial: false);
        var builder = new StaticBuilder("test_cond", "Disabled test");
        var rules = new List<NotificationRuleOptions>
        {
            new() { Condition = "test_cond", Providers = ["counting"] },
        };
        var opts = new NotificationsOptions { Enabled = false, Rules = rules };
        var monitor = new StaticOptionsMonitor<NotificationsOptions>(opts);
        var provider = new CountingProvider();
        var engine = new NotificationRulesEngine(
            monitor, [condition], [builder], [provider],
            NullLogger<NotificationRulesEngine>.Instance);

        condition.Set(true);
        // No prime — go straight to sweep so we don't prime an inactive engine.
        await engine.RunSweepAsync(CancellationToken.None);

        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task ZeroRules_ShortCircuitsWithoutEvaluating()
    {
        var condition = new ToggleCondition("test_cond", initial: false);
        var builder = new StaticBuilder("test_cond", "Zero rules test");
        var opts = new NotificationsOptions { Enabled = true, Rules = [] };
        var monitor = new StaticOptionsMonitor<NotificationsOptions>(opts);
        var provider = new CountingProvider();
        var engine = new NotificationRulesEngine(
            monitor, [condition], [builder], [provider],
            NullLogger<NotificationRulesEngine>.Instance);

        condition.Set(true);
        await engine.RunSweepAsync(CancellationToken.None);

        Assert.Equal(0, provider.CallCount);
    }

    // ── Invalid cooldown format ────────────────────────────────────────────

    [Fact]
    public async Task InvalidCooldown_LogsWarningAndTreatsAsZero()
    {
        var log = new CapturingLogger<NotificationRulesEngine>();
        var condition = new ToggleCondition("test_cond", initial: false);
        var builder = new StaticBuilder("test_cond", "Invalid cooldown test");
        var rules = new List<NotificationRuleOptions>
        {
            new() { Condition = "test_cond", Providers = ["counting"], Cooldown = "banana" },
        };
        var provider = new CountingProvider();
        var opts = new NotificationsOptions { Enabled = true, Rules = rules };
        var engine = new NotificationRulesEngine(
            new StaticOptionsMonitor<NotificationsOptions>(opts),
            [condition], [builder], [provider],
            log);

        await engine.PrimeInitialStateAsync(CancellationToken.None);
        condition.Set(true);
        await engine.RunSweepAsync(CancellationToken.None);

        // Notification fires because Cooldown "banana" is treated as zero.
        Assert.Equal(1, provider.CallCount);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning
            && e.Message.Contains("invalid Cooldown"));
    }

    // ── Throwing condition inside sweep ────────────────────────────────────

    [Fact]
    public async Task ConditionThrows_SkippedAndSweepContinues()
    {
        var log = new CapturingLogger<NotificationRulesEngine>();
        var throwing = new ThrowingCondition("thrower", new InvalidOperationException("bang"));
        var healthy = new ToggleCondition("healthy", initial: false);
        var throwingBuilder = new StaticBuilder("thrower", "Should not fire");
        var healthyBuilder = new StaticBuilder("healthy", "Should fire");
        var rules = new List<NotificationRuleOptions>
        {
            new() { Condition = "thrower", Providers = ["counting"] },
            new() { Condition = "healthy", Providers = ["counting"] },
        };
        var provider = new CountingProvider();
        var opts = new NotificationsOptions { Enabled = true, Rules = rules };
        var engine = new NotificationRulesEngine(
            new StaticOptionsMonitor<NotificationsOptions>(opts),
            [throwing, healthy], [throwingBuilder, healthyBuilder], [provider],
            log);

        await engine.PrimeInitialStateAsync(CancellationToken.None);
        healthy.Set(true);
        await engine.RunSweepAsync(CancellationToken.None);

        // The healthy condition still fires; the throwing condition is skipped.
        Assert.Equal(1, provider.CallCount);
        Assert.Equal("Should fire", provider.Notifications.First().Title);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning
            && e.Message.Contains("failed to evaluate condition"));
    }

    // ── Provider throws, isolation on failure ──────────────────────────────

    [Fact]
    public async Task ProviderThrows_OtherProvidersStillNotified()
    {
        var condition = new ToggleCondition("test_cond", initial: false);
        var builder = new StaticBuilder("test_cond", "Isolation test");
        var rules = new List<NotificationRuleOptions>
        {
            new() { Condition = "test_cond", Providers = ["throwing", "counting"] },
        };

        var throwingProvider = new ThrowingProvider("throwing", new IOException("smtp timeout"));
        var countingProvider = new CountingProvider("counting");
        var opts = new NotificationsOptions { Enabled = true, Rules = rules };
        var engine = new NotificationRulesEngine(
            new StaticOptionsMonitor<NotificationsOptions>(opts),
            [condition],
            [builder],
            [throwingProvider, countingProvider],
            NullLogger<NotificationRulesEngine>.Instance);

        await engine.PrimeInitialStateAsync(CancellationToken.None);
        condition.Set(true);
        await engine.RunSweepAsync(CancellationToken.None);

        // Counting provider still received the notification despite the throwing provider.
        Assert.Equal(1, countingProvider.CallCount);
    }

    // ── Missing builder ────────────────────────────────────────────────────

    [Fact]
    public async Task MissingBuilder_LogsWarningAndSkips()
    {
        var log = new CapturingLogger<NotificationRulesEngine>();
        var condition = new ToggleCondition("orphan_cond", initial: false);
        var provider = new CountingProvider();
        var rules = new List<NotificationRuleOptions>
        {
            new() { Condition = "orphan_cond", Providers = ["counting"] },
        };
        var opts = new NotificationsOptions { Enabled = true, Rules = rules };
        var engine = new NotificationRulesEngine(
            new StaticOptionsMonitor<NotificationsOptions>(opts),
            [condition],
            [], // no builders registered
            [provider],
            log);

        await engine.PrimeInitialStateAsync(CancellationToken.None);
        condition.Set(true);
        await engine.RunSweepAsync(CancellationToken.None);

        Assert.Equal(0, provider.CallCount);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning
            && e.Message.Contains("no builder registered"));
    }

    // ── PrimeInitialStateAsync: throwing condition ─────────────────────────

    [Fact]
    public async Task PrimeInitialState_ThrowingCondition_SetToFalse()
    {
        var log = new CapturingLogger<NotificationRulesEngine>();
        var throwing = new ThrowingCondition("thrower", new InvalidOperationException("prime failed"));
        var healthy = new ToggleCondition("healthy", initial: true);
        var throwingBuilder = new StaticBuilder("thrower", "Throwing");
        var healthyBuilder = new StaticBuilder("healthy", "Healthy");
        var provider = new CountingProvider();
        var rules = new List<NotificationRuleOptions>
        {
            new() { Condition = "thrower", Providers = ["counting"] },
            new() { Condition = "healthy", Providers = ["counting"] },
        };
        var opts = new NotificationsOptions { Enabled = true, Rules = rules };
        var engine = new NotificationRulesEngine(
            new StaticOptionsMonitor<NotificationsOptions>(opts),
            [throwing, healthy], [throwingBuilder, healthyBuilder], [provider],
            log);

        // Prime: throwing condition caught, initial state set to false.
        // Healthy condition starts true → no edge on first sweep.
        await engine.PrimeInitialStateAsync(CancellationToken.None);
        await engine.RunSweepAsync(CancellationToken.None);

        Assert.Equal(0, provider.CallCount);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning
            && e.Message.Contains("failed to evaluate initial state"));
    }

    // ── Unknown-provider branch ─────────────────────────────────────────────

    [Fact]
    public async Task UnknownProvider_LogsWarningAndContinues()
    {
        var log = new CapturingLogger<NotificationRulesEngine>();
        var condition = new ToggleCondition("test_cond", initial: false);
        var builder = new StaticBuilder("test_cond", "Unknown provider test");
        var rules = new List<NotificationRuleOptions>
        {
            new() { Condition = "test_cond", Providers = ["nonexistent"] },
        };
        var provider = new CountingProvider("counting");
        var opts = new NotificationsOptions { Enabled = true, Rules = rules };
        var engine = new NotificationRulesEngine(
            new StaticOptionsMonitor<NotificationsOptions>(opts),
            [condition], [builder], [provider],
            log);

        await engine.PrimeInitialStateAsync(CancellationToken.None);
        condition.Set(true);
        await engine.RunSweepAsync(CancellationToken.None);

        Assert.Equal(0, provider.CallCount);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning
            && e.Message.Contains("provider")
            && e.Message.Contains("not registered"));
    }

    // ── Multiple rules per condition ────────────────────────────────────────

    [Fact]
    public async Task MultipleRulesPerCondition_LogsWarningAndFiresOnce()
    {
        var log = new CapturingLogger<NotificationRulesEngine>();
        var condition = new ToggleCondition("test_cond", initial: false);
        var builder = new StaticBuilder("test_cond", "Multi-rule test");
        var rules = new List<NotificationRuleOptions>
        {
            new() { Condition = "test_cond", Providers = ["counting"], Severity = "Warning" },
            new() { Condition = "test_cond", Providers = ["counting"], Severity = "Critical" },
        };
        var provider = new CountingProvider();
        var opts = new NotificationsOptions { Enabled = true, Rules = rules };
        var engine = new NotificationRulesEngine(
            new StaticOptionsMonitor<NotificationsOptions>(opts),
            [condition],
            [builder],
            [provider],
            log);

        await engine.PrimeInitialStateAsync(CancellationToken.None);
        condition.Set(true);
        await engine.RunSweepAsync(CancellationToken.None);

        Assert.Equal(1, provider.CallCount);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning
            && e.Message.Contains("multiple rules map to condition"));

        // Clear and re-trigger → state was not prematurely cleared.
        condition.Set(false);
        await engine.RunSweepAsync(CancellationToken.None);
        condition.Set(true);
        await engine.RunSweepAsync(CancellationToken.None);

        Assert.Equal(2, provider.CallCount);
    }

    // ── InferConditionId fallback ───────────────────────────────────────────

    [Fact]
    public async Task InferConditionId_UsedWhenBuilderLacksIConditionAwareBuilder()
    {
        // FooBarNotificationBuilder → InferConditionId strips "NotificationBuilder"
        // then CamelToSnake → "foo_bar"
        var condition = new ToggleCondition("foo_bar", initial: false);
        var builder = new FooBarNotificationBuilder();
        var rules = new List<NotificationRuleOptions>
        {
            new() { Condition = "foo_bar", Providers = ["counting"] },
        };
        var provider = new CountingProvider();
        var opts = new NotificationsOptions { Enabled = true, Rules = rules };
        var engine = new NotificationRulesEngine(
            new StaticOptionsMonitor<NotificationsOptions>(opts),
            [condition],
            [builder],
            [provider],
            NullLogger<NotificationRulesEngine>.Instance);

        await engine.PrimeInitialStateAsync(CancellationToken.None);
        condition.Set(true);
        await engine.RunSweepAsync(CancellationToken.None);

        Assert.Equal(1, provider.CallCount);
        Assert.Equal("foo_bar", provider.Notifications.First().ConditionId);
        Assert.Equal("Fallback title", provider.Notifications.First().Title);
    }
}

// ── Test doubles ───────────────────────────────────────────────────────────

public sealed class ToggleCondition : ICondition, IDisposable
{
    private volatile bool _value;

    public string Id { get; }

    public ToggleCondition(string id, bool initial = false)
    {
        Id = id;
        _value = initial;
    }

    public void Set(bool value) => _value = value;

    public Task<bool> EvaluateAsync(CancellationToken ct) => Task.FromResult(_value);

    public void Dispose() { }
}

public sealed class StaticBuilder : INotificationBuilder, IConditionAwareBuilder
{
    private readonly string _conditionId;
    private readonly string _title;
    private readonly NotificationSeverity _severity;

    public string ConditionId => _conditionId;

    public StaticBuilder(string conditionId, string title, NotificationSeverity severity = NotificationSeverity.Information)
    {
        _conditionId = conditionId;
        _title = title;
        _severity = severity;
    }

    public Notification Build(DateTimeOffset evaluatedAt) => new()
    {
        ConditionId = _conditionId,
        Title = _title,
        Severity = _severity,
        Timestamp = evaluatedAt,
    };
}

public sealed class CountingProvider : INotificationProvider
{
    public int CallCount => _notifications.Count;
    public ConcurrentBag<Notification> Notifications => _notifications;
    private readonly ConcurrentBag<Notification> _notifications = new();
    private readonly string _name;

    public string Name => _name;

    public CountingProvider(string name = "counting")
    {
        _name = name;
    }

    public Task SendAsync(Notification notification, CancellationToken ct)
    {
        _notifications.Add(notification);
        return Task.CompletedTask;
    }
}

public sealed class ThrowingCondition : ICondition
{
    private readonly Exception _exception;

    public string Id { get; }

    public ThrowingCondition(string id, Exception exception)
    {
        Id = id;
        _exception = exception;
    }

    public Task<bool> EvaluateAsync(CancellationToken ct) => throw _exception;
}

public sealed class ThrowingProvider : INotificationProvider
{
    private readonly string _name;
    private readonly Exception _exception;

    public string Name => _name;

    public ThrowingProvider(string name, Exception exception)
    {
        _name = name;
        _exception = exception;
    }

    public Task SendAsync(Notification notification, CancellationToken ct) => throw _exception;
}

public sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T> where T : class, new()
{
    public T CurrentValue { get; private set; }

    public StaticOptionsMonitor(T value) { CurrentValue = value; }

    public void Set(T value) { CurrentValue = value; }

    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string> listener) => null;
    public IDisposable? OnChange(Action<T> listener) => null;
}

public sealed class FooBarNotificationBuilder : INotificationBuilder
{
    // Does NOT implement IConditionAwareBuilder — exercises the
    // InferConditionId / CamelToSnake fallback path.

    public Notification Build(DateTimeOffset evaluatedAt) => new()
    {
        ConditionId = "foo_bar",
        Title = "Fallback title",
        Severity = NotificationSeverity.Information,
        Timestamp = evaluatedAt,
    };
}
