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

    private sealed class WorkerPoolHealthWatchdogWiringFactory : WebApplicationFactory<Program>
    {
        private readonly MutableOptionsMonitor<CodeyBoxOptions> _monitor;
        private readonly TestScratchDirectory _scratch = TestScratchDirectory.Create("codeybox-pool-health-wiring-");
        private string _dbPath => _scratch.DbPath("pool-health-wiring.db");

        public WorkerPoolHealthWatchdogWiringFactory(MutableOptionsMonitor<CodeyBoxOptions> monitor)
            => _monitor = monitor;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.Sources.Clear();
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CodeyBox:DangerouslyDisableAuth"] = "true",
                    ["CodeyBox:StateDatabasePath"] = _dbPath,
                    ["CodeyBox:GitHubAppStorePath"] = Path.Combine(_scratch.DirectoryPath, "github-apps"),
                    ["CodeyBox:GitRootDirectory"] = Path.Combine(_scratch.DirectoryPath, "test-git"),
                    ["CodeyBox:AuditLog:Path"] = Path.Combine(_scratch.DirectoryPath, "test-log.json"),
                    ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(_scratch.DirectoryPath, "test-audit.json"),
                    ["CodeyBox:AgentStreams:Path"] = Path.Combine(_scratch.DirectoryPath, "test-agent-streams"),
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
        {
            if (disposing)
                try { File.Delete(_dbPath); } catch { /* best-effort */ }
                TestScratchDirectory.DeleteSqliteCompanions(_dbPath);
                _scratch.Dispose();
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
