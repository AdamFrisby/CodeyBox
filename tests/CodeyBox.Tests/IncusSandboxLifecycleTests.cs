using CodeyBox.Core;
using CodeyBox.HostProcess;
using CodeyBox.Sandbox.Incus;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

public sealed class IncusSandboxLifecycleTests
{
    [Fact]
    public void ParseOwnedInstancePresence_AcceptsOnlyExactOwnedSandbox()
    {
        const string json = """
            [
              {"name":"cb-work-prefix","type":"virtual-machine","config":{"user.codeybox.managed":"true","user.codeybox.kind":"sandbox"}},
              {"name":"cb-work","type":"virtual-machine","config":{"user.codeybox.managed":"true","user.codeybox.kind":"sandbox"}}
            ]
            """;

        Assert.True(IncusSandbox.ParseOwnedInstancePresence(json, "cb-work"));
        Assert.False(IncusSandbox.ParseOwnedInstancePresence(json, "missing"));
    }

    [Theory]
    [InlineData("false", "sandbox")]
    [InlineData("true", "baseline")]
    public void ParseOwnedInstancePresence_RejectsChangedOwnership(string managed, string kind)
    {
        var json =
            $"[{{\"name\":\"cb-work\",\"type\":\"virtual-machine\",\"config\":{{\"user.codeybox.managed\":\"{managed}\",\"user.codeybox.kind\":\"{kind}\"}}}}]";

        Assert.Throws<InvalidOperationException>(() =>
            IncusSandbox.ParseOwnedInstancePresence(json, "cb-work"));
    }

    [Fact]
    public void ParseOwnedInstancePresence_RejectsDuplicateExactNames()
    {
        const string json = """
            [
              {"name":"cb-work","type":"virtual-machine","config":{"user.codeybox.managed":"true","user.codeybox.kind":"sandbox"}},
              {"name":"cb-work","type":"virtual-machine","config":{"user.codeybox.managed":"true","user.codeybox.kind":"sandbox"}}
            ]
            """;

        Assert.Throws<InvalidOperationException>(() =>
            IncusSandbox.ParseOwnedInstancePresence(json, "cb-work"));
    }

    [Fact]
    public async Task Dispose_CapturesAndPersistsResourceMetricsBeforeCheckedCleanup()
    {
        const string sandboxName = "codeybox-metrics-test";
        var root = Path.Combine(Path.GetTempPath(), $"codeybox-incus-metrics-{Guid.NewGuid():N}");
        var sandboxRoot = Path.Combine(root, sandboxName);
        Directory.CreateDirectory(sandboxRoot);
        IncusMountStaging.InitializeOwnedTree(sandboxRoot, sandboxName, DateTimeOffset.UtcNow);
        var runner = new MetricsLifecycleRunner(sandboxName);
        var store = new RecordingUsageStore();
        var workItemId = WorkItemId.New();
        var disposed = 0;
        var options = new IncusSandboxOptions
        {
            CaptureResourceMetrics = true,
            DiskGuard = null,
        };
        var sandbox = new IncusSandbox(
            sandboxName,
            sandboxRoot,
            root,
            new SandboxSpec
            {
                ImageReference = "local-image",
                Network = new SandboxNetworkPolicy { ProfileName = "internet-only" },
            },
            options,
            new IncusCliRunner(runner),
            NullLogger.Instance,
            timings: null,
            workItemId,
            "work",
            "baseline-ref",
            store,
            _ => Interlocked.Increment(ref disposed));
        var liveBefore = SandboxLiveCounter.Active;
        SandboxLiveCounter.Increment();

        await sandbox.DisposeAsync();

        Assert.Equal(liveBefore, SandboxLiveCounter.Active);
        Assert.Equal(1, disposed);
        Assert.False(Directory.Exists(sandboxRoot));
        Assert.Contains(runner.Commands, command => command.Contains("delete", StringComparer.Ordinal));
        var metrics = Assert.IsType<SandboxResourceMetrics>(sandbox.ResourceMetrics);
        Assert.Equal(12.5, metrics.UptimeSeconds);
        Assert.Equal(37.25, metrics.AvgCpuPercent);
        Assert.Equal(1048576, metrics.PeakRamBytes);
        var record = Assert.Single(store.Records);
        Assert.Equal(workItemId, record.WorkItemId);
        Assert.Equal(1, record.PeakRamMb);
        Assert.Equal("baseline-ref", record.BaselineRef);
    }

    [Fact]
    public async Task Dispose_WhenVmIsGoneButStagingCleanupFails_PoisonsExecAndRetriesCleanup()
    {
        const string sandboxName = "codeybox-cleanup-retry";
        var root = Path.Combine(Path.GetTempPath(), $"codeybox-incus-retry-{Guid.NewGuid():N}");
        var sandboxRoot = Path.Combine(root, sandboxName);
        Directory.CreateDirectory(sandboxRoot);
        IncusMountStaging.InitializeOwnedTree(sandboxRoot, sandboxName, DateTimeOffset.UtcNow);
        File.Delete(Path.Combine(sandboxRoot, ".codeybox-incus-owner"));
        var runner = new DeletionLifecycleRunner(sandboxName);
        var inactive = 0;
        var sandbox = new IncusSandbox(
            sandboxName,
            sandboxRoot,
            root,
            new SandboxSpec { ImageReference = "local-image" },
            new IncusSandboxOptions { CaptureResourceMetrics = false, DiskGuard = null },
            new IncusCliRunner(runner),
            NullLogger.Instance,
            timings: null,
            WorkItemId.New(),
            "work",
            baselineRef: null,
            resourceUsageStore: null,
            _ => Interlocked.Increment(ref inactive));
        var liveBefore = SandboxLiveCounter.Active;
        SandboxLiveCounter.Increment();

        await Assert.ThrowsAnyAsync<Exception>(() => sandbox.DisposeAsync().AsTask());
        Assert.Equal(1, inactive);
        Assert.Equal(liveBefore, SandboxLiveCounter.Active);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["true"],
        }));

        IncusMountStaging.InitializeOwnedTree(sandboxRoot, sandboxName, DateTimeOffset.UtcNow);
        await sandbox.DisposeAsync();

        Assert.False(Directory.Exists(sandboxRoot));
        Assert.Equal(1, runner.DeleteCalls);
        Assert.Equal(1, inactive);
        Assert.Equal(liveBefore, SandboxLiveCounter.Active);
    }

    [Fact]
    public async Task Dispose_WhenDeleteReportsSuccessButVmPersists_RetainsStagingUntilVerifiedRetry()
    {
        const string sandboxName = "codeybox-delete-pending";
        var root = Path.Combine(Path.GetTempPath(), $"codeybox-incus-delete-pending-{Guid.NewGuid():N}");
        var sandboxRoot = Path.Combine(root, sandboxName);
        Directory.CreateDirectory(sandboxRoot);
        IncusMountStaging.InitializeOwnedTree(sandboxRoot, sandboxName, DateTimeOffset.UtcNow);
        var runner = new StickyDeletionLifecycleRunner(sandboxName);
        var inactive = 0;
        var sandbox = new IncusSandbox(
            sandboxName,
            sandboxRoot,
            root,
            new SandboxSpec { ImageReference = "local-image" },
            new IncusSandboxOptions
            {
                CaptureResourceMetrics = false,
                DiskGuard = null,
                OperationTimeout = TimeSpan.FromMilliseconds(100),
                ReadinessPollInterval = TimeSpan.FromMilliseconds(10),
            },
            new IncusCliRunner(runner),
            NullLogger.Instance,
            timings: null,
            WorkItemId.New(),
            "work",
            baselineRef: null,
            resourceUsageStore: null,
            _ => Interlocked.Increment(ref inactive));
        var liveBefore = SandboxLiveCounter.Active;
        SandboxLiveCounter.Increment();

        await Assert.ThrowsAsync<TimeoutException>(() => sandbox.DisposeAsync().AsTask());

        Assert.True(Directory.Exists(sandboxRoot));
        Assert.Equal(0, inactive);
        Assert.Equal(liveBefore + 1, SandboxLiveCounter.Active);
        runner.CompleteDeletion = true;

        await sandbox.DisposeAsync();

        Assert.False(Directory.Exists(sandboxRoot));
        Assert.Equal(2, runner.DeleteCalls);
        Assert.Equal(1, inactive);
        Assert.Equal(liveBefore, SandboxLiveCounter.Active);
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task LeakReap_WhenDeleteReportsSuccessButVmPersists_DoesNotDeleteStaging()
    {
        const string sandboxName = "codeybox-leak-delete-pending";
        var root = Path.Combine(Path.GetTempPath(), $"codeybox-incus-leak-pending-{Guid.NewGuid():N}");
        var sandboxRoot = Path.Combine(root, sandboxName);
        IncusMountStaging.EnsureOwnedStagingRoot(root);
        Directory.CreateDirectory(sandboxRoot);
        IncusMountStaging.InitializeOwnedTree(sandboxRoot, sandboxName, DateTimeOffset.UtcNow);
        var runner = new StickyProviderDeletionRunner(sandboxName);
        var provider = new IncusSandboxProvider(
            () => new IncusSandboxOptions
            {
                StagingDirectory = root,
                DiskGuard = null,
                OperationTimeout = TimeSpan.FromMilliseconds(100),
                ReadinessPollInterval = TimeSpan.FromMilliseconds(10),
            },
            NullLogger<IncusSandboxProvider>.Instance,
            timings: null,
            runner);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            provider.DisposeLeakedAsync(sandboxName, CancellationToken.None));

        Assert.True(Directory.Exists(sandboxRoot));
        runner.CompleteDeletion = true;
        await provider.DisposeLeakedAsync(sandboxName, CancellationToken.None);

        Assert.False(Directory.Exists(sandboxRoot));
        Assert.Equal(2, runner.DeleteCalls);
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task Create_WhenDaemonCompletionIsUncertain_RetainsNamedStagingForReaper()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codeybox-incus-retained-{Guid.NewGuid():N}");
        IncusMountStaging.EnsureOwnedStagingRoot(root);
        var runner = new UncertainCreateRunner();
        var options = new IncusSandboxOptions
        {
            StagingDirectory = root,
            UseBaselineImages = false,
            DiskGuard = null,
        };
        var provider = new IncusSandboxProvider(
            () => options,
            NullLogger<IncusSandboxProvider>.Instance,
            timings: null,
            runner);

        var deferred = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            provider.CreateAsync(new SandboxSpec { ImageReference = "local-image" }));

        Assert.Equal("create-cleanup", deferred.Operation);
        Assert.False(string.IsNullOrWhiteSpace(deferred.RetainedSandboxName));
        var retained = Assert.Single(await provider.ListAllManagedAsync(CancellationToken.None));
        Assert.Equal(deferred.RetainedSandboxName, retained.Name);
        Assert.NotNull(retained.CreatedAt);
        Assert.True(Directory.Exists(Path.Combine(root, retained.Name)));

        await provider.DisposeLeakedAsync(retained.Name, CancellationToken.None);
        Assert.Empty(await provider.ListAllManagedAsync(CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(root, retained.Name)));
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task BaselineBake_WhenDaemonCompletionIsUncertain_RetainsCandidateAdmission()
    {
        var runner = new UncertainCreateRunner();
        var options = new IncusSandboxOptions
        {
            UseBaselineImages = true,
            DiskGuard = null,
            NetworkProfiles = new Dictionary<string, string>
            {
                ["internet-only"] = "cb-net",
            },
        };
        var provider = new IncusSandboxProvider(
            () => options,
            NullLogger<IncusSandboxProvider>.Instance,
            timings: null,
            runner);

        var deferred = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            provider.EnsureBaselineImageAsync(
                "internet-only",
                SandboxProfileFlavor.Headless,
                pinnedBaselineRef: null,
                CancellationToken.None));

        Assert.StartsWith("baseline-", deferred.Operation, StringComparison.Ordinal);
        var candidate = Assert.Single(await provider.ListBaselineImagesAsync(CancellationToken.None));
        Assert.Equal(deferred.RetainedSandboxName, candidate.Name);
        Assert.NotNull(candidate.CreatedAt);
        await provider.DisposeBaselineImageAsync(candidate.Name, CancellationToken.None);
        Assert.Empty(await provider.ListBaselineImagesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Exec_UsesExecTimeoutAndCleansVerifiedCompletionSentinel()
    {
        const string sandboxName = "codeybox-exec-timeout";
        var root = Path.Combine(Path.GetTempPath(), $"codeybox-incus-exec-timeout-{Guid.NewGuid():N}");
        var completionPullCalls = 0;
        var wrapperExecCalls = 0;
        var forcedStopCalls = 0;
        var runner = new ScriptedLifecycleRunner(async (argv, _, ct) =>
        {
            if (IsFileCommand(argv, "push"))
                return Success();
            if (IsGuestCommand(argv, IncusCloudInit.ExecWrapperPath))
            {
                wrapperExecCalls++;
                await Task.Delay(TimeSpan.FromMilliseconds(150), ct);
                return Success("completed\n");
            }
            if (IsFileCommand(argv, "pull") && argv.Any(argument => argument.Contains("/complete-", StringComparison.Ordinal)))
            {
                completionPullCalls++;
                return Success("0\n");
            }
            if (IsFileCommand(argv, "pull"))
                return Failure();
            if (IsFileCommand(argv, "delete"))
                return Success();
            if (IsGuestCommand(argv, "test"))
                return Success();
            if (argv.Contains("stop", StringComparer.Ordinal))
            {
                forcedStopCalls++;
                return Success();
            }
            if (argv.Contains("list", StringComparer.Ordinal))
                return OwnedInstanceList(sandboxName, "STOPPED");
            throw new InvalidOperationException($"Unexpected Incus exec test command: {string.Join(' ', argv)}");
        });
        var options = FastLifecycleOptions() with
        {
            OperationTimeout = TimeSpan.FromMilliseconds(25),
            ExecTimeout = TimeSpan.FromSeconds(2),
        };
        var sandbox = CreateSandbox(sandboxName, root, options, runner);

        try
        {
            var result = await sandbox.ExecAsync(new SandboxExec { Argv = ["true"] });

            Assert.True(result.Success, result.Stderr);
            Assert.Equal("completed\n", result.Stdout);
            Assert.Equal(1, wrapperExecCalls);
            Assert.Equal(1, completionPullCalls);
            Assert.Equal(0, forcedStopCalls);
            var deletedControlFiles = runner.Commands
                .Where(command => IsFileCommand(command, "delete"))
                .Select(command => command[^1])
                .ToArray();
            Assert.Equal(3, deletedControlFiles.Length);
            Assert.Contains(deletedControlFiles, path => path.Contains("/env-", StringComparison.Ordinal));
            Assert.Contains(deletedControlFiles, path => path.Contains("/pid-", StringComparison.Ordinal));
            Assert.Contains(deletedControlFiles, path => path.Contains("/complete-", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exec_OrdinaryFailureWithoutMatchingCompletionSentinel_StopsAndPoisons(
        bool returnMismatchedSentinel)
    {
        const string sandboxName = "codeybox-exec-sentinel";
        var root = Path.Combine(Path.GetTempPath(), $"codeybox-incus-exec-sentinel-{Guid.NewGuid():N}");
        var completionPullCalls = 0;
        var wrapperExecCalls = 0;
        var forcedStopCalls = 0;
        var stopped = false;
        var runner = new ScriptedLifecycleRunner((argv, _, _) =>
        {
            if (IsFileCommand(argv, "push"))
                return Task.FromResult(Success());
            if (IsGuestCommand(argv, IncusCloudInit.ExecWrapperPath))
            {
                wrapperExecCalls++;
                return Task.FromResult(Failure("ordinary command failure\n"));
            }
            if (IsFileCommand(argv, "pull") && argv.Any(argument => argument.Contains("/complete-", StringComparison.Ordinal)))
            {
                completionPullCalls++;
                return Task.FromResult(returnMismatchedSentinel ? Success("0\n") : Failure());
            }
            if (IsFileCommand(argv, "pull"))
                return Task.FromResult(Failure());
            if (argv.Contains("stop", StringComparer.Ordinal) && argv.Contains("--force", StringComparer.Ordinal))
            {
                forcedStopCalls++;
                stopped = true;
                return Task.FromResult(Success());
            }
            if (argv.Contains("list", StringComparer.Ordinal))
                return Task.FromResult(OwnedInstanceList(sandboxName, stopped ? "STOPPED" : "RUNNING"));
            throw new InvalidOperationException($"Unexpected Incus sentinel test command: {string.Join(' ', argv)}");
        });
        var sandbox = CreateSandbox(sandboxName, root, FastLifecycleOptions(), runner);

        try
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                sandbox.ExecAsync(new SandboxExec { Argv = ["false"] }));

            Assert.Contains("completion sentinel", failure.Message, StringComparison.Ordinal);
            Assert.Equal(1, wrapperExecCalls);
            Assert.Equal(3, completionPullCalls);
            Assert.Equal(1, forcedStopCalls);
            Assert.DoesNotContain(runner.Commands, command => IsFileCommand(command, "delete"));
            var commandCount = runner.Commands.Count;

            var poisoned = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                sandbox.ExecAsync(new SandboxExec { Argv = ["true"] }));

            Assert.Contains("unverified prior exec cleanup", poisoned.Message, StringComparison.Ordinal);
            Assert.Equal(commandCount, runner.Commands.Count);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StopAndPreserve_GracefulFailureThenVerifiedForceStop_RemainsPreserved()
    {
        const string sandboxName = "codeybox-preserve-force-stop";
        var root = Path.Combine(Path.GetTempPath(), $"codeybox-incus-preserve-force-stop-{Guid.NewGuid():N}");
        var status = "RUNNING";
        var gracefulStopCalls = 0;
        var forcedStopCalls = 0;
        var deleteCalls = 0;
        var inactive = 0;
        var runner = new ScriptedLifecycleRunner((argv, _, _) =>
        {
            if (argv.Contains("config", StringComparer.Ordinal) && argv.Contains("set", StringComparer.Ordinal))
                return Task.FromResult(Success());
            if (argv.Contains("stop", StringComparer.Ordinal) && argv.Contains("--force", StringComparer.Ordinal))
            {
                forcedStopCalls++;
                status = "STOPPED";
                return Task.FromResult(Success());
            }
            if (argv.Contains("stop", StringComparer.Ordinal))
            {
                gracefulStopCalls++;
                return Task.FromResult(Failure("graceful stop failed\n"));
            }
            if (argv.Contains("list", StringComparer.Ordinal))
                return Task.FromResult(OwnedInstanceList(sandboxName, status));
            if (argv.Contains("delete", StringComparer.Ordinal))
            {
                deleteCalls++;
                return Task.FromResult(Success());
            }
            throw new InvalidOperationException($"Unexpected Incus preserve test command: {string.Join(' ', argv)}");
        });
        var sandbox = CreateSandbox(
            sandboxName,
            root,
            FastLifecycleOptions(),
            runner,
            _ => Interlocked.Increment(ref inactive));
        SandboxLiveCounter.Increment();

        try
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                sandbox.StopAndPreserveAsync());

            Assert.Contains("forced stop was verified", failure.Message, StringComparison.Ordinal);
            Assert.Equal(1, gracefulStopCalls);
            Assert.Equal(1, forcedStopCalls);
            Assert.Equal(0, deleteCalls);
            Assert.Contains(
                runner.Commands,
                command => command.Any(argument => string.Equals(
                    argument,
                    $"{IncusSandboxProvider.PreemptKey}=true",
                    StringComparison.Ordinal)));

            await sandbox.DisposeAsync();

            Assert.Equal(0, deleteCalls);
            Assert.Equal(1, inactive);
            Assert.True(Directory.Exists(Path.Combine(root, sandboxName)));
        }
        finally
        {
            if (Volatile.Read(ref inactive) == 0)
                SandboxLiveCounter.Decrement();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StopAndPreserve_ForceStopStillRunning_LaterDisposeForceDeletes()
    {
        const string sandboxName = "codeybox-preserve-delete-running";
        var root = Path.Combine(Path.GetTempPath(), $"codeybox-incus-preserve-delete-running-{Guid.NewGuid():N}");
        var status = "RUNNING";
        var gracefulStopCalls = 0;
        var forcedStopCalls = 0;
        var deleteCalls = 0;
        var inactive = 0;
        var runner = new ScriptedLifecycleRunner((argv, _, _) =>
        {
            if (argv.Contains("config", StringComparer.Ordinal) && argv.Contains("set", StringComparer.Ordinal))
                return Task.FromResult(Success());
            if (argv.Contains("stop", StringComparer.Ordinal) && argv.Contains("--force", StringComparer.Ordinal))
            {
                forcedStopCalls++;
                return Task.FromResult(Success());
            }
            if (argv.Contains("stop", StringComparer.Ordinal))
            {
                gracefulStopCalls++;
                return Task.FromResult(Failure("graceful stop failed\n"));
            }
            if (argv.Contains("list", StringComparer.Ordinal))
                return Task.FromResult(OwnedInstanceList(sandboxName, status));
            if (argv.Contains("delete", StringComparer.Ordinal))
            {
                deleteCalls++;
                status = string.Empty;
                return Task.FromResult(Success());
            }
            throw new InvalidOperationException($"Unexpected Incus force-delete test command: {string.Join(' ', argv)}");
        });
        var sandbox = CreateSandbox(
            sandboxName,
            root,
            FastLifecycleOptions(),
            runner,
            _ => Interlocked.Increment(ref inactive));
        SandboxLiveCounter.Increment();

        try
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                sandbox.StopAndPreserveAsync());

            Assert.Contains("could not reach a verified STOPPED state", failure.Message, StringComparison.Ordinal);
            Assert.Equal(1, gracefulStopCalls);
            Assert.Equal(1, forcedStopCalls);
            Assert.Equal(0, deleteCalls);

            await sandbox.DisposeAsync();

            Assert.Equal(1, deleteCalls);
            Assert.Equal(1, inactive);
            Assert.False(Directory.Exists(Path.Combine(root, sandboxName)));
        }
        finally
        {
            if (Volatile.Read(ref inactive) == 0)
                SandboxLiveCounter.Decrement();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static IncusSandboxOptions FastLifecycleOptions() => new()
    {
        CaptureResourceMetrics = false,
        DiskGuard = null,
        OperationTimeout = TimeSpan.FromMilliseconds(250),
        ExecTimeout = TimeSpan.FromSeconds(2),
        VmStopTimeout = TimeSpan.FromMilliseconds(100),
        ReadinessPollInterval = TimeSpan.FromMilliseconds(1),
    };

    private static IncusSandbox CreateSandbox(
        string sandboxName,
        string root,
        IncusSandboxOptions options,
        IProcessRunner runner,
        Action<string>? onDisposed = null)
    {
        var sandboxRoot = Path.Combine(root, sandboxName);
        Directory.CreateDirectory(sandboxRoot);
        IncusMountStaging.InitializeOwnedTree(sandboxRoot, sandboxName, DateTimeOffset.UtcNow);
        return new IncusSandbox(
            sandboxName,
            sandboxRoot,
            root,
            new SandboxSpec { ImageReference = "local-image" },
            options,
            new IncusCliRunner(runner),
            NullLogger.Instance,
            timings: null,
            WorkItemId.New(),
            "work",
            baselineRef: null,
            resourceUsageStore: null,
            onDisposed ?? (_ => { }));
    }

    private static ProcessRunResult Success(string stdout = "") =>
        new(0, stdout, string.Empty);

    private static ProcessRunResult Failure(string stderr = "") =>
        new(1, string.Empty, stderr);

    private static ProcessRunResult OwnedInstanceList(string sandboxName, string status) =>
        string.IsNullOrEmpty(status)
            ? Success("[]")
            : Success(
                $"[{{\"name\":\"{sandboxName}\",\"type\":\"virtual-machine\",\"status\":\"{status}\",\"config\":{{\"{IncusSandboxProvider.ManagedKey}\":\"true\",\"{IncusSandboxProvider.KindKey}\":\"{IncusSandboxProvider.SandboxKind}\"}}}}]");

    private static bool IsFileCommand(IReadOnlyList<string> argv, string verb) =>
        argv.Contains("file", StringComparer.Ordinal) && argv.Contains(verb, StringComparer.Ordinal);

    private static bool IsGuestCommand(IReadOnlyList<string> argv, string executable) =>
        argv.Contains("exec", StringComparer.Ordinal) && argv.Contains(executable, StringComparer.Ordinal);

    private sealed class ScriptedLifecycleRunner(
        Func<IReadOnlyList<string>, string?, CancellationToken, Task<ProcessRunResult>> handler)
        : IProcessRunner
    {
        internal List<IReadOnlyList<string>> Commands { get; } = [];

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
            ct.ThrowIfCancellationRequested();
            Commands.Add(argv.ToArray());
            return handler(argv, stdin, ct);
        }
    }

    private sealed class MetricsLifecycleRunner(string sandboxName) : IProcessRunner
    {
        private bool _deleted;
        internal List<IReadOnlyList<string>> Commands { get; } = [];

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
            ct.ThrowIfCancellationRequested();
            Commands.Add(argv.ToArray());
            if (argv.Contains("list", StringComparer.Ordinal))
            {
                var json = _deleted
                    ? "[]"
                    : $"[{{\"name\":\"{sandboxName}\",\"type\":\"virtual-machine\",\"status\":\"RUNNING\",\"config\":{{\"{IncusSandboxProvider.ManagedKey}\":\"true\",\"{IncusSandboxProvider.KindKey}\":\"{IncusSandboxProvider.SandboxKind}\"}}}}]";
                return Task.FromResult(new ProcessRunResult(0, json, string.Empty));
            }
            if (argv.Contains("exec", StringComparer.Ordinal))
            {
                const string metrics = """
                    uptime=12.5
                    load1=1.0
                    load5=2.0
                    load15=3.0
                    cpu=37.25
                    peak=1048576
                    rx=2097152
                    tx=3145728
                    """;
                return Task.FromResult(new ProcessRunResult(0, metrics, string.Empty));
            }
            if (argv.Contains("delete", StringComparer.Ordinal))
            {
                _deleted = true;
                return Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty));
            }
            throw new InvalidOperationException($"Unexpected Incus lifecycle command: {string.Join(' ', argv)}");
        }
    }

    private sealed class RecordingUsageStore : ISandboxResourceUsageStore
    {
        internal List<SandboxResourceUsageRecord> Records { get; } = [];

        public Task RecordAsync(SandboxResourceUsageRecord record, CancellationToken ct = default)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SandboxResourceUsageRecord>> ListRecentAsync(
            int limit,
            DateTimeOffset? sinceUtc = null,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SandboxResourceUsageRecord>>(Records.Take(limit).ToArray());
    }

    private sealed class DeletionLifecycleRunner(string sandboxName) : IProcessRunner
    {
        private bool _deleted;
        internal int DeleteCalls { get; private set; }

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
            ct.ThrowIfCancellationRequested();
            if (argv.Contains("list", StringComparer.Ordinal))
            {
                var json = _deleted
                    ? "[]"
                    : $"[{{\"name\":\"{sandboxName}\",\"type\":\"virtual-machine\",\"status\":\"RUNNING\",\"config\":{{\"{IncusSandboxProvider.ManagedKey}\":\"true\",\"{IncusSandboxProvider.KindKey}\":\"{IncusSandboxProvider.SandboxKind}\"}}}}]";
                return Task.FromResult(new ProcessRunResult(0, json, string.Empty));
            }
            if (argv.Contains("delete", StringComparer.Ordinal))
            {
                DeleteCalls++;
                _deleted = true;
                return Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty));
            }
            throw new InvalidOperationException($"Unexpected Incus deletion command: {string.Join(' ', argv)}");
        }
    }

    private sealed class StickyDeletionLifecycleRunner(string sandboxName) : IProcessRunner
    {
        private bool _deleted;
        internal bool CompleteDeletion { get; set; }
        internal int DeleteCalls { get; private set; }

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
            ct.ThrowIfCancellationRequested();
            if (argv.Contains("list", StringComparer.Ordinal))
            {
                var json = _deleted
                    ? "[]"
                    : $"[{{\"name\":\"{sandboxName}\",\"type\":\"virtual-machine\",\"status\":\"RUNNING\",\"config\":{{\"{IncusSandboxProvider.ManagedKey}\":\"true\",\"{IncusSandboxProvider.KindKey}\":\"{IncusSandboxProvider.SandboxKind}\"}}}}]";
                return Task.FromResult(new ProcessRunResult(0, json, string.Empty));
            }
            if (argv.Contains("delete", StringComparer.Ordinal))
            {
                DeleteCalls++;
                _deleted = CompleteDeletion;
                return Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty));
            }
            throw new InvalidOperationException($"Unexpected Incus sticky deletion command: {string.Join(' ', argv)}");
        }
    }

    private sealed class StickyProviderDeletionRunner(string sandboxName) : IProcessRunner
    {
        private bool _deleted;
        internal bool CompleteDeletion { get; set; }
        internal int DeleteCalls { get; private set; }

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
            ct.ThrowIfCancellationRequested();
            if (argv.SequenceEqual(["incus", "project", "list", "--format=json"]))
                return Task.FromResult(new ProcessRunResult(0, "[{\"name\":\"codeybox\"}]", string.Empty));
            if (argv.Contains("list", StringComparer.Ordinal))
            {
                var json = _deleted
                    ? "[]"
                    : $"[{{\"name\":\"{sandboxName}\",\"type\":\"virtual-machine\",\"status\":\"RUNNING\",\"config\":{{\"{IncusSandboxProvider.ManagedKey}\":\"true\",\"{IncusSandboxProvider.KindKey}\":\"{IncusSandboxProvider.SandboxKind}\"}}}}]";
                return Task.FromResult(new ProcessRunResult(0, json, string.Empty));
            }
            if (argv.Contains("config", StringComparer.Ordinal)
                && argv.Contains("get", StringComparer.Ordinal))
            {
                var value = argv.Contains(IncusSandboxProvider.ManagedKey, StringComparer.Ordinal)
                    ? "true\n"
                    : IncusSandboxProvider.SandboxKind + "\n";
                return Task.FromResult(new ProcessRunResult(0, value, string.Empty));
            }
            if (argv.Contains("delete", StringComparer.Ordinal))
            {
                DeleteCalls++;
                _deleted = CompleteDeletion;
                return Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty));
            }
            throw new InvalidOperationException($"Unexpected Incus sticky provider deletion command: {string.Join(' ', argv)}");
        }
    }

    private sealed class UncertainCreateRunner : IProcessRunner
    {
        private string? _restrictedDiskPaths;

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
            ct.ThrowIfCancellationRequested();
            if (argv.SequenceEqual(["incus", "project", "list", "--format=json"]))
                return Task.FromResult(new ProcessRunResult(0, "[{\"name\":\"codeybox\"}]", string.Empty));
            if (argv.SequenceEqual(["incus", "query", "/1.0"]))
            {
                return Task.FromResult(new ProcessRunResult(
                    0,
                    "{\"metadata\":{\"api_extensions\":[\"disk_io_bus_cache_filesystem\",\"projects_restrictions\"],\"environment\":{\"kernel_version\":\"6.14.0-test\"}}}",
                    string.Empty));
            }
            if (argv.SequenceEqual(["incus", "query", "/1.0/projects/codeybox"]))
            {
                var config = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [IncusProjectSecurity.FeaturesImagesKey] = "false",
                    [IncusProjectSecurity.FeaturesProfilesKey] = "true",
                    [IncusProjectSecurity.ManagedKey] = "true",
                    [IncusProjectSecurity.SchemaKey] = "1",
                };
                if (_restrictedDiskPaths is not null)
                {
                    config[IncusProjectSecurity.RestrictedKey] = "true";
                    config[IncusProjectSecurity.RestrictedDiskKey] = "allow";
                    config[IncusProjectSecurity.RestrictedDiskPathsKey] = _restrictedDiskPaths;
                    config[IncusProjectSecurity.RestrictedNicKey] = "allow";
                    config[IncusProjectSecurity.RestrictedSnapshotsKey] = "allow";
                    config[IncusProjectSecurity.RestrictedVmLowLevelKey] = "block";
                }
                var json = System.Text.Json.JsonSerializer.Serialize(new
                {
                    metadata = new { name = "codeybox", config },
                });
                return Task.FromResult(new ProcessRunResult(0, json, string.Empty));
            }
            if (argv.Take(4).SequenceEqual(["incus", "project", "set", "codeybox"]))
            {
                _restrictedDiskPaths = argv
                    .Single(argument => argument.StartsWith(
                        IncusProjectSecurity.RestrictedDiskPathsKey + "=",
                        StringComparison.Ordinal))
                    [(IncusProjectSecurity.RestrictedDiskPathsKey.Length + 1)..];
                return Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty));
            }
            if (argv.Contains("storage", StringComparer.Ordinal)
                && argv.Contains("list", StringComparer.Ordinal))
            {
                return Task.FromResult(new ProcessRunResult(
                    0,
                    "[{\"name\":\"codeybox-zfs\",\"driver\":\"zfs\",\"config\":{}}]",
                    string.Empty));
            }
            if (argv.Contains("init", StringComparer.Ordinal))
                throw new TimeoutException("daemon completion is unknown");
            if (argv.Contains("list", StringComparer.Ordinal))
                return Task.FromResult(new ProcessRunResult(0, "[]", string.Empty));
            throw new InvalidOperationException($"Unexpected Incus uncertain-create command: {string.Join(' ', argv)}");
        }
    }
}
