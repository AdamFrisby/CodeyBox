using System.Reflection;
using CodeyBox.Api;
using CodeyBox.Orchestrator;
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
    public void ProgramWiresQuotaRetrySchedulerLiveOptionsAccessorAndAvailabilitySignal()
    {
        var monitor = new MutableOptionsMonitor<CodeyBoxOptions>(OptionsWithQuotaRetry(
            enabled: true,
            interval: "00:00:07",
            margin: "00:00:03",
            maxRetries: 9));
        using var factory = new QuotaRetrySchedulerWiringFactory(monitor);

        var scheduler = factory.Services.GetRequiredService<QuotaRetryScheduler>();
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

        var transientAccessor = Assert.IsType<Func<AutoRetryOnTransientFailureOptions>>(
            typeof(QuotaRetryScheduler)
                .GetField("_transientRetryOptionsAccessor", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(scheduler));
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
}
