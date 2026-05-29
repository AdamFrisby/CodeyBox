using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Api;
using CodeyBox.Core;
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

/// <summary>
/// Pins the composition-root wiring in <see cref="Program.ComputeHostShutdownTimeout"/>:
/// the callback that sets <c>HostOptions.ShutdownTimeout</c> must read the same
/// inputs from <see cref="CodeyBoxOptions"/> that the orchestrator pool
/// uses (via <see cref="OrchestratorOptionsFactory"/>) and feed them, together
/// with the resolved provider's suspend capability, into
/// <see cref="SuspendTimeoutPolicy.ResolveHostShutdownTimeout"/>. A drift here
/// (wrong property, inverted precedence, or a literal that bypasses the factory)
/// would leave the host SIGKILL budget too small and reproduce the acceptance
/// -criterion #1 failure the wave-scaling change targets.
/// </summary>
public sealed class HostShutdownTimeoutWiringTests
{
    private static CodeyBoxOptions Opts(
        int? concurrency = null, int? maxWorkers = null, int graceSeconds = 60)
    {
        var o = new CodeyBoxOptions
        {
            Concurrency = concurrency,
            WorkerPool = new WorkerPoolOptions { MaxConcurrentWorkers = maxWorkers },
        };
        o.Shutdown.GraceSeconds = graceSeconds;
        return o;
    }

    [Fact]
    public void NonSuspendingProvider_KeepsTheGraceWindow()
    {
        // A provider that does not implement ISuspendingSandboxProvider never
        // suspends on shutdown, so the ceiling stays at the configured grace
        // regardless of worker count.
        var timeout = Program.ComputeHostShutdownTimeout(
            Opts(maxWorkers: 32, graceSeconds: 45),
            providerSuspendsOnShutdown: false,
            NullLogger.Instance);

        Assert.Equal(TimeSpan.FromSeconds(45), timeout);
    }

    [Fact]
    public void SuspendingProvider_SingleWorker_StacksOneWaveOnTopOfTheGrace()
    {
        // One in-flight VM → one suspend wave of the default 12 GiB profile
        // budget (30 min), STACKED on top of the 60s post-suspend drain grace.
        var timeout = Program.ComputeHostShutdownTimeout(
            Opts(maxWorkers: 1),
            providerSuspendsOnShutdown: true,
            NullLogger.Instance);

        Assert.Equal(TimeSpan.FromMinutes(30) + TimeSpan.FromSeconds(60), timeout);
    }

    [Fact]
    public void SuspendingProvider_ScalesByWaveCount_FromWorkerPool()
    {
        // 16 workers > the parallel-suspend cap (8) → two sequential waves → 60 min,
        // plus the 60s drain grace. This is the exact undersizing the wave-scaling
        // fix targets: a single-wave ceiling would SIGKILL the host before wave 2
        // finished its snapshot.
        var timeout = Program.ComputeHostShutdownTimeout(
            Opts(maxWorkers: 16),
            providerSuspendsOnShutdown: true,
            NullLogger.Instance);

        Assert.Equal(TimeSpan.FromMinutes(60) + TimeSpan.FromSeconds(60), timeout);
    }

    [Fact]
    public void WorkerCount_FollowsOrchestratorOptionsFactoryPrecedence()
    {
        // The ceiling must size off the SAME worker count the orchestrator pool
        // runs at. Legacy CodeyBox:Concurrency is the fallback when WorkerPool is
        // unset (16 → 2 waves → 60 min + 60s grace)...
        var legacyOnly = Program.ComputeHostShutdownTimeout(
            Opts(concurrency: 16, maxWorkers: null),
            providerSuspendsOnShutdown: true,
            NullLogger.Instance);
        Assert.Equal(TimeSpan.FromMinutes(60) + TimeSpan.FromSeconds(60), legacyOnly);

        // ...and WorkerPool:MaxConcurrentWorkers wins when both are set, so a stale
        // legacy value cannot inflate (or here, would not shrink) the ceiling. 1
        // worker → a single 30-min wave (+60s grace) even though Concurrency says 16.
        var workerPoolWins = Program.ComputeHostShutdownTimeout(
            Opts(concurrency: 16, maxWorkers: 1),
            providerSuspendsOnShutdown: true,
            NullLogger.Instance);
        Assert.Equal(TimeSpan.FromMinutes(30) + TimeSpan.FromSeconds(60), workerPoolWins);
    }

    [Fact]
    public void Grace_StacksOnTopOfTheSuspendReserve()
    {
        // ShutdownTimeout is grace + suspendReserve: the post-suspend drain window
        // is sequential with the suspend drain, not overlapping, so a long operator
        // grace is ADDED to the reserve rather than absorbing it. 4h grace + one
        // 30-min wave = 4h30m.
        var timeout = Program.ComputeHostShutdownTimeout(
            Opts(maxWorkers: 1, graceSeconds: 4 * 60 * 60),
            providerSuspendsOnShutdown: true,
            NullLogger.Instance);

        Assert.Equal(TimeSpan.FromHours(4) + TimeSpan.FromMinutes(30), timeout);
    }

    // --- DI-level wiring: the AddOptions<HostOptions>().Configure callback -------
    // The static-helper tests above pin ComputeHostShutdownTimeout, but the
    // production path also depends on the Configure delegate (Program.cs ~line 188)
    // deriving providerSuspendsOnShutdown from `sandboxProvider is
    // ISuspendingSandboxProvider` and feeding it in. These tests build the host and
    // read IOptions<HostOptions> from DI so a regression that hard-codes the bool,
    // drops the capability check, or mis-wires the delegate is caught — none of
    // which the static-helper tests would notice.

    [Fact]
    public void HostOptions_FromDi_RaisesCeiling_ForSuspendingProvider()
    {
        using var factory = new HostOptionsWiringFactory(
            new FakeSuspendingProvider(),
            graceSeconds: 60,
            maxConcurrentWorkers: 1);

        var hostOptions = factory.Services.GetRequiredService<IOptions<HostOptions>>().Value;

        // Capability detected → one 30-min wave of the default profile, stacked on
        // the 60s drain grace.
        Assert.Equal(TimeSpan.FromMinutes(30) + TimeSpan.FromSeconds(60), hostOptions.ShutdownTimeout);
    }

    [Fact]
    public void HostOptions_FromDi_KeepsGrace_ForNonSuspendingProvider()
    {
        using var factory = new HostOptionsWiringFactory(
            new FakeNonSuspendingProvider(),
            graceSeconds: 45,
            maxConcurrentWorkers: 32);

        var hostOptions = factory.Services.GetRequiredService<IOptions<HostOptions>>().Value;

        // No ISuspendingSandboxProvider → the capability branch is false and the
        // ceiling stays at the bare grace regardless of worker count.
        Assert.Equal(TimeSpan.FromSeconds(45), hostOptions.ShutdownTimeout);
    }

    private sealed class HostOptionsWiringFactory : WebApplicationFactory<Program>
    {
        private readonly ISandboxProvider _provider;
        private readonly int _graceSeconds;
        private readonly int _maxConcurrentWorkers;
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(), $"codeybox-hostopts-{Guid.NewGuid():N}.db");

        public HostOptionsWiringFactory(
            ISandboxProvider provider, int graceSeconds, int maxConcurrentWorkers)
        {
            _provider = provider;
            _graceSeconds = graceSeconds;
            _maxConcurrentWorkers = maxConcurrentWorkers;
        }

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
                    ["CodeyBox:Shutdown:GraceSeconds"] = _graceSeconds.ToString(),
                    ["CodeyBox:WorkerPool:MaxConcurrentWorkers"] = _maxConcurrentWorkers.ToString(),
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IProjectRepository>();
                services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository());
                // Swap the config-selected provider for the capability fixture the
                // test wants. The HostOptions Configure delegate resolves
                // ISandboxProvider lazily, so it picks up this replacement.
                services.RemoveAll<ISandboxProvider>();
                services.AddSingleton(_provider);
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                try { File.Delete(_dbPath); } catch { /* best-effort */ }
            base.Dispose(disposing);
        }
    }

    private sealed class FakeSuspendingProvider : ISandboxProvider, ISuspendingSandboxProvider
    {
        public string Name => "fake-suspending";
        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);
        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
        public IReadOnlyList<(WorkItemId WorkItemId, ISuspendableSandbox Sandbox)> SnapshotSuspendableActive() => [];
        public Task ResumeSandboxAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeNonSuspendingProvider : ISandboxProvider
    {
        public string Name => "fake-non-suspending";
        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);
        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }
}
