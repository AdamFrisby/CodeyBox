using System.Diagnostics;
using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CodeyBox.Projects;

namespace CodeyBox.Tests;

[Collection("GlobalSerilog")]
public sealed class StartupResumeApiAvailabilityTests
{
    [Theory]
    [InlineData("hang", SandboxResumeMode.Background)]
    [InlineData("throw", SandboxResumeMode.Background)]
    [InlineData("hang", SandboxResumeMode.Blocking)]
    [InlineData("throw", SandboxResumeMode.Blocking)]
    public async Task StartupResumeFailure_DoesNotBlockQuotaEndpoint_AndMarksItemFailed(
        string behavior,
        SandboxResumeMode mode)
    {
        var configuredTimeout = mode == SandboxResumeMode.Background
            ? TimeSpan.FromSeconds(4)
            : TimeSpan.FromSeconds(3);
        using var factory = new StartupResumeFullHostFactory(
            behavior,
            mode,
            configuredTimeout,
            isolateStartupResumeHostedServices: true);
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "resume wedge",
            Prompt = "p",
            State = WorkItemState.Working,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            SuspendedVmName = $"vm-{behavior}",
            SuspendedAt = DateTimeOffset.UtcNow,
        };
        await factory.Store.CreateAsync(item);
        await factory.Registry.RegisterAsync(new WorkerRegistration
        {
            WorkerId = $"worker-{behavior}",
            HostName = "host",
            ProcessId = 123,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            LastHeartbeatAt = DateTimeOffset.UtcNow,
            CurrentWorkItemId = item.Id.ToString(),
        });

        var sw = new Stopwatch();
        HttpClient? bootstrapClient = null;
        HttpClient? networkClient = null;
        HttpResponseMessage? response = null;
        try
        {
            // Both modes get +30s wall-clock slack to absorb parallel-suite
            // host-startup contention; the per-mode elapsed assertions below
            // are what prove the resume contract, not the WaitAsync deadline.
            // Background mode: factory.CreateClient() returns as soon as the
            // host is up because resume runs in a BackgroundService, so the
            // stopwatch only spans the GET (host startup is not part of the
            // signal). Blocking mode: factory.CreateClient() blocks until
            // the resume timeout elapses, so the stopwatch must span
            // CreateClient to observe the block.
            var availabilityDeadline = configuredTimeout + TimeSpan.FromSeconds(30);
            response = await RunOnDedicatedThreadAsync(() =>
            {
                if (mode == SandboxResumeMode.Blocking)
                    sw.Start();
                bootstrapClient = factory.CreateClient();
                var baseAddress = bootstrapClient.BaseAddress
                    ?? throw new InvalidOperationException("Kestrel-backed client did not expose a base address");
                networkClient = new HttpClient { BaseAddress = baseAddress };
                if (mode == SandboxResumeMode.Background)
                    sw.Start();
                return networkClient.GetAsync("/quota").GetAwaiter().GetResult();
            }).WaitAsync(availabilityDeadline);
            sw.Stop();

            response.EnsureSuccessStatusCode();
            if (mode == SandboxResumeMode.Background)
            {
                Assert.True(sw.Elapsed < configuredTimeout,
                    $"GET /quota was not served before configured startup resume timeout {configuredTimeout}; elapsed {sw.Elapsed}");
            }
            else
            {
                if (behavior == "hang")
                {
                    Assert.True(sw.Elapsed >= configuredTimeout,
                        $"Blocking startup resume did not honor configured mode; GET /quota was served before resume timeout {configuredTimeout}; elapsed {sw.Elapsed}");
                }
                Assert.True(sw.Elapsed < availabilityDeadline,
                    $"GET /quota was not served after configured blocking resume timeout {configuredTimeout}; elapsed {sw.Elapsed}");
            }
        }
        finally
        {
            response?.Dispose();
            networkClient?.Dispose();
            bootstrapClient?.Dispose();
        }

        // Wider deadline absorbs thread-pool starvation under parallel-suite
        // CPU contention. Background mode marks Failed after the
        // configuredTimeout elapses; the happy path still flips within tens
        // of ms after that, but the BackgroundService kick can sit on the
        // ready-queue much longer under load.
        var failed = await WaitForStateAsync(
            factory.Store,
            item.Id,
            WorkItemState.Failed,
            configuredTimeout + TimeSpan.FromSeconds(60));
        Assert.Null(failed.SuspendedVmName);
        Assert.Contains(behavior == "hang" ? "timed out" : "simulated resume failure", failed.LastError);
        Assert.Contains(item.SuspendedVmName!, factory.Provider.ResumedNames);
    }

    [Fact]
    public async Task StartupResume_HotReloadedShutdownConfig_UsesProductionOptionsForModeTimeoutAndAdoption()
    {
        // The un-reloaded (initial) timeout is the value that would govern the
        // hanging resume if the hot reload had NOT taken. Keep it far above the
        // availability guard below so the guard cleanly discriminates
        // "reload applied (250 ms)" from "reload ignored (initial)" even when
        // parallel audit-suite load adds many seconds of HTTP-serving latency on
        // top of the 250 ms resume.
        var initialTimeout = TimeSpan.FromSeconds(90);
        var reloadedTimeout = TimeSpan.FromMilliseconds(250);
        var reloadedAdoptionSeconds = 7;
        var availabilityGuard = initialTimeout - TimeSpan.FromSeconds(5);
        using var factory = new StartupResumeFullHostFactory(
            behavior: "complete",
            mode: SandboxResumeMode.Background,
            resumeTimeout: initialTimeout,
            adoptionDeadlineSeconds: 1800,
            isolateStartupResumeHostedServices: true,
            reloadBeforeStartup: new Dictionary<string, string?>
            {
                ["CodeyBox:Shutdown:SandboxResumeMode"] = SandboxResumeMode.Blocking.ToString(),
                ["CodeyBox:Shutdown:SandboxResumeTimeout"] = reloadedTimeout.ToString(),
                ["CodeyBox:Shutdown:SandboxAdoptionDeadlineSeconds"] = reloadedAdoptionSeconds.ToString(),
            });
        var timedOut = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "hot reload timeout",
            Prompt = "p",
            State = WorkItemState.Working,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            SuspendedVmName = "vm-hot-timeout",
            SuspendedAt = DateTimeOffset.UtcNow,
        };
        var adopted = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "hot reload adoption",
            Prompt = "p",
            State = WorkItemState.Working,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            SuspendedVmName = "vm-hot-adoption",
            SuspendedAt = DateTimeOffset.UtcNow,
            AgentLogPath = "/work/.codeybox/agent-logs/hot-reload.log",
        };
        await factory.Store.CreateAsync(timedOut);
        await factory.Store.CreateAsync(adopted);
        factory.Provider.ResumeNamesToHang.Add(timedOut.SuspendedVmName!);
        factory.Provider.AdoptionExitCodeToReturn = 0;

        var sw = Stopwatch.StartNew();
        HttpClient? bootstrapClient = null;
        HttpClient? networkClient = null;
        HttpResponseMessage? response = null;
        try
        {
            // Keep the real Kestrel endpoint in the path, but isolate hosted
            // startup work to the reload shim plus SandboxResumeOnStartupService
            // so this measures the behavior under test instead of unrelated
            // background services. The persisted work item assertion below
            // proves the hot-reloaded 250 ms timeout was the value used by the
            // resume handler.
            response = await RunOnDedicatedThreadAsync(() =>
            {
                bootstrapClient = factory.CreateClient();
                var baseAddress = bootstrapClient.BaseAddress
                    ?? throw new InvalidOperationException("Kestrel-backed client did not expose a base address");
                networkClient = new HttpClient { BaseAddress = baseAddress };
                return networkClient.GetAsync("/quota").GetAwaiter().GetResult();
            }).WaitAsync(initialTimeout + TimeSpan.FromSeconds(10));
            sw.Stop();

            response.EnsureSuccessStatusCode();
            // The guard stays below the unreloaded timeout so an ignored reload
            // fails, while the isolated hosted-service set keeps this focused on
            // startup resume instead of unrelated background services.
            // The reloaded 250 ms timeout being the value actually applied is
            // proven independently by the >= reloaded assertion below plus the
            // persisted work-item assertion.
            Assert.True(sw.Elapsed < availabilityGuard,
                $"GET /quota was blocked for {sw.Elapsed}; hot-reloaded startup resume timeout {reloadedTimeout} should keep API availability below the {initialTimeout} un-reloaded window.");
            Assert.True(sw.Elapsed >= reloadedTimeout,
                $"hot-reloaded Blocking mode was not observed; GET /quota was served before resume timeout {reloadedTimeout}; elapsed {sw.Elapsed}");
        }
        finally
        {
            response?.Dispose();
            networkClient?.Dispose();
            bootstrapClient?.Dispose();
        }

        var failed = await WaitForStateAsync(factory.Store, timedOut.Id, WorkItemState.Failed, TimeSpan.FromSeconds(2));
        Assert.Null(failed.SuspendedVmName);
        Assert.Contains($"timed out after {reloadedTimeout}", failed.LastError);

        await WaitUntilAsync(() => factory.Provider.AdoptionCalls.Count == 1);
        var adoption = Assert.Single(factory.Provider.AdoptionCalls);
        Assert.Equal("vm-hot-adoption", adoption.VmName);
        Assert.Equal(TimeSpan.FromSeconds(reloadedAdoptionSeconds), adoption.Deadline);
    }

    private static async Task<WorkItem> WaitForStateAsync(
        IWorkItemStore store,
        WorkItemId id,
        WorkItemState expected,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        WorkItem? latest = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            latest = await store.GetAsync(id);
            if (latest?.State == expected)
                return latest;

            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }

        latest = await store.GetAsync(id);
        Assert.Equal(expected, latest!.State);
        return latest;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }

        Assert.True(condition(), "condition was not met before the timeout elapsed");
    }

    private static Task<T> RunOnDedicatedThreadAsync<T>(Func<T> work) =>
        Task.Factory.StartNew(
            work,
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach | TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    private sealed class StartupResumeFullHostFactory : WebApplicationFactory<Program>
    {
        private readonly string _behavior;
        private readonly SandboxResumeMode _mode;
        private readonly TimeSpan _resumeTimeout;
        private readonly int? _adoptionDeadlineSeconds;
        private readonly IReadOnlyDictionary<string, string?>? _reloadBeforeStartup;
        private readonly bool _isolateStartupResumeHostedServices;
        private readonly ReloadableConfigurationSource _configSource;
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(), $"codeybox-startup-resume-api-{Guid.NewGuid():N}.db");

        public SqliteWorkItemStore Store { get; }
        public SqliteWorkerRegistry Registry { get; }
        public StartupResumeProvider Provider { get; }

        public StartupResumeFullHostFactory(
            string behavior,
            SandboxResumeMode mode,
            TimeSpan resumeTimeout,
            int? adoptionDeadlineSeconds = null,
            bool isolateStartupResumeHostedServices = false,
            IReadOnlyDictionary<string, string?>? reloadBeforeStartup = null)
        {
            _behavior = behavior;
            _mode = mode;
            _resumeTimeout = resumeTimeout;
            _adoptionDeadlineSeconds = adoptionDeadlineSeconds;
            _reloadBeforeStartup = reloadBeforeStartup;
            _isolateStartupResumeHostedServices = isolateStartupResumeHostedServices;
            _configSource = new ReloadableConfigurationSource(BuildInitialConfig());
            Store = new SqliteWorkItemStore(_dbPath);
            Registry = new SqliteWorkerRegistry(_dbPath);
            Provider = new StartupResumeProvider(_behavior);
            UseKestrel(0);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.Add(_configSource);
            });
            builder.ConfigureTestServices(services =>
            {
                if (_isolateStartupResumeHostedServices)
                    RemoveCodeyBoxHostedServices(services);

                if (_reloadBeforeStartup is not null)
                {
                    services.Insert(0, ServiceDescriptor.Singleton<IHostedService>(
                        new ReloadShutdownConfigBeforeStartupService(
                            () => _configSource.SetValues(_reloadBeforeStartup))));
                }

                if (_isolateStartupResumeHostedServices)
                {
                    services.AddSingleton<IHostedService>(sp => new SandboxResumeOnStartupService(
                        sp.GetService<ISandboxProvider>(),
                        sp.GetRequiredService<IWorkItemStore>(),
                        sp.GetRequiredService<ILogger<SandboxResumeOnStartupService>>(),
                        () => Program.BuildSandboxStartupResumeOptions(
                            sp.GetRequiredService<IOptionsMonitor<CodeyBoxOptions>>().CurrentValue.Shutdown),
                        sp.GetRequiredService<IStartupRecoveryInputSink>(),
                        sp.GetRequiredService<IInfrastructureDeferralScheduler>(),
                        sp.GetRequiredService<IHostApplicationLifetime>()));
                }

                services.RemoveAll<IWorkItemStore>();
                services.AddSingleton<IWorkItemStore>(Store);

                services.RemoveAll<IWorkerRegistry>();
                services.AddSingleton<IWorkerRegistry>(Registry);

                services.RemoveAll<ISandboxProvider>();
                services.AddSingleton<ISandboxProvider>(Provider);

                services.RemoveAll<IAgentQuotaProbe>();
                services.AddSingleton<IAgentQuotaProbe>(new NullQuotaProbe());

                services.RemoveAll<IAgentModelListProbe>();

                services.RemoveAll<IProjectRepository>();
                services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository(
                    new Project
                    {
                        Id = new ProjectId("test-project"),
                        DisplayName = "Test Project",
                        RepositoryUrl = "https://github.com/test/repo",
                    }));
            });
        }

        private static void RemoveCodeyBoxHostedServices(IServiceCollection services)
        {
            var descriptors = services
                .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                    && !IsAspNetCoreWebHostService(descriptor))
                .ToArray();
            foreach (var descriptor in descriptors)
                services.Remove(descriptor);
        }

        private static bool IsAspNetCoreWebHostService(ServiceDescriptor descriptor) =>
            string.Equals(
                descriptor.ImplementationType?.FullName,
                "Microsoft.AspNetCore.Hosting.GenericWebHostService",
                StringComparison.Ordinal);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                base.Dispose(disposing);
                Store.Dispose();
                Registry.Dispose();
                try { File.Delete(_dbPath); } catch { }
                return;
            }
            base.Dispose(disposing);
        }

        private Dictionary<string, string?> BuildInitialConfig()
        {
            var tmp = Path.GetTempPath();
            var values = new Dictionary<string, string?>
            {
                ["CodeyBox:DangerouslyDisableAuth"] = "true",
                ["CodeyBox:StateDatabasePath"] = _dbPath,
                ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"test-git-{Guid.NewGuid():N}"),
                ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"test-log-{Guid.NewGuid():N}-.json"),
                ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"test-audit-{Guid.NewGuid():N}-.json"),
                ["CodeyBox:AgentStreams:Path"] = Path.Combine(tmp, $"test-agent-streams-{Guid.NewGuid():N}"),
                ["CodeyBox:Smoke:Enabled"] = "false",
                ["CodeyBox:Smoke:InVm:Enabled"] = "false",
                ["CodeyBox:Shutdown:SandboxResumeMode"] = _mode.ToString(),
                ["CodeyBox:Shutdown:SandboxResumeTimeout"] = _resumeTimeout.ToString(),
                ["CodeyBox:WorkerProgressWatchdog:CheckInterval"] = "00:00:00.010",
                ["CodeyBox:WorkerProgressWatchdog:ProgressTimeout"] = "00:00:00.010",
                ["CodeyBox:WorkerProgressWatchdog:AutoRecover"] = "true",
                ["CodeyBox:DeadWorker:HeartbeatInterval"] = "00:00:00.005",
                ["CodeyBox:DeadWorker:DeadWorkerThreshold"] = "00:00:00.015",
                ["CodeyBox:DeadWorker:CheckInterval"] = "00:00:00.010",
            };
            if (_adoptionDeadlineSeconds is not null)
            {
                values["CodeyBox:Shutdown:SandboxAdoptionDeadlineSeconds"] =
                    _adoptionDeadlineSeconds.Value.ToString();
            }

            return values;
        }
    }

    private sealed class StartupResumeProvider : ISandboxProvider, IActiveSandboxProvider, ISuspendingSandboxProvider
    {
        private readonly string _behavior;
        private readonly TaskCompletionSource _never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> _resumedNames = new();
        private readonly List<AdoptionCall> _adoptionCalls = new();

        public StartupResumeProvider(string behavior) => _behavior = behavior;

        public HashSet<string> ResumeNamesToHang { get; } = new(StringComparer.Ordinal);
        public int? AdoptionExitCodeToReturn { get; set; }

        public IReadOnlyList<string> ResumedNames
        {
            get
            {
                lock (_resumedNames) return _resumedNames.ToArray();
            }
        }

        public IReadOnlyList<AdoptionCall> AdoptionCalls
        {
            get
            {
                lock (_adoptionCalls) return _adoptionCalls.ToArray();
            }
        }

        public string Name => "startup-resume-test";

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
            => Task.FromResult<ISandbox>(new NoopSandbox());

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);

        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;

        public IReadOnlyList<(WorkItemId WorkItemId, IShutdownTeardownSandbox Sandbox)> SnapshotActiveSandboxes() => [];

        public Task ResumeSandboxAsync(string name, CancellationToken ct)
        {
            lock (_resumedNames) _resumedNames.Add(name);

            if (_behavior == "throw")
                throw new InvalidOperationException("simulated resume failure");
            if (_behavior == "hang" || ResumeNamesToHang.Contains(name))
                return _never.Task;

            return Task.CompletedTask;
        }

        public Task<int?> WaitForAdoptedAgentCompletionAsync(
            string vmName,
            string agentLogPath,
            Action<string>? logSink,
            TimeSpan? deadline,
            CancellationToken ct)
        {
            lock (_adoptionCalls) _adoptionCalls.Add(new AdoptionCall(vmName, agentLogPath, deadline));
            return Task.FromResult(AdoptionExitCodeToReturn);
        }

        public Task<bool> PushSuspendedVmCheckpointRefAsync(
            string vmName,
            string workingDir,
            string refName,
            string commitMessage,
            CancellationToken ct)
        {
            return Task.FromResult(false);
        }

        private sealed class NoopSandbox : ISandbox
        {
            public string Id => "startup-resume-created";

            public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
                => Task.FromResult(new SandboxExecResult(0, string.Empty, string.Empty));

            public void PauseDispatch() { }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed record AdoptionCall(string VmName, string AgentLogPath, TimeSpan? Deadline);

    private sealed class ReloadShutdownConfigBeforeStartupService(Action reload) : IHostedLifecycleService
    {
        public Task StartingAsync(CancellationToken cancellationToken)
        {
            reload();
            return Task.CompletedTask;
        }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ReloadableConfigurationSource : IConfigurationSource
    {
        private readonly Dictionary<string, string?> _values;
        private ReloadableConfigurationProvider? _provider;

        public ReloadableConfigurationSource(IReadOnlyDictionary<string, string?> values)
        {
            _values = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);
        }

        public IConfigurationProvider Build(IConfigurationBuilder builder)
        {
            _provider = new ReloadableConfigurationProvider(_values);
            return _provider;
        }

        public void SetValues(IReadOnlyDictionary<string, string?> values)
        {
            if (_provider is null)
                throw new InvalidOperationException("Configuration provider has not been built");

            _provider.SetValues(values);
        }
    }

    private sealed class ReloadableConfigurationProvider : ConfigurationProvider
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, string?> _values;

        public ReloadableConfigurationProvider(IReadOnlyDictionary<string, string?> values)
        {
            _values = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);
        }

        public override void Load()
        {
            lock (_gate)
                Data = new Dictionary<string, string?>(_values, StringComparer.OrdinalIgnoreCase);
        }

        public void SetValues(IReadOnlyDictionary<string, string?> values)
        {
            lock (_gate)
            {
                foreach (var (key, value) in values)
                    _values[key] = value;
                Data = new Dictionary<string, string?>(_values, StringComparer.OrdinalIgnoreCase);
            }
            OnReload();
        }
    }
}
