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
using CodeyBox.Projects;

namespace CodeyBox.Tests;

[Collection("GlobalSerilog")]
public sealed class StartupResumeApiAvailabilityTests
{
    [Theory]
    [InlineData("hang", SandboxResumeMode.Background)]
    [InlineData("throw", SandboxResumeMode.Background)]
    [InlineData("hang", SandboxResumeMode.Blocking)]
    public async Task StartupResumeFailure_DoesNotBlockQuotaEndpoint_AndMarksItemFailed(
        string behavior,
        SandboxResumeMode mode)
    {
        var configuredTimeout = mode == SandboxResumeMode.Background
            ? TimeSpan.FromSeconds(10)
            : TimeSpan.FromSeconds(3);
        using var factory = new StartupResumeFullHostFactory(behavior, mode, configuredTimeout);
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

        var sw = Stopwatch.StartNew();
        HttpClient? bootstrapClient = null;
        HttpClient? networkClient = null;
        HttpResponseMessage? response = null;
        try
        {
            var availabilityDeadline = mode == SandboxResumeMode.Background
                ? configuredTimeout
                : configuredTimeout + TimeSpan.FromSeconds(7);
            response = await Task.Run(async () =>
            {
                bootstrapClient = factory.CreateClient();
                var baseAddress = bootstrapClient.BaseAddress
                    ?? throw new InvalidOperationException("Kestrel-backed client did not expose a base address");
                networkClient = new HttpClient { BaseAddress = baseAddress };
                return await networkClient.GetAsync("/quota");
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
                Assert.True(sw.Elapsed >= configuredTimeout,
                    $"Blocking startup resume did not honor configured mode; GET /quota was served before resume timeout {configuredTimeout}; elapsed {sw.Elapsed}");
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

        var failed = await WaitForStateAsync(
            factory.Store,
            item.Id,
            WorkItemState.Failed,
            configuredTimeout + TimeSpan.FromSeconds(5));
        Assert.Null(failed.SuspendedVmName);
        Assert.Contains(behavior == "hang" ? "timed out" : "simulated resume failure", failed.LastError);
        Assert.Contains(item.SuspendedVmName!, factory.Provider.ResumedNames);
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

    private sealed class StartupResumeFullHostFactory : WebApplicationFactory<Program>
    {
        private readonly string _behavior;
        private readonly SandboxResumeMode _mode;
        private readonly TimeSpan _resumeTimeout;
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(), $"codeybox-startup-resume-api-{Guid.NewGuid():N}.db");

        public SqliteWorkItemStore Store { get; }
        public SqliteWorkerRegistry Registry { get; }
        public StartupResumeProvider Provider { get; }

        public StartupResumeFullHostFactory(
            string behavior,
            SandboxResumeMode mode,
            TimeSpan resumeTimeout)
        {
            _behavior = behavior;
            _mode = mode;
            _resumeTimeout = resumeTimeout;
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
                var tmp = Path.GetTempPath();
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
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
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IWorkItemStore>();
                services.AddSingleton<IWorkItemStore>(Store);

                services.RemoveAll<IWorkerRegistry>();
                services.AddSingleton<IWorkerRegistry>(Registry);

                services.RemoveAll<ISandboxProvider>();
                services.AddSingleton<ISandboxProvider>(Provider);

                services.RemoveAll<IAgentQuotaProbe>();
                services.AddSingleton<IAgentQuotaProbe>(new NullQuotaProbe());

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

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Store.Dispose();
                Registry.Dispose();
                try { File.Delete(_dbPath); } catch { }
            }
            base.Dispose(disposing);
        }
    }

    private sealed class StartupResumeProvider : ISandboxProvider, ISuspendingSandboxProvider
    {
        private readonly string _behavior;
        private readonly TaskCompletionSource _never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> _resumedNames = new();

        public StartupResumeProvider(string behavior) => _behavior = behavior;

        public IReadOnlyList<string> ResumedNames
        {
            get
            {
                lock (_resumedNames) return _resumedNames.ToArray();
            }
        }

        public string Name => "startup-resume-test";

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
            => Task.FromResult<ISandbox>(new NoopSandbox());

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);

        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;

        public IReadOnlyList<(WorkItemId WorkItemId, ISuspendableSandbox Sandbox)> SnapshotSuspendableActive() => [];

        public Task ResumeSandboxAsync(string name, CancellationToken ct)
        {
            lock (_resumedNames) _resumedNames.Add(name);

            if (_behavior == "throw")
                throw new InvalidOperationException("simulated resume failure");

            return _never.Task;
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
}
