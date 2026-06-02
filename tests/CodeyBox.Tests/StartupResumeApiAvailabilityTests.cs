using System.Diagnostics;
using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using CodeyBox.Projects;

namespace CodeyBox.Tests;

public sealed class StartupResumeApiAvailabilityTests
{
    [Theory]
    [InlineData("hang")]
    [InlineData("throw")]
    public async Task StartupResumeFailure_DoesNotBlockQuotaEndpoint_AndMarksItemFailed(string behavior)
    {
        var configuredTimeout = TimeSpan.FromMilliseconds(50);
        using var factory = new StartupResumeFullHostFactory(behavior, configuredTimeout);
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

        var client = factory.CreateClient();
        var sw = Stopwatch.StartNew();
        using var response = await client.GetAsync("/quota")
            .WaitAsync(TimeSpan.FromSeconds(2));
        sw.Stop();

        response.EnsureSuccessStatusCode();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2),
            $"GET /quota was not served promptly while startup resume was running; elapsed {sw.Elapsed}");

        var failed = await WaitForStateAsync(factory.Store, item.Id, WorkItemState.Failed);
        Assert.Null(failed.SuspendedVmName);
        Assert.Contains(behavior == "hang" ? "timed out" : "simulated resume failure", failed.LastError);
        Assert.Contains(item.SuspendedVmName!, factory.Provider.ResumedNames);
    }

    private static async Task<WorkItem> WaitForStateAsync(
        IWorkItemStore store,
        WorkItemId id,
        WorkItemState expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
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
        private readonly TimeSpan _resumeTimeout;
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(), $"codeybox-startup-resume-api-{Guid.NewGuid():N}.db");

        public SqliteWorkItemStore Store { get; }
        public SqliteWorkerRegistry Registry { get; }
        public StartupResumeProvider Provider { get; }

        public StartupResumeFullHostFactory(string behavior, TimeSpan resumeTimeout)
        {
            _behavior = behavior;
            _resumeTimeout = resumeTimeout;
            Store = new SqliteWorkItemStore(_dbPath);
            Registry = new SqliteWorkerRegistry(_dbPath);
            Provider = new StartupResumeProvider(_behavior);
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
                    ["CodeyBox:Shutdown:SandboxResumeMode"] = nameof(SandboxStartupResumeMode.Background),
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
            => throw new NotImplementedException();

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
    }
}
