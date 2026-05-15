using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Sandbox.Multipass;

namespace CodeyBox.Tests.Uat.SandboxProviders;

/// <summary>
/// UAT coverage for <c>Multipass sandbox provider - Runs agents in isolated Ubuntu VMs with host-enforced network profiles</c>.
/// Plan anchor: docs/uat/00-plan.md#multipass-sandbox-provider---runs-agents-in-isolated-ubuntu-vms-with-host-enforced-network-profiles
/// </summary>
public sealed class MultipassSandboxProviderTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-uat-multipass-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, recursive: true);
    }

    [Fact]
    public void CloudInit_InstallsExecWrapperAndRouteServiceWithoutGuestFirewallRules()
    {
        var cloudInit = MultipassSandboxProvider.BuildCloudInit(
            ["apt-get update", "npm install -g @anthropic-ai/claude-code"],
            "packages:\n  - git\n");

        Assert.StartsWith("#cloud-config", cloudInit, StringComparison.Ordinal);
        Assert.Contains("path: /usr/local/bin/codeybox-exec", cloudInit);
        Assert.Contains("path: /usr/local/sbin/codeybox-route", cloudInit);
        Assert.Contains("path: /etc/systemd/system/codeybox-route.service", cloudInit);
        Assert.Contains("systemctl enable --now codeybox-route.service", cloudInit);
        Assert.Contains("apt-get update", cloudInit);
        Assert.Contains("npm install -g @anthropic-ai/claude-code", cloudInit);
        Assert.Contains("packages:\n  - git", cloudInit);
        Assert.DoesNotContain("iptables", cloudInit, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ufw", cloudInit, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LaunchArgv_MapsConfiguredProfileToHostBridgeAndRejectsUnknownProfiles()
    {
        var provider = NewProvider(networkProfiles: new Dictionary<string, string>
        {
            ["claude"] = "cb-claude",
        });
        var spec = new SandboxSpec
        {
            ImageReference = "24.04",
            Network = new SandboxNetworkPolicy { ProfileName = "claude" },
            Limits = new SandboxResourceLimits
            {
                CpuCount = 4,
                MemoryBytes = 8L * 1024 * 1024 * 1024,
                DiskBytes = 20L * 1024 * 1024 * 1024,
            },
        };

        var argv = provider.BuildLaunchArgv("codeybox-test", spec, "/staging/cloud-init.yaml");

        Assert.Equal("multipass", argv[0]);
        Assert.Contains("--cpus", argv);
        Assert.Contains("4", argv);
        var argvList = argv.ToList();
        var networkIndex = argvList.IndexOf("--network");
        Assert.True(networkIndex > 0, string.Join(' ', argv));
        Assert.Equal("name=cb-claude,mode=auto", argv[networkIndex + 1]);
        Assert.True(argvList.IndexOf("24.04") > networkIndex);

        var missing = new SandboxSpec
        {
            ImageReference = "ignored",
            Network = new SandboxNetworkPolicy { ProfileName = "missing" },
        };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            provider.BuildLaunchArgv("codeybox-test", missing, "/staging/cloud-init.yaml"));
        Assert.Contains("missing", ex.Message);
        Assert.Contains("claude", ex.Message);
    }

    [Fact]
    public void StagingRoot_IsCreatedWithOperatorOnlyPermissions()
    {
        if (OperatingSystem.IsWindows()) return;
        var staging = Path.Combine(_workspace, "staging");

        _ = NewProvider(stagingDirectory: staging);

        var mode = File.GetUnixFileMode(staging);
        Assert.True(mode.HasFlag(UnixFileMode.UserRead));
        Assert.True(mode.HasFlag(UnixFileMode.UserWrite));
        Assert.True(mode.HasFlag(UnixFileMode.UserExecute));
        Assert.False(mode.HasFlag(UnixFileMode.GroupRead), mode.ToString());
        Assert.False(mode.HasFlag(UnixFileMode.OtherRead), mode.ToString());
    }

    [Fact]
    public async Task DisposeLeakedAsync_RejectsUnsafeVmNamesBeforeShellingOut()
    {
        var runner = new RecordingMultipassRunner((_, _, _) =>
            Task.FromResult(new RunResult(0, "", "")));
        var provider = NewProvider(runner: runner);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            provider.DisposeLeakedAsync("codeybox-bad/../../../escape", CancellationToken.None));

        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task ListAllManagedAsync_FiltersCodeyboxVmsAddsDiskInfoAndUsesTtlCache()
    {
        var staging = Path.Combine(_workspace, "staging");
        Directory.CreateDirectory(Path.Combine(staging, "codeybox-alpha"));
        Directory.CreateDirectory(Path.Combine(staging, "codeybox-beta"));
        await File.WriteAllTextAsync(Path.Combine(staging, "codeybox-beta", ".codeybox-preempt"), "preserved");

        var listCalls = 0;
        var infoCalls = 0;
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "list", "--format", "json"])
            {
                listCalls++;
                return Task.FromResult(new RunResult(0, """
                    {"list":[
                      {"name":"primary"},
                      {"name":"cb-baseline-claude"},
                      {"name":"codeybox-alpha"},
                      {"name":"codeybox-beta"},
                      {"name":"codeybox-invalid.name"}
                    ]}
                    """, ""));
            }

            if (argv.Count >= 4 && argv[1] == "info")
            {
                infoCalls++;
                return Task.FromResult(new RunResult(0, """
                    {"info":{
                      "codeybox-alpha":{"disks":{"sda1":{"used":"1048576"}}},
                      "codeybox-beta":{"disks":{"sda1":{"used":"2097152"}}}
                    }}
                    """, ""));
            }

            return Task.FromResult(new RunResult(99, "", "unexpected argv: " + string.Join(' ', argv)));
        });
        var provider = NewProvider(stagingDirectory: staging, runner: runner);

        var first = await provider.ListAllManagedAsync(CancellationToken.None);
        var second = await provider.ListAllManagedAsync(CancellationToken.None);

        Assert.Same(first, second);
        Assert.Equal(1, listCalls);
        Assert.Equal(1, infoCalls);
        Assert.Equal(["codeybox-alpha", "codeybox-beta"], first.Select(s => s.Name).ToArray());
        Assert.All(first, s => Assert.NotNull(s.CreatedAt));
        Assert.Equal(1024 * 1024, first.Single(s => s.Name == "codeybox-alpha").DiskBytes);
        Assert.True(first.Single(s => s.Name == "codeybox-beta").HasPreemptMarker);
    }

    [Fact]
    public async Task BaselineImages_BakeOncePerProfileUnderConcurrentCreatesThenCloneSandboxes()
    {
        var launchEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowLaunch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var baselineLaunches = 0;
        var cloneCount = 0;
        var installCount = 0;

        var runner = new RecordingMultipassRunner(async (argv, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();

            if (argv is [_, "info", var name, "--format=csv"])
            {
                if (states.TryGetValue(name, out var state))
                    return new RunResult(0, state, "");
                return new RunResult(1, "", "not found");
            }

            if (argv.Count >= 4 && argv[1] == "launch" && argv[2] == "--name")
            {
                var launchName = argv[3];
                if (launchName.StartsWith("cb-baseline-", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref baselineLaunches);
                    states[launchName] = "Running";
                    launchEntered.TrySetResult();
                    await allowLaunch.Task.WaitAsync(ct);
                }
                return new RunResult(0, "", "");
            }

            if (argv is [_, "exec", var execName, "--", "cloud-init", "status", "--wait"])
                return new RunResult(states.ContainsKey(execName) ? 0 : 1, "", "");

            if (argv is [_, "exec", var installName, "--", "sudo", "bash", "-c", ..]
                && installName.StartsWith("cb-baseline-", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref installCount);
                return new RunResult(0, "", "");
            }

            if (argv is [_, "stop", var stopName])
            {
                states[stopName] = "Stopped";
                return new RunResult(0, "", "");
            }

            if (argv is [_, "clone", var source, "--name", var cloneName])
            {
                Assert.StartsWith("cb-baseline-", source, StringComparison.Ordinal);
                states[cloneName] = "Stopped";
                Interlocked.Increment(ref cloneCount);
                return new RunResult(0, "", "");
            }

            if (argv is [_, "start", var startName])
            {
                states[startName] = "Running";
                return new RunResult(0, "", "");
            }

            if (argv is [_, "transfer", _, var destination]
                && destination.EndsWith(":.codeybox-env", StringComparison.Ordinal))
                return new RunResult(0, "", "");

            if (argv is [_, "exec", _, "--", "chmod", "0600", "/home/ubuntu/.codeybox-env"])
                return new RunResult(0, "", "");

            return new RunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv));
        });
        var provider = NewProvider(
            stagingDirectory: Path.Combine(_workspace, "staging"),
            networkProfiles: new Dictionary<string, string> { ["claude"] = "cb-claude" },
            useBaselineImages: true,
            extraRuncmd: ["touch /opt/codeybox-baseline"],
            runner: runner);
        var spec = new SandboxSpec
        {
            ImageReference = "ignored",
            Network = new SandboxNetworkPolicy { ProfileName = "claude" },
            WorkingDirectory = "/work",
        };

        var firstCreate = provider.CreateAsync(spec, CancellationToken.None);
        await launchEntered.Task;
        var secondCreate = provider.CreateAsync(spec, CancellationToken.None);
        Assert.Equal(1, Volatile.Read(ref baselineLaunches));

        allowLaunch.SetResult();
        var sandboxes = await Task.WhenAll(firstCreate, secondCreate);
        await sandboxes[0].DisposeAsync();
        await sandboxes[1].DisposeAsync();

        Assert.Equal(1, baselineLaunches);
        Assert.Equal(1, installCount);
        Assert.Equal(2, cloneCount);
    }

    [Fact]
    public async Task CreateAsync_RetriesTransientMultipassSocketLaunchFailureAndSucceeds()
    {
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var launchCalls = 0;
        var versionCalls = 0;

        var runner = new RecordingMultipassRunner((argv, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();

            if (argv is [_, "version"])
            {
                versionCalls++;
                return Task.FromResult(new RunResult(0, "multipass 1.15.0", ""));
            }

            if (argv.Count >= 4 && argv[1] == "launch" && argv[2] == "--name")
            {
                launchCalls++;
                if (launchCalls == 1)
                    return Task.FromResult(new RunResult(1, "", "cannot connect to the multipass socket"));
                states[argv[3]] = "Running";
                return Task.FromResult(new RunResult(0, "", ""));
            }

            if (argv is [_, "info", var name, "--format=csv"])
            {
                var state = states.TryGetValue(name, out var current) ? current : "Running";
                return Task.FromResult(new RunResult(0, state, ""));
            }

            if (argv is [_, "exec", _, "--", "cloud-init", "status", "--wait"])
                return Task.FromResult(new RunResult(0, "", ""));

            if (argv is [_, "stop", var stopName])
            {
                states[stopName] = "Stopped";
                return Task.FromResult(new RunResult(0, "", ""));
            }

            if (argv is [_, "start", var startName])
            {
                states[startName] = "Running";
                return Task.FromResult(new RunResult(0, "", ""));
            }

            if (argv is [_, "transfer", _, var destination]
                && destination.EndsWith(":.codeybox-env", StringComparison.Ordinal))
                return Task.FromResult(new RunResult(0, "", ""));

            if (argv is [_, "exec", _, "--", "chmod", "0600", "/home/ubuntu/.codeybox-env"])
                return Task.FromResult(new RunResult(0, "", ""));

            return Task.FromResult(new RunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
        var logger = new RecordingLogger<MultipassSandboxProvider>();
        var provider = NewProvider(
            stagingDirectory: Path.Combine(_workspace, "staging"),
            runner: runner,
            logger: logger,
            daemonRetryPolicy: InstantDaemonRetryPolicy());

        var sandbox = await provider.CreateAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            TimingWorkItemId = WorkItemId.New(),
        });
        await sandbox.DisposeAsync();

        Assert.Equal(2, launchCalls);
        Assert.Equal(1, versionCalls);
        Assert.Contains(logger.Entries, e =>
            e.Level == Microsoft.Extensions.Logging.LogLevel.Information
            && e.Message.Contains("transient multipass daemon error", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateAsync_TransientMultipassSocketLaunchFailureExhaustsRetriesWithClearMessage()
    {
        var launchCalls = 0;
        var versionCalls = 0;

        var runner = new RecordingMultipassRunner((argv, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();

            if (argv is [_, "version"])
            {
                versionCalls++;
                return Task.FromResult(new RunResult(1, "", "cannot connect to the multipass socket"));
            }

            if (argv.Count >= 2 && argv[1] == "launch")
            {
                launchCalls++;
                return Task.FromResult(new RunResult(1, "", "cannot connect to the multipass socket"));
            }

            if (argv.Count >= 2 && argv[1] == "delete")
                return Task.FromResult(new RunResult(0, "", ""));

            return Task.FromResult(new RunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
        var logger = new RecordingLogger<MultipassSandboxProvider>();
        var provider = NewProvider(
            stagingDirectory: Path.Combine(_workspace, "staging"),
            runner: runner,
            logger: logger,
            daemonRetryPolicy: InstantDaemonRetryPolicy());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.CreateAsync(new SandboxSpec
            {
                ImageReference = "ignored",
                TimingWorkItemId = WorkItemId.New(),
            }));

        Assert.Contains("multipass daemon unreachable after 2 retries", ex.Message);
        Assert.Equal(3, launchCalls);
        Assert.Equal(3, versionCalls);
        Assert.Contains(logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
        Assert.Contains(logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error);
    }

    [Fact]
    public async Task RetryHelper_RetriesTransientSshReadinessWithoutRealDelays()
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();

        var result = await MultipassRetry.RunWithRetryAsync(
            action: _ => Task.FromResult(++attempts == 1
                ? new RunResult(1, "", "ssh connection failed: Connection refused")
                : new RunResult(0, "ok", "")),
            log: NullLogger.Instance,
            description: "uat transfer",
            ct: CancellationToken.None,
            delay: (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ok", result.Stdout);
        Assert.Equal(2, attempts);
        Assert.Equal([MultipassRetry.DefaultInitialDelay], delays);
    }

    private static MultipassSandboxProvider NewProvider(
        string? stagingDirectory = null,
        IReadOnlyDictionary<string, string>? networkProfiles = null,
        bool useBaselineImages = false,
        IReadOnlyList<string>? extraRuncmd = null,
        RecordingMultipassRunner? runner = null,
        RecordingLogger<MultipassSandboxProvider>? logger = null,
        MultipassDaemonRetryPolicy? daemonRetryPolicy = null)
    {
        var options = new MultipassSandboxOptions
        {
            MultipassBinary = runner is null ? "multipass" : "/bin/false",
            StagingDirectory = stagingDirectory,
            NetworkProfiles = networkProfiles ?? new Dictionary<string, string>(),
            UseBaselineImages = useBaselineImages,
            ExtraRuncmd = extraRuncmd ?? [],
        };
        Microsoft.Extensions.Logging.ILogger<MultipassSandboxProvider> resolvedLogger = logger is not null
            ? logger
            : NullLogger<MultipassSandboxProvider>.Instance;

        return runner is null
            ? new MultipassSandboxProvider(options, resolvedLogger)
            : new MultipassSandboxProvider(
                options,
                resolvedLogger,
                null,
                runner,
                daemonRetryPolicy);
    }

    private static MultipassDaemonRetryPolicy InstantDaemonRetryPolicy() => new()
    {
        Delay = (_, _) => Task.CompletedTask,
        HealthProbeTimeout = TimeSpan.FromMilliseconds(100),
    };
}
