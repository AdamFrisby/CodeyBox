using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.HostProcess;
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

    [Fact]
    public async Task MultipassRemoteProvider_routes_selected_host_options_to_open_ssh_transport()
    {
        var runner = new RecordingSshProcessRunner();
        var monitor = new MutableOptionsMonitor<CodeyBoxOptions>(OptionsFor(
            HostConfig(
                "a",
                "exec-a.example",
                cap: 1,
                cordoned: false,
                healthy: true,
                sshBinary: "ssh-a",
                sshPort: 2201,
                sshKeyPath: "/tmp/codeybox-key-a",
                extraSshOptions: ["UserKnownHostsFile=/tmp/codeybox-known-a"]),
            HostConfig(
                "b",
                "exec-b.example",
                cap: 1,
                cordoned: false,
                healthy: true,
                sshBinary: "ssh-b",
                sshPort: 2202,
                sshKeyPath: "/tmp/codeybox-key-b",
                extraSshOptions: ["UserKnownHostsFile=/tmp/codeybox-known-b"])));
        using var factory = new MultipassRemoteHotReloadFactory(monitor, runner);

        var provider = factory.Services.GetRequiredService<ISandboxProvider>();
        var first = await provider.CreateAsync(new SandboxSpec { ImageReference = "24.04" });
        var second = await provider.CreateAsync(new SandboxSpec { ImageReference = "24.04" });
        try
        {
            var launchCalls = runner.Snapshot()
                .Where(static c => c.RemoteCommand.Contains("'launch'", StringComparison.Ordinal))
                .ToArray();

            Assert.Equal(2, launchCalls.Length);
            Assert.Contains(launchCalls, c =>
                c.Target == "exec-a.example"
                && c.Argv[0] == "ssh-a"
                && ContainsArgPair(c.Argv, "-p", "2201")
                && ContainsArgPair(c.Argv, "-i", "/tmp/codeybox-key-a")
                && ContainsArgPair(c.Argv, "-o", "UserKnownHostsFile=/tmp/codeybox-known-a"));
            Assert.Contains(launchCalls, c =>
                c.Target == "exec-b.example"
                && c.Argv[0] == "ssh-b"
                && ContainsArgPair(c.Argv, "-p", "2202")
                && ContainsArgPair(c.Argv, "-i", "/tmp/codeybox-key-b")
                && ContainsArgPair(c.Argv, "-o", "UserKnownHostsFile=/tmp/codeybox-known-b"));
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
        }
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
        IList<string>? allowedProfiles = null,
        string? sshBinary = null,
        int? sshPort = null,
        string? sshKeyPath = null,
        IList<string>? extraSshOptions = null) => new()
        {
            Id = id,
            SshTarget = target,
            SshBinary = sshBinary,
            SshPort = sshPort,
            SshKeyPath = sshKeyPath,
            ExtraSshOptions = extraSshOptions,
            MaxConcurrentSandboxes = cap,
            Cordoned = cordoned,
            Healthy = healthy,
            AllowedNetworkProfiles = allowedProfiles,
        };

    private sealed class MultipassRemoteHotReloadFactory(
        MutableOptionsMonitor<CodeyBoxOptions> monitor,
        IProcessRunner? runner = null) : WebApplicationFactory<Program>
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
                if (runner is not null)
                {
                    services.RemoveAll<IProcessRunner>();
                    services.AddSingleton(runner);
                }
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

    private static bool ContainsArgPair(IReadOnlyList<string> argv, string option, string value)
    {
        for (var i = 0; i + 1 < argv.Count; i++)
        {
            if (argv[i] == option && argv[i + 1] == value)
                return true;
        }

        return false;
    }

    private sealed record RecordedSshRun(IReadOnlyList<string> Argv, string Target, string RemoteCommand);

    private sealed class RecordingSshProcessRunner : IProcessRunner
    {
        private readonly object _gate = new();
        private readonly List<RecordedSshRun> _runs = [];
        private readonly Dictionary<string, string> _lastVmByTarget = new(StringComparer.Ordinal);

        public IReadOnlyList<RecordedSshRun> Snapshot()
        {
            lock (_gate)
                return _runs.ToArray();
        }

        public Task<ProcessRunResult> RunAsync(
            IReadOnlyList<string> argv,
            string? stdin,
            CancellationToken ct,
            Action<string>? stdoutChunkCallback = null,
            Action<string>? stderrChunkCallback = null,
            int? maxStdoutBytes = null,
            int? maxStderrBytes = null,
            IReadOnlyDictionary<string, string>? environment = null,
            bool killOnOutputLimit = true)
        {
            _ = stdin;
            _ = ct;
            _ = stdoutChunkCallback;
            _ = stderrChunkCallback;
            _ = maxStdoutBytes;
            _ = maxStderrBytes;
            _ = environment;
            _ = killOnOutputLimit;

            if (argv.Count < 3)
                return Task.FromResult(new ProcessRunResult(2, "", "ssh argv too short"));

            var target = argv[^2];
            var remoteCommand = argv[^1];
            lock (_gate)
                _runs.Add(new RecordedSshRun(argv.ToArray(), target, remoteCommand));

            if (remoteCommand.Contains("'list'", StringComparison.Ordinal))
                return Task.FromResult(new ProcessRunResult(0, """{"list":[]}""", ""));

            if (remoteCommand.Contains("'launch'", StringComparison.Ordinal))
            {
                var vmName = ExtractQuotedArgumentAfter(remoteCommand, "'--name'")
                    ?? throw new InvalidOperationException("launch command did not include --name");
                lock (_gate)
                    _lastVmByTarget[target] = vmName;
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (remoteCommand.Contains("'info'", StringComparison.Ordinal))
            {
                var vmName = ExtractQuotedArgumentAfter(remoteCommand, "'info'");
                if (string.IsNullOrWhiteSpace(vmName))
                {
                    lock (_gate)
                        _lastVmByTarget.TryGetValue(target, out vmName);
                }

                vmName ??= "unknown";
                return Task.FromResult(new ProcessRunResult(
                    0,
                    $"{{\"info\":{{\"{vmName}\":{{\"state\":\"Running\"}}}}}}",
                    ""));
            }

            return Task.FromResult(new ProcessRunResult(0, "", ""));
        }

        private static string? ExtractQuotedArgumentAfter(string remoteCommand, string marker)
        {
            var markerIndex = remoteCommand.IndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0)
                return null;

            var quoteIndex = remoteCommand.IndexOf('\'', markerIndex + marker.Length);
            if (quoteIndex < 0 || quoteIndex + 1 >= remoteCommand.Length)
                return null;

            var end = remoteCommand.IndexOf('\'', quoteIndex + 1);
            return end < 0 ? null : remoteCommand[(quoteIndex + 1)..end];
        }
    }
}
