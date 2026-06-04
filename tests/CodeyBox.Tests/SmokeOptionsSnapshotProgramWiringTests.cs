using System.Reflection;
using System.Runtime.CompilerServices;
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

namespace CodeyBox.Tests;

public sealed class SmokeOptionsSnapshotProgramWiringTests
{
    [Fact]
    public async Task ProgramWiresSmokeConsumersToSharedSnapshotSingleton()
    {
        using var factory = new SmokeOptionsSnapshotWiringFactory(smokeEnabled: false);
        var snapshot = factory.Services.GetRequiredService<SmokeOptionsSnapshot>();
        var dispatchAvailability = factory.Services.GetRequiredService<IAgentDispatchAvailability>();

        Assert.False(snapshot.Enabled);
        Assert.False(factory.Services.GetRequiredService<SmokeOptions>().Enabled);
        Assert.Same(dispatchAvailability, FieldValue(
            factory.Services.GetRequiredService<AgentClassRouter>(),
            "_dispatchAvailability"));
        Assert.Same(dispatchAvailability, FieldValue(
            factory.Services.GetRequiredService<PipelineRunner>(),
            "_dispatchAvailability"));
        Assert.Same(dispatchAvailability, FieldValue(
            factory.Services.GetRequiredService<WorkerPoolHealthCoordinator>(),
            "_dispatchAvailability"));
        Assert.Same(snapshot, Field<SmokeOptionsSnapshot>(
            dispatchAvailability,
            "_smokeOptions"));
        Assert.Same(snapshot, Field<SmokeOptionsSnapshot>(
            factory.Services.GetRequiredService<CredentialSmokeGate>(),
            "_opts"));
        Assert.Same(snapshot, Field<SmokeOptionsSnapshot>(
            factory.Services.GetRequiredService<InVmSmokeProber>(),
            "_smokeOptions"));
        Assert.Same(snapshot, Field<SmokeOptionsSnapshot>(
            factory.Services.GetRequiredService<IInVmSmokeCoveragePolicy>(),
            "_smokeOptions"));
        Assert.Same(snapshot, Field<SmokeOptionsSnapshot>(
            factory.Services.GetRequiredService<PeriodicSmokeProbeService>(),
            "_smokeOpts"));
        Assert.Same(snapshot, Field<SmokeOptionsSnapshot>(
            factory.Services.GetRequiredService<AgentConfigHotReload>(),
            "_smokeOptions"));

        var registry = factory.Services.GetRequiredService<ISmokeAvailabilityRegistry>();
        registry.MarkSmokeResult(
            AgentKind.Claude,
            new AgentSmokeResult(false, "transient: try later", TimeSpan.Zero, SmokeFailureCategory.Transient),
            SmokeExclusionSource.InVmSmoke);

        var direct = dispatchAvailability.GetAvailability(AgentKind.Claude);
        Assert.NotNull(direct);
        Assert.True(direct.Available);

        var decision = await factory.Services.GetRequiredService<AgentClassRouter>().ResolveAsync(
            new WorkItem
            {
                Id = WorkItemId.New(),
                ProjectId = new ProjectId("proj"),
                Title = "smoke disabled",
                Prompt = "p",
                AgentClassId = "frontier",
            },
            project: null,
            CancellationToken.None);

        Assert.NotNull(decision.Chosen);
        Assert.Equal(AgentKind.Claude, decision.Chosen.Agent);
        Assert.Equal(0, factory.Gate.EnsureCalls);
    }

    [Fact]
    public async Task ProgramDispatchAvailabilityUsesConfiguredGateAndRegistry()
    {
        using var factory = new SmokeOptionsSnapshotWiringFactory(smokeEnabled: true);
        factory.Gate.EnsureResult = new AgentAvailability(false, "in-vm rejected", null);

        var registry = factory.Services.GetRequiredService<ISmokeAvailabilityRegistry>();
        registry.MarkSmokeResult(
            AgentKind.Claude,
            new AgentSmokeResult(false, "host smoke failed", TimeSpan.Zero),
            SmokeExclusionSource.HostSmoke);

        var dispatchAvailability = factory.Services.GetRequiredService<IAgentDispatchAvailability>();
        var registryRead = dispatchAvailability.GetAvailability(AgentKind.Claude);
        Assert.NotNull(registryRead);
        Assert.False(registryRead.Available);
        Assert.Contains("host smoke failed", registryRead.Reason!);

        var gated = await dispatchAvailability.EnsureAvailableAsync(
            AgentKind.Codex,
            default,
            CancellationToken.None);

        Assert.NotNull(gated);
        Assert.False(gated.Available);
        Assert.Equal("in-vm rejected", gated.Reason);
        Assert.Equal(1, factory.Gate.EnsureCalls);
    }

    private static T Field<T>(object instance, string name)
    {
        var field = instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return Assert.IsType<T>(field.GetValue(instance));
    }

    private static object? FieldValue(object instance, string name)
    {
        var field = instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return field.GetValue(instance);
    }

    private sealed class SmokeOptionsSnapshotWiringFactory : WebApplicationFactory<Program>
    {
        private readonly bool _smokeEnabled;
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(), $"codeybox-smoke-snapshot-wiring-{Guid.NewGuid():N}.db");

        public SmokeOptionsSnapshotWiringFactory(bool smokeEnabled) => _smokeEnabled = smokeEnabled;

        public RecordingGate Gate { get; } = new();

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
                    ["CodeyBox:Smoke:Enabled"] = _smokeEnabled ? "true" : "false",
                    ["CodeyBox:AgentClasses:0:Id"] = "frontier",
                    ["CodeyBox:AgentClasses:0:DisplayName"] = "Frontier",
                    ["CodeyBox:AgentClasses:0:Members:0:Agent"] = "claude",
                    ["CodeyBox:AgentClasses:0:Members:0:Billing"] = "PayPerApi",
                    ["CodeyBox:AgentClasses:0:Members:0:QualityScore"] = "100",
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IProjectRepository>();
                services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository());
                services.RemoveAll<IInVmSmokeGate>();
                services.AddSingleton<IInVmSmokeGate>(Gate);
                services.RemoveAll<ITimingStore>();
                services.AddSingleton<ITimingStore, NoopTimingStore>();
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                try { File.Delete(_dbPath); } catch { /* best-effort */ }
            base.Dispose(disposing);
        }
    }

    private sealed class RecordingGate : IInVmSmokeGate
    {
        public int EnsureCalls { get; private set; }
        public AgentAvailability EnsureResult { get; set; } = new(false, "gate should be bypassed", null);
        public bool Enabled => true;

        public Task<AgentAvailability> EnsureAvailableAsync(
            AgentKind kind,
            InVmSmokeSandboxTarget target,
            CancellationToken ct)
        {
            EnsureCalls++;
            return Task.FromResult(EnsureResult);
        }

        public Task ProbeAllAsync(CancellationToken ct) => Task.CompletedTask;
        public Task ProbeAllAsync(InVmSmokeSandboxTarget target, CancellationToken ct) => Task.CompletedTask;

        public Task<AgentAvailability?> ForceProbeAsync(AgentKind kind, CancellationToken ct) =>
            Task.FromResult<AgentAvailability?>(EnsureResult);
    }

    private sealed class NoopTimingStore : ITimingStore
    {
        public Task BeginAsync(TimingRecord record, CancellationToken ct = default) => Task.CompletedTask;

        public Task EndAsync(string id, DateTimeOffset endedAt, long durationMs, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<TimingRecord>> GetByWorkItemAsync(WorkItemId id, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TimingRecord>>([]);

        public Task DeleteByWorkItemAsync(WorkItemId id, CancellationToken ct = default) => Task.CompletedTask;

        public async IAsyncEnumerable<TimingRecord> StreamCompletedAsync(
            int workItemLimit,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
