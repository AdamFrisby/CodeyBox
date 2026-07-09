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

[Collection("GlobalSerilog")]
public sealed class MultipassRemoteProgramWiringTests
{
    [Fact]
    public void MultipassRemoteProvider_reads_hot_reloaded_host_pool_from_options_monitor()
    {
        var monitor = new MutableOptionsMonitor<CodeyBoxOptions>(OptionsFor(
            HostConfig("initial", "initial.example", cap: 1, cordoned: true, healthy: true)));
        using var factory = new MultipassRemoteHotReloadFactory(monitor);

        var provider = factory.Services.GetRequiredService<ISandboxProvider>();
        var hostPool = Assert.IsAssignableFrom<ISandboxHostPoolSnapshot>(provider);
        var initial = Assert.Single(hostPool.SnapshotHostPool());
        Assert.Equal("initial", initial.HostId);
        Assert.Equal(1, initial.Capacity);
        Assert.True(initial.Cordoned);

        monitor.Set(OptionsFor(
            HostConfig("a", "a.example", cap: 2, cordoned: false, healthy: false),
            HostConfig("b", "b.example", cap: 3, cordoned: true, healthy: true, allowedProfiles: ["work"])));

        var reloaded = hostPool.SnapshotHostPool()
            .OrderBy(static h => h.HostId, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["a", "b"], reloaded.Select(static h => h.HostId).ToArray());
        Assert.Equal(2, reloaded[0].Capacity);
        Assert.False(reloaded[0].ConfiguredHealthy);
        Assert.False(reloaded[0].Cordoned);
        Assert.Equal(3, reloaded[1].Capacity);
        Assert.True(reloaded[1].ConfiguredHealthy);
        Assert.True(reloaded[1].Cordoned);
        Assert.Equal(["work"], reloaded[1].AllowedNetworkProfiles);
    }

    private static CodeyBoxOptions OptionsFor(params MultipassRemoteExecutorHostConfig[] hosts) => new()
    {
        SandboxProvider = "multipass-remote",
        WorkerPool = new WorkerPoolOptions
        {
            MaxConcurrentWorkers = 4,
            MaxConcurrentSandboxes = 4,
        },
        SandboxNetworkProfiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["work"] = "cb-work",
        },
        MultipassRemoteSandbox = new MultipassRemoteSandboxConfig
        {
            SshTarget = "unused-default",
            RemoteMultipassPath = "/snap/bin/multipass",
            RemoteStagingRoot = "/remote/staging",
            ExecutorHosts = hosts.ToList(),
        },
    };

    private static MultipassRemoteExecutorHostConfig HostConfig(
        string id,
        string target,
        int cap,
        bool cordoned,
        bool healthy,
        IList<string>? allowedProfiles = null) => new()
        {
            Id = id,
            SshTarget = target,
            MaxConcurrentSandboxes = cap,
            Cordoned = cordoned,
            Healthy = healthy,
            AllowedNetworkProfiles = allowedProfiles,
        };

    private sealed class MultipassRemoteHotReloadFactory(
        MutableOptionsMonitor<CodeyBoxOptions> monitor) : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(),
            $"codeybox-multipass-remote-hot-reload-{Guid.NewGuid():N}.db");

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
                    ["CodeyBox:SandboxProvider"] = "multipass-remote",
                    ["CodeyBox:StateDatabasePath"] = _dbPath,
                    ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"multipass-remote-git-{Guid.NewGuid():N}"),
                    ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"multipass-remote-log-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"multipass-remote-audit-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AgentStreams:Path"] = Path.Combine(tmp, $"multipass-remote-streams-{Guid.NewGuid():N}"),
                    ["CodeyBox:WorkerPool:MaxConcurrentWorkers"] = "4",
                    ["CodeyBox:WorkerPool:MaxConcurrentSandboxes"] = "4",
                    ["CodeyBox:MultipassRemoteSandbox:SshTarget"] = "initial.example",
                    ["CodeyBox:MultipassRemoteSandbox:RemoteMultipassPath"] = "/snap/bin/multipass",
                    ["CodeyBox:MultipassRemoteSandbox:RemoteStagingRoot"] = "/remote/staging",
                    ["CodeyBox:MultipassRemoteSandbox:ExecutorHosts:0:Id"] = "initial",
                    ["CodeyBox:MultipassRemoteSandbox:ExecutorHosts:0:SshTarget"] = "initial.example",
                    ["CodeyBox:MultipassRemoteSandbox:ExecutorHosts:0:MaxConcurrentSandboxes"] = "1",
                    ["CodeyBox:MultipassRemoteSandbox:ExecutorHosts:0:Cordoned"] = "true",
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IOptionsMonitor<CodeyBoxOptions>>();
                services.AddSingleton<IOptionsMonitor<CodeyBoxOptions>>(monitor);
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                try { File.Delete(_dbPath); } catch { }
            base.Dispose(disposing);
        }
    }

    private sealed class MutableOptionsMonitor<T>(T initial) : IOptionsMonitor<T>
    {
        private readonly object _gate = new();
        private T _current = initial;

        public T CurrentValue
        {
            get
            {
                lock (_gate)
                    return _current;
            }
        }

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener)
        {
            _ = listener;
            return NoopDisposable.Instance;
        }

        public void Set(T value)
        {
            lock (_gate)
                _current = value;
        }

        private sealed class NoopDisposable : IDisposable
        {
            public static readonly NoopDisposable Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
