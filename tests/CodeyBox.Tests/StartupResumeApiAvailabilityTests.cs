using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeyBox.Tests;

public sealed class StartupResumeApiAvailabilityTests
{
    [Theory]
    [InlineData("hang")]
    [InlineData("throw")]
    public async Task StartupResumeFailure_DoesNotBlockQuotaEndpoint_AndMarksItemFailed(string behavior)
    {
        using var baseFactory = new WorkItemApiFactory();
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "resume wedge",
            Prompt = "p",
            State = WorkItemState.Working,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            SuspendedVmName = $"vm-{behavior}",
            SuspendedAt = DateTimeOffset.UtcNow,
        };
        await baseFactory.Store.CreateAsync(item);

        var provider = new StartupResumeProvider(behavior);
        using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CodeyBox:Shutdown:SandboxResumeMode"] = nameof(SandboxStartupResumeMode.Background),
                    ["CodeyBox:Shutdown:SandboxResumeTimeout"] = "00:00:00.050",
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISandboxProvider>();
                services.AddSingleton<ISandboxProvider>(provider);
                services.AddHostedService(sp =>
                {
                    var monitor = sp.GetRequiredService<IOptionsMonitor<CodeyBoxOptions>>();
                    return new SandboxResumeOnStartupService(
                        sp.GetRequiredService<ISandboxProvider>(),
                        sp.GetRequiredService<IWorkItemStore>(),
                        sp.GetRequiredService<ILogger<SandboxResumeOnStartupService>>(),
                        optionsAccessor: () =>
                        {
                            var shutdown = monitor.CurrentValue.Shutdown;
                            return new SandboxStartupResumeOptions
                            {
                                Mode = shutdown.SandboxResumeMode,
                                ResumeTimeout = shutdown.SandboxResumeTimeout,
                                AdoptionDeadline = TimeSpan.FromSeconds(shutdown.SandboxAdoptionDeadlineSeconds),
                            };
                        },
                        barrier: sp.GetRequiredService<StartupSandboxResumeBarrier>());
                });
            });
        });

        var client = await Task.Run(() => factory.CreateClient()).WaitAsync(TimeSpan.FromSeconds(10));
        using var response = await client.GetAsync("/quota").WaitAsync(TimeSpan.FromSeconds(15));

        response.EnsureSuccessStatusCode();
        var failed = await WaitForStateAsync(baseFactory.Store, item.Id, WorkItemState.Failed);
        Assert.Null(failed.SuspendedVmName);
        Assert.Contains(behavior == "hang" ? "timed out" : "simulated resume failure", failed.LastError);
        Assert.Contains(item.SuspendedVmName!, provider.ResumedNames);
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
