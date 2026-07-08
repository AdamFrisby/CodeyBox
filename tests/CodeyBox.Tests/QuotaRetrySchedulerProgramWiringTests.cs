using System.Reflection;
using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CodeyBox.Tests;

public sealed class QuotaRetrySchedulerProgramWiringTests
{
    [Fact]
    public void ProgramDefaultsTransientRetryToEnabledWhenConfigSectionIsOmitted()
    {
        using var factory = new DefaultTransientRetryWiringFactory();

        var cbOptions = factory.Services.GetRequiredService<IOptionsMonitor<CodeyBoxOptions>>().CurrentValue;
        Assert.True(cbOptions.AutoRetryOnTransientFailure.Enabled);

        var orchestratorOptions = factory.Services.GetRequiredService<OrchestratorOptions>();
        Assert.True(orchestratorOptions.AutoRetryOnTransientFailure.Enabled);

        var scheduler = factory.Services.GetRequiredService<TransientRetryScheduler>();
        var accessor = Assert.IsType<Func<AutoRetryOnTransientFailureOptions>>(
            typeof(TransientRetryScheduler)
                .GetField("_transientRetryOptionsAccessor", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(scheduler));

        Assert.True(accessor().Enabled);
    }

    [Fact]
    public void ProgramWiresQuotaRetrySchedulerLiveOptionsAccessorAndAvailabilitySignal()
    {
        var monitor = new MutableOptionsMonitor<CodeyBoxOptions>(OptionsWithQuotaRetry(
            enabled: true,
            interval: "00:00:07",
            margin: "00:00:03",
            maxRetries: 9));
        using var factory = new QuotaRetrySchedulerWiringFactory(monitor);

        var scheduler = factory.Services.GetRequiredService<QuotaRetryScheduler>();
        Assert.IsType<WorkItemAutoRetryScheduler>(
            factory.Services.GetRequiredService<IWorkItemAutoRetryScheduler>());
        Assert.Same(
            scheduler,
            factory.Services.GetRequiredService<IQuotaRetryDispatchPromoter>());
        var orchestratorPromoter = typeof(OrchestratorService)
            .GetField("_quotaRetryDispatchPromoter", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(factory.Services.GetRequiredService<OrchestratorService>());
        Assert.Same(scheduler, orchestratorPromoter);
        var accessor = Assert.IsType<Func<AutoRetryOnQuotaFailureOptions>>(
            typeof(QuotaRetryScheduler)
                .GetField("_autoRetryOptionsAccessor", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(scheduler));

        var current = accessor();
        Assert.True(current.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(7), current.PeriodicCheckInterval);
        Assert.Equal(TimeSpan.FromSeconds(3), current.ClockDriftSafetyMargin);
        Assert.Equal(9, current.MaxAutoRetriesPerWorkItem);

        monitor.Set(OptionsWithQuotaRetry(
            enabled: true,
            interval: "00:00:13",
            margin: "00:00:05",
            maxRetries: 4));
        var reloaded = accessor();
        Assert.Equal(TimeSpan.FromSeconds(13), reloaded.PeriodicCheckInterval);
        Assert.Equal(TimeSpan.FromSeconds(5), reloaded.ClockDriftSafetyMargin);
        Assert.Equal(4, reloaded.MaxAutoRetriesPerWorkItem);

        var wiredSignal = typeof(QuotaRetryScheduler)
            .GetField("_quotaAvailabilitySignal", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(scheduler);
        Assert.Same(factory.Services.GetRequiredService<IAgentQuotaAvailabilitySignal>(), wiredSignal);

        var transientScheduler = factory.Services.GetRequiredService<TransientRetryScheduler>();
        var transientAccessor = Assert.IsType<Func<AutoRetryOnTransientFailureOptions>>(
            typeof(TransientRetryScheduler)
                .GetField("_transientRetryOptionsAccessor", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(transientScheduler));
        var transient = transientAccessor();
        Assert.True(transient.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(45), transient.BaseDelay);
        Assert.Equal(2.5, transient.Multiplier);
        Assert.Equal(TimeSpan.FromMinutes(10), transient.MaxDelay);
        Assert.Equal(6, transient.MaxAutoRetriesPerWorkItem);
        Assert.Equal(TimeSpan.FromMinutes(40), transient.MaxElapsedTime);
        Assert.Equal(TransientRetryJitterMode.Decorrelated, transient.JitterMode);

        monitor.Set(OptionsWithQuotaRetry(
            enabled: true,
            interval: "00:00:13",
            margin: "00:00:05",
            maxRetries: 4,
            transientBaseDelay: "00:01:10",
            transientMultiplier: 3.0,
            transientMaxDelay: "00:12:00",
            transientMaxRetries: 8,
            transientMaxElapsed: "00:50:00",
            transientJitterMode: "Full"));
        var reloadedTransient = transientAccessor();
        Assert.Equal(TimeSpan.FromSeconds(70), reloadedTransient.BaseDelay);
        Assert.Equal(3.0, reloadedTransient.Multiplier);
        Assert.Equal(TimeSpan.FromMinutes(12), reloadedTransient.MaxDelay);
        Assert.Equal(8, reloadedTransient.MaxAutoRetriesPerWorkItem);
        Assert.Equal(TimeSpan.FromMinutes(50), reloadedTransient.MaxElapsedTime);
        Assert.Equal(TransientRetryJitterMode.Full, reloadedTransient.JitterMode);
    }

    [Fact]
    public async Task ProgramWiredTransientRetryOptionsHotReloadAffectsSchedulingBehavior()
    {
        var monitor = new MutableOptionsMonitor<CodeyBoxOptions>(OptionsWithQuotaRetry(
            enabled: true,
            interval: "00:00:07",
            margin: "00:00:03",
            maxRetries: 9,
            transientBaseDelay: "00:00:45",
            transientMultiplier: 2.5,
            transientMaxDelay: "00:10:00",
            transientMaxRetries: 6,
            transientMaxElapsed: "00:40:00",
            transientJitterMode: "None"));
        using var factory = new QuotaRetrySchedulerWiringFactory(monitor);

        var scheduler = factory.Services.GetRequiredService<IWorkItemAutoRetryScheduler>();
        var store = factory.Services.GetRequiredService<IWorkItemStore>();
        var item = NewTransientRetryItem() with { TransientRetryAttempts = 2 };
        await store.CreateAsync(item);

        monitor.Set(OptionsWithQuotaRetry(
            enabled: true,
            interval: "00:00:07",
            margin: "00:00:03",
            maxRetries: 9,
            transientBaseDelay: "00:01:10",
            transientMultiplier: 3.0,
            transientMaxDelay: "00:02:00",
            transientMaxRetries: 8,
            transientMaxElapsed: "00:50:00",
            transientJitterMode: "None"));

        var result = await scheduler.NotifyTransientFailureAsync(item);

        var stored = await store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(WorkItemAutoRetryScheduleStatus.Scheduled, result.Status);
        Assert.Equal(TimeSpan.FromMinutes(2), stored!.NextTransientRetryAt - stored.TransientRetryFirstFailedAt);
    }

    [Fact]
    public async Task ProgramWiredQuotaRecoverySignalRequeuesWaitingItemBeforePeriodicPoll()
    {
        using var factory = new QuotaRecoverySignalWiringFactory();

        var scheduler = factory.Services.GetRequiredService<QuotaRetryScheduler>();
        var wiredSignal = typeof(QuotaRetryScheduler)
            .GetField("_quotaAvailabilitySignal", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(scheduler);
        Assert.Same(factory.Services.GetRequiredService<IAgentQuotaAvailabilitySignal>(), wiredSignal);
        Assert.Same(
            factory.Services.GetRequiredService<IAgentQuotaAvailabilitySignal>(),
            factory.Services.GetRequiredService<IAgentQuotaAvailabilityPublisher>());
        Assert.Same(
            factory.Services.GetRequiredService<IAgentQuotaAvailabilitySignal>(),
            factory.Services.GetRequiredService<IAgentQuotaAvailabilityObservationSource>());

        var store = factory.Services.GetRequiredService<IWorkItemStore>();
        var project = await factory.Services
            .GetRequiredService<IProjectRepository>()
            .GetAsync(new ProjectId("quota-signal-project"), CancellationToken.None);
        Assert.NotNull(project);

        var parked = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = project!.Id,
            Title = "parked",
            Prompt = "do thing",
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            QuotaRetryAttempts = 0,
            AgentClassId = "quota-signal-class",
            NextQuotaRetryAt = DateTimeOffset.UtcNow.AddDays(7),
        };
        await store.CreateAsync(parked);

        var router = factory.Services.GetRequiredService<AgentClassRouter>();
        var member = router.GetClassMembers("quota-signal-class")[0];
        var monitor = factory.Services.GetRequiredService<AgentQuotaRecoveryProbeMonitor>();
        router.MarkExhausted(member, TimeSpan.FromHours(6), DateTimeOffset.UtcNow.AddDays(7));
        factory.Probe.AvailablePct = 90;
        Assert.Equal(1, await monitor.ProbeTrackedMembersOnceAsync(CancellationToken.None));

        var retried = await WaitForQuotaRetryAttemptAsync(store, parked.Id, TimeSpan.FromSeconds(5));
        Assert.Equal(WorkItemState.Queued, retried.State);
        Assert.Equal(1, retried.QuotaRetryAttempts);
    }

    [Fact]
    public async Task ProgramWiredQuotaRecoveryHostedLoopRequeuesWaitingItemBeforePeriodicPoll()
    {
        using var factory = new QuotaRecoverySignalWiringFactory(startHostedServices: true);

        var monitor = factory.Services.GetRequiredService<AgentQuotaRecoveryProbeMonitor>();
        Assert.Contains(
            factory.Services.GetServices<IHostedService>(),
            hosted => ReferenceEquals(hosted, monitor));

        var store = factory.Services.GetRequiredService<IWorkItemStore>();
        var project = await factory.Services
            .GetRequiredService<IProjectRepository>()
            .GetAsync(new ProjectId("quota-signal-project"), CancellationToken.None);
        Assert.NotNull(project);

        var parked = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = project!.Id,
            Title = "parked",
            Prompt = "do thing",
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            QuotaRetryAttempts = 0,
            AgentClassId = "quota-signal-class",
            NextQuotaRetryAt = DateTimeOffset.UtcNow.AddDays(7),
        };
        await store.CreateAsync(parked);

        var router = factory.Services.GetRequiredService<AgentClassRouter>();
        var member = router.GetClassMembers("quota-signal-class")[0];
        router.MarkExhausted(member, TimeSpan.FromHours(6), DateTimeOffset.UtcNow.AddDays(7));
        factory.Probe.AvailablePct = 90;

        var retried = await WaitForQuotaRetryAttemptAsync(store, parked.Id, TimeSpan.FromSeconds(10));
        Assert.Equal(WorkItemState.Queued, retried.State);
        Assert.Equal(1, retried.QuotaRetryAttempts);
    }

    private static CodeyBoxOptions OptionsWithQuotaRetry(
        bool enabled,
        string interval,
        string margin,
        int maxRetries,
        string transientBaseDelay = "00:00:45",
        double transientMultiplier = 2.5,
        string transientMaxDelay = "00:10:00",
        int transientMaxRetries = 6,
        string transientMaxElapsed = "00:40:00",
        string transientJitterMode = "Decorrelated")
        => new()
        {
            AutoRetryOnQuotaFailure = new AutoRetryOnQuotaFailureConfig
            {
                Enabled = enabled,
                PeriodicCheckInterval = interval,
                ClockDriftSafetyMargin = margin,
                MaxAutoRetriesPerWorkItem = maxRetries,
            },
            AutoRetryOnTransientFailure = new AutoRetryOnTransientFailureConfig
            {
                Enabled = true,
                PeriodicCheckInterval = "00:00:11",
                BaseDelay = transientBaseDelay,
                Multiplier = transientMultiplier,
                MaxDelay = transientMaxDelay,
                MaxAutoRetriesPerWorkItem = transientMaxRetries,
                MaxElapsedTime = transientMaxElapsed,
                JitterMode = transientJitterMode,
            },
        };

    private static WorkItem NewTransientRetryItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("transient-wiring"),
        Title = "Transient retry wiring",
        Prompt = "retry after transient transport failure",
        State = WorkItemState.WaitingForTransientRetry,
        LastError = "Agent claude reported transient transport failure",
        FailureKind = "transient",
        PushUpstream = false,
    };

    private sealed class QuotaRetrySchedulerWiringFactory : WebApplicationFactory<Program>
    {
        private readonly MutableOptionsMonitor<CodeyBoxOptions> _monitor;
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(), $"codeybox-quota-retry-wiring-{Guid.NewGuid():N}.db");

        public QuotaRetrySchedulerWiringFactory(MutableOptionsMonitor<CodeyBoxOptions> monitor)
            => _monitor = monitor;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.Sources.Clear();
                var tmp = Path.GetTempPath();
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CodeyBox:DangerouslyDisableAuth"] = "true",
                    ["CodeyBox:StateDatabasePath"] = _dbPath,
                    ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"test-git-{Guid.NewGuid():N}"),
                    ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"test-log-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"test-audit-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AgentStreams:Path"] = Path.Combine(tmp, $"test-agent-streams-{Guid.NewGuid():N}"),
                    ["CodeyBox:AutoRetryOnQuotaFailure:Enabled"] = "false",
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IOptionsMonitor<CodeyBoxOptions>>();
                services.AddSingleton<IOptionsMonitor<CodeyBoxOptions>>(_monitor);
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                try { File.Delete(_dbPath); } catch { /* best-effort */ }
            base.Dispose(disposing);
        }
    }

    private sealed class QuotaRecoverySignalWiringFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(), $"codeybox-quota-signal-wiring-{Guid.NewGuid():N}.db");
        private readonly bool _startHostedServices;

        public MutableProgramQuotaProbe Probe { get; } = new(AgentKind.Codex, 0);

        public QuotaRecoverySignalWiringFactory(bool startHostedServices = false)
            => _startHostedServices = startHostedServices;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.Sources.Clear();
                var tmp = Path.GetTempPath();
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CodeyBox:DangerouslyDisableAuth"] = "true",
                    ["CodeyBox:Smoke:Enabled"] = "false",
                    ["CodeyBox:StateDatabasePath"] = _dbPath,
                    ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"test-git-{Guid.NewGuid():N}"),
                    ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"test-log-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"test-audit-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AgentStreams:Path"] = Path.Combine(tmp, $"test-agent-streams-{Guid.NewGuid():N}"),
                    ["CodeyBox:AutoRetryOnQuotaFailure:Enabled"] = "true",
                    ["CodeyBox:AutoRetryOnQuotaFailure:PeriodicCheckInterval"] = "06:00:00",
                    ["CodeyBox:AutoRetryOnQuotaFailure:MaxAutoRetriesPerWorkItem"] = "3",
                    ["CodeyBox:QuotaRouter:MinQuotaPct"] = "10",
                    ["CodeyBox:QuotaRouter:QuotaRecoveryProbeIntervalSeconds"] = "1",
                    ["CodeyBox:AgentClasses:0:Id"] = "quota-signal-class",
                    ["CodeyBox:AgentClasses:0:DisplayName"] = "Quota Signal",
                    ["CodeyBox:AgentClasses:0:Members:0:Agent"] = "codex",
                    ["CodeyBox:AgentClasses:0:Members:0:Billing"] = "Subscription",
                    ["CodeyBox:AgentClasses:0:Members:0:QualityScore"] = "100",
                    ["CodeyBox:Projects:0:Id"] = "quota-signal-project",
                    ["CodeyBox:Projects:0:DisplayName"] = "Quota Signal Project",
                    ["CodeyBox:Projects:0:RepositoryUrl"] = "https://example.invalid/repo.git",
                    ["CodeyBox:Projects:0:DefaultAgentClass"] = "quota-signal-class",
                });
            });
            builder.ConfigureTestServices(services =>
            {
                if (!_startHostedServices)
                    services.RemoveAll<IHostedService>();
                services.RemoveAll<IAgentQuotaProbe>();
                services.AddSingleton(Probe);
                services.AddSingleton<IAgentQuotaProbe>(sp => sp.GetRequiredService<MutableProgramQuotaProbe>());
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                try { File.Delete(_dbPath); } catch { /* best-effort */ }
            base.Dispose(disposing);
        }
    }

    private sealed class DefaultTransientRetryWiringFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(), $"codeybox-transient-default-wiring-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.Sources.Clear();
                var tmp = Path.GetTempPath();
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CodeyBox:DangerouslyDisableAuth"] = "true",
                    ["CodeyBox:StateDatabasePath"] = _dbPath,
                    ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"test-git-{Guid.NewGuid():N}"),
                    ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"test-log-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"test-audit-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AgentStreams:Path"] = Path.Combine(tmp, $"test-agent-streams-{Guid.NewGuid():N}"),
                    ["CodeyBox:AutoRetryOnQuotaFailure:Enabled"] = "false",
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                try { File.Delete(_dbPath); } catch { /* best-effort */ }
            base.Dispose(disposing);
        }
    }

    private sealed class MutableOptionsMonitor<T> : IOptionsMonitor<T>
    {
        private T _value;

        public MutableOptionsMonitor(T initial) => _value = initial;

        public T CurrentValue => _value;
        public T Get(string? name) => _value;
        public IDisposable OnChange(Action<T, string?> listener) => new NoopDisposable();
        public void Set(T value) => _value = value;

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }

    private sealed class MutableProgramQuotaProbe : IAgentQuotaProbe
    {
        public MutableProgramQuotaProbe(AgentKind kind, double availablePct)
        {
            Kind = kind;
            AvailablePct = availablePct;
        }

        public AgentKind Kind { get; }
        public double AvailablePct { get; set; }

        public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
            => Task.FromResult(new AgentQuotaSnapshot { AvailablePct = AvailablePct });
    }

    private static async Task<WorkItem> WaitForQuotaRetryAttemptAsync(
        IWorkItemStore store,
        WorkItemId id,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        WorkItem? latest = null;
        while (DateTime.UtcNow < deadline)
        {
            latest = await store.GetAsync(id);
            if (latest?.QuotaRetryAttempts > 0)
                return latest;

            await Task.Delay(25);
        }

        latest = await store.GetAsync(id);
        Assert.Fail($"Timed out waiting for quota retry attempt; latest state={latest?.State}, attempts={latest?.QuotaRetryAttempts}");
        throw new InvalidOperationException("unreachable");
    }
}
