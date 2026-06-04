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
    public void ProgramWiresSmokeConsumersToSharedSnapshotSingleton()
    {
        using var factory = new SmokeOptionsSnapshotWiringFactory();
        var snapshot = factory.Services.GetRequiredService<SmokeOptionsSnapshot>();
        var dispatchAvailability = factory.Services.GetRequiredService<IAgentDispatchAvailability>();

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
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(), $"codeybox-smoke-snapshot-wiring-{Guid.NewGuid():N}.db");

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
                    ["CodeyBox:Smoke:Enabled"] = "false",
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IProjectRepository>();
                services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository());
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
