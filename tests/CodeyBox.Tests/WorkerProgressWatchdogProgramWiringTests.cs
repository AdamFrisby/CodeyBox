using System.Reflection;
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

namespace CodeyBox.Tests;

public sealed class WorkerProgressWatchdogProgramWiringTests
{
    [Fact]
    public void ProgramWiresActivitySourceIntoWorkerProgressWatchdog()
    {
        using var factory = new WorkerProgressWatchdogWiringFactory(new ProgressProvider());

        var activitySource = factory.Services.GetRequiredService<IWorkerProgressActivitySource>();
        var progressProvider = factory.Services.GetRequiredService<IActiveSandboxProgressProvider>();
        var watchdog = factory.Services.GetRequiredService<WorkerProgressWatchdog>();

        Assert.Same(factory.Provider, progressProvider);
        Assert.Same(activitySource, FieldValue(watchdog, "_activitySource"));

        var defaultSource = Assert.IsType<DefaultWorkerProgressActivitySource>(activitySource);
        Assert.Same(progressProvider, FieldValue(defaultSource, "_activeSandboxProvider"));
    }

    [Fact]
    public void ProgramFallsBackToNullActiveSandboxProgressProvider()
    {
        using var factory = new WorkerProgressWatchdogWiringFactory(new PlainProvider());

        var activitySource = factory.Services.GetRequiredService<IWorkerProgressActivitySource>();
        var progressProvider = factory.Services.GetRequiredService<IActiveSandboxProgressProvider>();

        Assert.Same(NullActiveSandboxProgressProvider.Instance, progressProvider);

        var defaultSource = Assert.IsType<DefaultWorkerProgressActivitySource>(activitySource);
        Assert.Same(progressProvider, FieldValue(defaultSource, "_activeSandboxProvider"));
    }

    private static object? FieldValue(object instance, string name)
    {
        var field = instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return field.GetValue(instance);
    }

    private sealed class WorkerProgressWatchdogWiringFactory(ISandboxProvider provider) : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(), $"codeybox-progress-watchdog-wiring-{Guid.NewGuid():N}.db");

        public ISandboxProvider Provider { get; } = provider;

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
                    ["CodeyBox:WorkerProgressWatchdog:ProgressTimeout"] = "00:30:00",
                    ["CodeyBox:WorkerProgressWatchdog:CheckInterval"] = "00:01:00",
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<ISandboxProvider>();
                services.AddSingleton<ISandboxProvider>(Provider);
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                try { File.Delete(_dbPath); } catch { }
            base.Dispose(disposing);
        }
    }

    public sealed class ProgressProvider : ISandboxProvider, IActiveSandboxProgressProvider
    {
        public string Name => "progress-provider-test";

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);

        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;

        public IReadOnlyList<ActiveSandboxProgress> SnapshotActiveSandboxProgress() => [];
    }

    public sealed class PlainProvider : ISandboxProvider
    {
        public string Name => "plain-provider-test";

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);

        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }
}
