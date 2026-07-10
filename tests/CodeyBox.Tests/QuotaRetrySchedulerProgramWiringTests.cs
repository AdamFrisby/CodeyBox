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

[Collection("GlobalSerilog")]
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
            maxRetries: 9,
            quotaSweepBatchSize: 11));
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
        Assert.Equal(11, current.MaxWaitingForQuotaResetSweepBatchSize);

        monitor.Set(OptionsWithQuotaRetry(
            enabled: true,
            interval: "00:00:13",
            margin: "00:00:05",
            maxRetries: 4,
            quotaSweepBatchSize: 12));
        var reloaded = accessor();
        Assert.Equal(TimeSpan.FromSeconds(13), reloaded.PeriodicCheckInterval);
        Assert.Equal(TimeSpan.FromSeconds(5), reloaded.ClockDriftSafetyMargin);
        Assert.Equal(4, reloaded.MaxAutoRetriesPerWorkItem);
        Assert.Equal(12, reloaded.MaxWaitingForQuotaResetSweepBatchSize);

        var wiredSignal = typeof(QuotaRetryScheduler)
            .GetField("_quotaAvailabilitySignal", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(scheduler);
        Assert.Same(factory.Services.GetRequiredService<IAgentQuotaAvailabilitySignal>(), wiredSignal);
        var wiredAgentAvailabilitySignal = typeof(QuotaRetryScheduler)
            .GetField("_agentAvailabilityRecoverySignal", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(scheduler);
        Assert.Same(factory.Services.GetRequiredService<IAgentAvailabilityRecoverySignal>(), wiredAgentAvailabilitySignal);

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
        var wiredAgentAvailabilitySignal = typeof(QuotaRetryScheduler)
            .GetField("_agentAvailabilityRecoverySignal", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(scheduler);
        Assert.Same(factory.Services.GetRequiredService<IAgentAvailabilityRecoverySignal>(), wiredAgentAvailabilitySignal);
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

    [Fact]
    public async Task ProgramWiredCursorTokenUpdatedClearsLastKnownGoodWrapper()
    {
        var root = Directory.CreateTempSubdirectory("codeybox-cursor-wrapper-").FullName;
        var previousCursorAuthFile = Environment.GetEnvironmentVariable("CODEYBOX_CURSOR_AUTH_FILE");
        try
        {
            var cursorAuthPath = Path.Combine(root, "cursor-auth.json");
            File.WriteAllText(cursorAuthPath, """{"accessToken":"same-token","refresh":"r1"}""");
            Environment.SetEnvironmentVariable("CODEYBOX_CURSOR_AUTH_FILE", cursorAuthPath);
            using var factory = new CursorWrapperInvalidationFactory(root, cursorAuthPath);
            var probe = factory.Services
                .GetServices<IAgentQuotaProbe>()
                .Single(p => p.Kind == AgentKind.Cursor);
            Assert.IsType<LastKnownGoodQuotaProbe>(probe);
            var member = new AgentMembership
            {
                Agent = AgentKind.Cursor,
                Billing = AgentBilling.Subscription,
                ModelId = "composer-2.5",
                QualityScore = 98,
            };

            var first = await probe.GetAvailabilityAsync(member, CancellationToken.None);
            Assert.True(first.IsKnown, $"{first.Unknown}: {first.Notes}; calls={factory.Handler.CallCount}");
            Assert.Equal(70.0, first.AvailablePct);
            Assert.Equal(1, factory.Handler.CallCount);

            factory.Handler.ThrowTransient = true;
            File.WriteAllText(cursorAuthPath, """{"accessToken":"same-token","refresh":"changed-r2"}""");
            File.SetLastWriteTimeUtc(cursorAuthPath, DateTime.UtcNow.AddSeconds(1));
            var source = factory.Services.GetRequiredService<CursorCredentialFileSource>();
            Assert.Contains("changed-r2", source.GetRaw());

            var second = await probe.GetAvailabilityAsync(member, CancellationToken.None);

            Assert.Equal(2, factory.Handler.CallCount);
            Assert.False(second.IsKnown);
            Assert.Equal(QuotaUnknownReason.Transient, second.Unknown);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CURSOR_AUTH_FILE", previousCursorAuthFile);
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static CodeyBoxOptions OptionsWithQuotaRetry(
        bool enabled,
        string interval,
        string margin,
        int maxRetries,
        int quotaSweepBatchSize = AutoRetryOnQuotaFailureOptions.DefaultWaitingForQuotaResetSweepBatchSize,
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
                MaxWaitingForQuotaResetSweepBatchSize = quotaSweepBatchSize,
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
                // This test's subject is the quota-recovery probe monitor's
                // periodic loop (and the QuotaRetryScheduler subscription it
                // drives). Always strip the full hosted-service set so the
                // OrchestratorService dispatcher never runs: it would otherwise
                // pull the just-requeued Queued item and — because the codex CLI
                // is absent in-test — drive it to Failed, racing the assertion
                // under load. When exercising the hosted loop, re-add only the
                // two quota-recovery hosted services the requeue actually needs
                // (the monitor's background probe loop and the scheduler whose
                // constructor subscribes to the recovery signal).
                services.RemoveAll<IHostedService>();
                if (_startHostedServices)
                {
                    services.AddHostedService(sp => sp.GetRequiredService<AgentQuotaRecoveryProbeMonitor>());
                    services.AddHostedService(sp => sp.GetRequiredService<QuotaRetryScheduler>());
                }
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

    private sealed class CursorWrapperInvalidationFactory : WebApplicationFactory<Program>
    {
        private readonly string _root;
        private readonly string _cursorAuthPath;

        public CursorWrapperInvalidationFactory(string root, string cursorAuthPath)
        {
            _root = root;
            _cursorAuthPath = cursorAuthPath;
        }

        public MutableCursorUsageHandler Handler { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.Sources.Clear();
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CodeyBox:DangerouslyDisableAuth"] = "true",
                    ["CodeyBox:Smoke:Enabled"] = "false",
                    [CredentialFileWatcherSettings.ConfigurationKey] = "false",
                    ["CodeyBox:StateDatabasePath"] = Path.Combine(_root, "state.db"),
                    ["CodeyBox:GitRootDirectory"] = Path.Combine(_root, "git"),
                    ["CodeyBox:AuditLog:Path"] = Path.Combine(_root, "logs", "api-.json"),
                    ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(_root, "logs", "audit-.json"),
                    ["CodeyBox:AgentStreams:Path"] = Path.Combine(_root, "agent-streams"),
                    ["CodeyBox:CursorAuthFile"] = _cursorAuthPath,
                    ["CodeyBox:QuotaRouter:ProbeMaxStalenessSeconds"] = "300",
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(
                    new QuotaFakeHttpClientFactory("agent-quota", Handler));
            });
        }
    }

    private sealed class MutableCursorUsageHandler : HttpMessageHandler
    {
        private const string UsageBody = """
        {
          "planUsage": {
            "autoPercentUsed": 25,
            "apiPercentUsed": 10,
            "totalPercentUsed": 30
          },
          "enabled": true,
          "autoBucketModels": ["composer-2.5"]
        }
        """;

        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);
        public bool ThrowTransient { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            if (ThrowTransient)
                throw new HttpRequestException("simulated cursor quota outage");

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(UsageBody),
            });
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
