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

public sealed class WorkerPoolHealthWatchdogProgramWiringTests
{
    [Fact]
    public void ProgramWiresHostedWatchdogAndLiveOptionsAccessor()
    {
        var monitor = new MutableOptionsMonitor<CodeyBoxOptions>(
            OptionsWithWatchdog(TimeSpan.FromMinutes(3)));
        using var factory = new WorkerPoolHealthWatchdogWiringFactory(monitor);

        var watchdog = factory.Services.GetRequiredService<WorkerPoolHealthWatchdog>();
        Assert.Same(
            watchdog,
            factory.Services.GetServices<IHostedService>().OfType<WorkerPoolHealthWatchdog>().Single());

        var accessor = Assert.IsType<Func<WorkerPoolHealthWatchdogOptions>>(
            typeof(WorkerPoolHealthWatchdog)
                .GetField("_optsAccessor", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(watchdog));

        Assert.Equal(TimeSpan.FromMinutes(3), accessor().StallTimeout);

        var healthSource = Assert.IsType<WorkerPoolHealthCoordinator>(
            factory.Services.GetRequiredService<IWorkerPoolHealthSource>());
        Assert.Same(
            healthSource,
            factory.Services.GetRequiredService<IAgentCapacitySnapshot>());
        Assert.Same(
            healthSource,
            typeof(WorkerPoolHealthWatchdog)
                .GetField("_pool", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(watchdog));

        var quotaRecovery = Assert.IsType<QuotaRetryScheduler>(
            factory.Services.GetRequiredService<IWorkerPoolQuotaRecovery>());
        Assert.Same(
            quotaRecovery,
            typeof(WorkerPoolHealthWatchdog)
                .GetField("_quotaRecovery", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(watchdog));
        Assert.Same(
            factory.Services.GetRequiredService<AgentClassRouter>(),
            factory.Services.GetRequiredService<IAgentRoutingReadiness>());

        monitor.Set(OptionsWithWatchdog(TimeSpan.FromMinutes(7)));

        Assert.Equal(TimeSpan.FromMinutes(7), accessor().StallTimeout);
    }

    private static CodeyBoxOptions OptionsWithWatchdog(TimeSpan stallTimeout)
        => new()
        {
            WorkerPoolHealthWatchdog = new WorkerPoolHealthWatchdogOptions
            {
                StallTimeout = stallTimeout,
                CheckInterval = TimeSpan.FromMinutes(1),
            },
        };

    private sealed class WorkerPoolHealthWatchdogWiringFactory : CodeyBox.Tests.CodeyBoxWebApplicationFactory
    {
        private readonly MutableOptionsMonitor<CodeyBoxOptions> _monitor;
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(), $"codeybox-pool-health-wiring-{Guid.NewGuid():N}.db");

        public WorkerPoolHealthWatchdogWiringFactory(MutableOptionsMonitor<CodeyBoxOptions> monitor)
            => _monitor = monitor;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.Sources.Clear();
                var tmp = Temp.Root;
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CodeyBox:DangerouslyDisableAuth"] = "true",
                    ["CodeyBox:StateDatabasePath"] = _dbPath,
                    ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"test-git-{Guid.NewGuid():N}"),
                    ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"test-log-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"test-audit-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AgentStreams:Path"] = Path.Combine(tmp, $"test-agent-streams-{Guid.NewGuid():N}"),
                    ["CodeyBox:WorkerPoolHealthWatchdog:StallTimeout"] = "00:03:00",
                    ["CodeyBox:WorkerPoolHealthWatchdog:CheckInterval"] = "00:01:00",
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IOptionsMonitor<CodeyBoxOptions>>();
                services.AddSingleton<IOptionsMonitor<CodeyBoxOptions>>(_monitor);
            });
        }

        protected override void Dispose(bool disposing)
        => DisposeHostThenDeleteSqliteDatabase(disposing, _dbPath);
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
