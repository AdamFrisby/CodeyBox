using CodeyBox.Core;
using CodeyBox.HostProcess;
using CodeyBox.Sandbox.Incus;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using ControllableTimeProvider = Microsoft.Extensions.Time.Testing.FakeTimeProvider;

namespace CodeyBox.Tests;

public sealed class IncusSandboxLifecycleTests
{
    [Fact]
    public void OptionsAccessor_RejectsChangedProjectIdentityBeforeCallingIncus()
    {
        var stagingRoot = Path.Combine(Path.GetTempPath(), $"codeybox-incus-identity-{Guid.NewGuid():N}");
        var options = new IncusSandboxOptions
        {
            StagingDirectory = stagingRoot,
            DiskGuard = null,
        };
        var runner = new ScriptedLifecycleRunner((_, _, _) =>
            throw new InvalidOperationException("Incus must not be called"));
        var provider = new IncusSandboxProvider(
            () => options,
            NullLogger<IncusSandboxProvider>.Instance,
            timings: null,
            runner);

        options = options with { ProjectName = "codeybox-reloaded" };

        var exception = Assert.Throws<InvalidOperationException>(provider.SampleDiskGuardState);
        Assert.Contains("ProjectName", exception.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public void OptionsAccessor_RejectsChangedEffectiveStagingIdentityBeforeCallingIncus()
    {
        var stagingRoot = Path.Combine(Path.GetTempPath(), $"codeybox-incus-identity-{Guid.NewGuid():N}");
        var options = new IncusSandboxOptions
        {
            StagingDirectory = stagingRoot,
            DiskGuard = null,
        };
        var runner = new ScriptedLifecycleRunner((_, _, _) =>
            throw new InvalidOperationException("Incus must not be called"));
        var provider = new IncusSandboxProvider(
            () => options,
            NullLogger<IncusSandboxProvider>.Instance,
            timings: null,
            runner);

        options = options with { StagingDirectory = stagingRoot + "-reloaded" };

        var exception = Assert.Throws<InvalidOperationException>(provider.SampleDiskGuardState);
        Assert.Contains("StagingDirectory", exception.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task Create_RejectsOversizedSpecEnvironmentBeforeCallingIncus()
    {
        var environment = Enumerable.Range(0, IncusSandbox.MaxExecEnvironmentEntries + 1)
            .ToDictionary(index => $"KEY_{index}", _ => "value", StringComparer.Ordinal);
        var runner = new ScriptedLifecycleRunner((_, _, _) =>
            throw new InvalidOperationException("Incus must not be called"));
        var provider = new IncusSandboxProvider(
            () => new IncusSandboxOptions { DiskGuard = null },
            NullLogger<IncusSandboxProvider>.Instance,
            timings: null,
            runner);

        await Assert.ThrowsAsync<ArgumentException>(() => provider.CreateAsync(new SandboxSpec
        {
            ImageReference = "local-image",
            Environment = environment,
        }));

        Assert.Empty(runner.Commands);
    }

    [Fact]
    public void SerializeEnvironment_RejectsOversizedValueBeforeBuildingCombinedEntry()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["A"] = new string('x', 16 * 1024 * 1024),
        };

        var exception = Assert.Throws<ArgumentException>(() =>
            IncusSandbox.SerializeEnvironment(environment));

        Assert.Contains("UTF-8 safety bound", exception.Message, StringComparison.Ordinal);
        Assert.Equal(
            "A=x\0",
            IncusSandbox.SerializeEnvironment(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["A"] = "x" }));
    }

    [Theory]
    [InlineData(100, 40, 60)]
    [InlineData(0, 0, 0)]
    public void CalculateStorageFreeBytes_AcceptsValidResourceData(
        long total,
        long used,
        long expected)
    {
        Assert.Equal(expected, IncusSandboxProvider.CalculateStorageFreeBytes(total, used));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(1, -1)]
    [InlineData(1, 2)]
    [InlineData(long.MinValue, long.MaxValue)]
    public void CalculateStorageFreeBytes_RejectsMalformedResourceData(long total, long used)
    {
        Assert.Throws<InvalidOperationException>(() =>
            IncusSandboxProvider.CalculateStorageFreeBytes(total, used));
    }

    [Theory]
    [InlineData("NaN", 0, 100)]
    [InlineData("Infinity", 0, 100)]
    [InlineData("-Infinity", 0, 100)]
    [InlineData("-0.01", 0, 100)]
    [InlineData("100.01", 0, 100)]
    public void ParseMetricDouble_RejectsNonFiniteAndOutOfRangeValues(
        string value,
        double minimum,
        double maximum)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["metric"] = value,
        };

        Assert.Null(IncusSandbox.ParseMetricDouble(values, "metric", minimum, maximum));
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("37.25", 37.25)]
    [InlineData("100", 100)]
    public void ParseMetricDouble_AcceptsFiniteValuesInsideRange(string value, double expected)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["metric"] = value,
        };

        Assert.Equal(expected, IncusSandbox.ParseMetricDouble(values, "metric", 0, 100));
    }

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
        var capturedAt = new DateTimeOffset(2026, 7, 12, 1, 2, 3, TimeSpan.Zero);
        var time = new ControllableTimeProvider(capturedAt);
        var root = Path.Combine(Path.GetTempPath(), $"codeybox-incus-metrics-{Guid.NewGuid():N}");
        var sandboxRoot = Path.Combine(root, sandboxName);
        Directory.CreateDirectory(sandboxRoot);
        IncusMountStaging.InitializeOwnedTree(sandboxRoot, sandboxName, capturedAt);
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
            new IncusCliRunner(runner, time),
            NullLogger.Instance,
            timings: null,
            workItemId,
            "work",
            "baseline-ref",
            store,
            _ => Interlocked.Increment(ref disposed),
            timeProvider: time);
        SandboxLiveCounter.Increment();
        try
        {
            await sandbox.DisposeAsync();

            Assert.Equal(1, disposed);
            Assert.False(Directory.Exists(sandboxRoot));
            Assert.Contains(runner.Commands, command => command.Contains("delete", StringComparer.Ordinal));
            var metrics = Assert.IsType<SandboxResourceMetrics>(sandbox.ResourceMetrics);
            Assert.Equal(12.5, metrics.UptimeSeconds);
            Assert.Equal(37.25, metrics.AvgCpuPercent);
            Assert.Equal(1048576, metrics.PeakRamBytes);
            Assert.Equal(capturedAt, metrics.CapturedAt);
            var record = Assert.Single(store.Records);
            Assert.Equal(workItemId, record.WorkItemId);
            Assert.Equal(1, record.PeakRamMb);
            Assert.Equal("baseline-ref", record.BaselineRef);
        }
        finally
        {
            // The process-global gauge has dedicated exclusive-collection
            // coverage. This lifecycle test observes its own transition via
            // the callback and only balances a failed pre-notification path.
            if (Volatile.Read(ref disposed) == 0)
                SandboxLiveCounter.Decrement();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
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
        SandboxLiveCounter.Increment();
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => sandbox.DisposeAsync().AsTask());
            Assert.Equal(1, inactive);
            await Assert.ThrowsAsync<InvalidOperationException>(() => sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["true"],
            }));

            IncusMountStaging.InitializeOwnedTree(sandboxRoot, sandboxName, DateTimeOffset.UtcNow);
            await sandbox.DisposeAsync();

            Assert.False(Directory.Exists(sandboxRoot));
            Assert.Equal(1, runner.DeleteCalls);
            Assert.Equal(1, inactive);
        }
        finally
        {
            // NotifyNoLongerActive is asserted through the sandbox-local
            // callback; do not snapshot the process-global gauge while other
            // sandbox tests legitimately mutate it in parallel.
            if (Volatile.Read(ref inactive) == 0)
                SandboxLiveCounter.Decrement();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
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
        SandboxLiveCounter.Increment();

        await Assert.ThrowsAsync<TimeoutException>(() => sandbox.DisposeAsync().AsTask());

        Assert.True(Directory.Exists(sandboxRoot));
        Assert.Equal(0, inactive);
        runner.CompleteDeletion = true;

        await sandbox.DisposeAsync();

        Assert.False(Directory.Exists(sandboxRoot));
        Assert.Equal(2, runner.DeleteCalls);
        Assert.Equal(1, inactive);
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
        var runner = new StickyProviderDeletionRunner(sandboxName, root);
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

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ColdLifecycle_RejectsForeignOrUnrestrictedProjectBeforeTrustingInstanceMarkers(
        bool hasManagedShape,
        bool hasRequiredRestrictions)
    {
        const string sandboxName = "codeybox-foreign-project";
        const string baselineName = "cb-foreign-baseline";
        var root = Path.Combine(Path.GetTempPath(), $"codeybox-incus-foreign-{Guid.NewGuid():N}");
        IncusMountStaging.EnsureOwnedStagingRoot(root);
        var runner = new ForeignProjectRunner(root, hasManagedShape, hasRequiredRestrictions);
        var provider = new IncusSandboxProvider(
            () => new IncusSandboxOptions
            {
                StagingDirectory = root,
                DiskGuard = null,
            },
            NullLogger<IncusSandboxProvider>.Instance,
            timings: null,
            runner);

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                provider.ListAllManagedAsync(CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                provider.DisposeLeakedAsync(sandboxName, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                provider.ListBaselineImagesAsync(CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                provider.DisposeBaselineImageAsync(baselineName, CancellationToken.None));

            Assert.Equal(0, runner.InstanceListCalls);
            Assert.Equal(0, runner.DeleteCalls);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ListBaselineImagesAsync_MalformedProjectRowThrowsInsteadOfReturningFalseEmpty()
    {
        var runner = new ScriptedLifecycleRunner((argv, _, _) =>
            argv.SequenceEqual(["incus", "project", "list", "--format=json"])
                ? Task.FromResult(Success("[{}]"))
                : throw new InvalidOperationException($"Unexpected Incus command: {string.Join(' ', argv)}"));
        var provider = new IncusSandboxProvider(
            () => new IncusSandboxOptions { DiskGuard = null },
            NullLogger<IncusSandboxProvider>.Instance,
            timings: null,
            runner);

        await Assert.ThrowsAsync<JsonException>(() =>
            provider.ListBaselineImagesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ListBaselineImagesAsync_MalformedInstanceRowThrowsInsteadOfReturningPartialInventory()
    {
        var stagingRoot = Path.Combine(Path.GetTempPath(), $"codeybox-incus-inventory-{Guid.NewGuid():N}");
        var runner = new ScriptedLifecycleRunner((argv, _, _) =>
        {
            if (argv.SequenceEqual(["incus", "project", "list", "--format=json"]))
                return Task.FromResult(Success("[{\"name\":\"codeybox\"}]"));
            if (argv.SequenceEqual(["incus", "query", "/1.0/projects/codeybox"]))
                return Task.FromResult(Success(ManagedProjectQuery(stagingRoot)));
            if (argv.Contains("list", StringComparer.Ordinal))
                return Task.FromResult(Success("[{}]"));
            throw new InvalidOperationException($"Unexpected Incus command: {string.Join(' ', argv)}");
        });
        var provider = new IncusSandboxProvider(
            () => new IncusSandboxOptions
            {
                StagingDirectory = stagingRoot,
                DiskGuard = null,
            },
            NullLogger<IncusSandboxProvider>.Instance,
            timings: null,
            runner);

        await Assert.ThrowsAsync<JsonException>(() =>
            provider.ListBaselineImagesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Create_WhenDaemonCompletionIsUncertain_RetainsNamedStagingForReaper()
    {
        var stateHome = Path.Combine(Path.GetTempPath(), $"codeybox-incus-state-{Guid.NewGuid():N}");
        var root = Path.Combine(stateHome, "codeybox", "incus-staging");
        Directory.CreateDirectory(Path.GetDirectoryName(root)!);
        var createdAt = new DateTimeOffset(2026, 7, 12, 4, 5, 6, TimeSpan.Zero);
        var time = new ControllableTimeProvider(createdAt);
        var generatedId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var environmentReads = new List<string>();
        var runner = new UncertainCreateRunner();
        var options = new IncusSandboxOptions
        {
            StagingDirectory = null,
            UseBaselineImages = false,
            DiskGuard = null,
        };
        var provider = new IncusSandboxProvider(
            () => options,
            NullLogger<IncusSandboxProvider>.Instance,
            timings: null,
            runner,
            timeProvider: time,
            newGuid: () => generatedId,
            environmentVariableReader: name =>
            {
                environmentReads.Add(name);
                return string.Equals(name, "XDG_STATE_HOME", StringComparison.Ordinal)
                    ? stateHome
                    : null;
            });

        var deferred = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            provider.CreateAsync(new SandboxSpec { ImageReference = "local-image" }));

        Assert.Equal("create-cleanup", deferred.Operation);
        Assert.Equal("codeybox-11111111222233334444", deferred.RetainedSandboxName);
        var retained = Assert.Single(await provider.ListAllManagedAsync(CancellationToken.None));
        Assert.Equal(deferred.RetainedSandboxName, retained.Name);
        Assert.Equal(createdAt, retained.CreatedAt);
        Assert.Contains("XDG_STATE_HOME", environmentReads);
        Assert.DoesNotContain("HOME", environmentReads);
        Assert.True(Directory.Exists(Path.Combine(root, retained.Name)));

        await provider.DisposeLeakedAsync(retained.Name, CancellationToken.None);
        Assert.Empty(await provider.ListAllManagedAsync(CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(root, retained.Name)));
        Directory.Delete(stateHome, recursive: true);
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
        var controlId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var generatedIds = 0;
        var root = Path.Combine(Path.GetTempPath(), $"codeybox-incus-exec-timeout-{Guid.NewGuid():N}");
        var completionPullCalls = 0;
        var wrapperExecCalls = 0;
        var forcedStopCalls = 0;
        var environmentAbsenceChecks = 0;
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
            {
                if (argv.Any(argument => argument.Contains("/env-", StringComparison.Ordinal))
                    && ++environmentAbsenceChecks == 1)
                {
                    return Failure();
                }
                return Success();
            }
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
            ExecControlFileCleanupAttempts = 2,
        };
        var sandbox = CreateSandbox(
            sandboxName,
            root,
            options,
            runner,
            newGuid: () =>
            {
                generatedIds++;
                return controlId;
            });

        try
        {
            var result = await sandbox.ExecAsync(new SandboxExec { Argv = ["true"] });

            Assert.True(result.Success, result.Stderr);
            Assert.Equal("completed\n", result.Stdout);
            Assert.Equal(1, wrapperExecCalls);
            Assert.Equal(1, completionPullCalls);
            Assert.Equal(0, forcedStopCalls);
            Assert.Equal(2, environmentAbsenceChecks);
            Assert.Equal(1, generatedIds);
            var deletedControlFiles = runner.Commands
                .Where(command => IsFileCommand(command, "delete"))
                .Select(command => command[^1])
                .ToArray();
            Assert.Equal(4, deletedControlFiles.Length);
            Assert.Contains(deletedControlFiles, path => path.EndsWith("/env-aaaaaaaabbbbccccddddeeeeeeeeeeee", StringComparison.Ordinal));
            Assert.Contains(deletedControlFiles, path => path.EndsWith("/pid-aaaaaaaabbbbccccddddeeeeeeeeeeee", StringComparison.Ordinal));
            Assert.Contains(deletedControlFiles, path => path.EndsWith("/complete-aaaaaaaabbbbccccddddeeeeeeeeeeee", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Exec_RejectsEmptyInjectedControlIdBeforeCallingIncus()
    {
        const string sandboxName = "codeybox-empty-control-id";
        var root = Path.Combine(Path.GetTempPath(), $"codeybox-incus-empty-id-{Guid.NewGuid():N}");
        var runner = new ScriptedLifecycleRunner((_, _, _) =>
            throw new InvalidOperationException("Incus must not be called for an invalid generated ID."));
        var sandbox = CreateSandbox(
            sandboxName,
            root,
            FastLifecycleOptions(),
            runner,
            newGuid: static () => Guid.Empty);

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                sandbox.ExecAsync(new SandboxExec { Argv = ["true"] }));

            Assert.Contains("empty value for exec control files", exception.Message, StringComparison.Ordinal);
            Assert.Empty(runner.Commands);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Exec_SnapshotsCallerOwnedSpecArgvAndEnvironmentBeforeFirstCliAwait()
    {
        const string sandboxName = "codeybox-exec-snapshot";
        var root = Path.Combine(Path.GetTempPath(), $"codeybox-incus-exec-snapshot-{Guid.NewGuid():N}");
        var pushStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePush = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? pushedEnvironment = null;
        IReadOnlyList<string>? wrapperCommand = null;
        var runner = new ScriptedLifecycleRunner(async (argv, stdin, ct) =>
        {
            if (IsFileCommand(argv, "push"))
            {
                pushedEnvironment = stdin;
                pushStarted.TrySetResult();
                await releasePush.Task.WaitAsync(ct);
                return Success();
            }
            if (IsGuestCommand(argv, IncusCloudInit.ExecWrapperPath))
            {
                wrapperCommand = argv.ToArray();
                return Success();
            }
            if (IsFileCommand(argv, "pull")
                && argv.Any(argument => argument.Contains("/complete-", StringComparison.Ordinal)))
            {
                return Success("0\n");
            }
            if (IsFileCommand(argv, "pull"))
                return Failure();
            if (IsFileCommand(argv, "delete") || IsGuestCommand(argv, "test"))
                return Success();
            throw new InvalidOperationException($"Unexpected Incus exec snapshot command: {string.Join(' ', argv)}");
        });
        var specEnvironment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SPEC_VALUE"] = "spec-before",
        };
        var spec = new SandboxSpec
        {
            ImageReference = "local-image",
            Environment = specEnvironment,
        };
        var sandbox = CreateSandbox(
            sandboxName,
            root,
            FastLifecycleOptions(),
            runner,
            spec: spec,
            newGuid: () => Guid.Parse("99999999-8888-7777-6666-555555555555"));
        specEnvironment["SPEC_VALUE"] = "spec-after";
        var argv = new List<string> { "original-command", "original-argument" };
        var extraEnvironment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["EXEC_VALUE"] = "exec-before",
        };

        try
        {
            var running = sandbox.ExecAsync(new SandboxExec
            {
                Argv = argv,
                ExtraEnvironment = extraEnvironment,
                EnvironmentContainsSecrets = true,
            });
            await pushStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            argv[0] = "mutated-command";
            argv.Add("mutated-argument");
            extraEnvironment["EXEC_VALUE"] = "exec-after";
            extraEnvironment["LATE_VALUE"] = "late";
            releasePush.TrySetResult();

            var result = await running.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(result.Success, result.Stderr);
            Assert.NotNull(pushedEnvironment);
            Assert.Contains("SPEC_VALUE=spec-before\0", pushedEnvironment, StringComparison.Ordinal);
            Assert.DoesNotContain("spec-after", pushedEnvironment, StringComparison.Ordinal);
            Assert.Contains("EXEC_VALUE=exec-before\0", pushedEnvironment, StringComparison.Ordinal);
            Assert.DoesNotContain("exec-after", pushedEnvironment, StringComparison.Ordinal);
            Assert.DoesNotContain("LATE_VALUE", pushedEnvironment, StringComparison.Ordinal);
            Assert.NotNull(wrapperCommand);
            Assert.Contains("original-command", wrapperCommand);
            Assert.Contains("original-argument", wrapperCommand);
            Assert.DoesNotContain("mutated-command", wrapperCommand);
            Assert.DoesNotContain("mutated-argument", wrapperCommand);
        }
        finally
        {
            releasePush.TrySetResult();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exec_SecretEnvironmentUsesFilePushStdinAndNeverFallsBackToArgv(
        bool failEnvironmentPush)
    {
        const string sandboxName = "codeybox-secret-environment";
        const string specSecret = "incus-spec-secret-sentinel-57cd8db7";
        const string execSecret = "incus-exec-secret-sentinel-7c30d63d";
        var root = Path.Combine(Path.GetTempPath(), $"codeybox-incus-secret-env-{Guid.NewGuid():N}");
        var pushedPayloads = new List<string>();
        var wrapperExecCalls = 0;
        var runner = new ScriptedLifecycleRunner((argv, stdin, _) =>
        {
            if (IsFileCommand(argv, "push"))
            {
                pushedPayloads.Add(stdin ?? throw new InvalidOperationException("Environment push had no stdin payload."));
                return Task.FromResult(failEnvironmentPush
                    ? Failure("environment push rejected\n")
                    : Success());
            }
            if (IsGuestCommand(argv, IncusCloudInit.ExecWrapperPath))
            {
                wrapperExecCalls++;
                return Task.FromResult(Success("secret-visible\n"));
            }
            if (IsFileCommand(argv, "pull")
                && argv.Any(argument => argument.Contains("/complete-", StringComparison.Ordinal)))
            {
                return Task.FromResult(Success("0\n"));
            }
            if (IsFileCommand(argv, "pull"))
                return Task.FromResult(Failure());
            if (IsFileCommand(argv, "delete") || IsGuestCommand(argv, "test"))
                return Task.FromResult(Success());
            throw new InvalidOperationException($"Unexpected Incus secret-environment command: {string.Join(' ', argv)}");
        });
        var sandbox = CreateSandbox(
            sandboxName,
            root,
            FastLifecycleOptions(),
            runner,
            spec: new SandboxSpec
            {
                ImageReference = "local-image",
                Environment = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["CODEYBOX_SPEC_SECRET"] = specSecret,
                    ["REMOVE_ME"] = "spec-value",
                },
            });
        var exec = new SandboxExec
        {
            Argv = ["printenv", "OPENAI_API_KEY"],
            ExtraEnvironment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["OPENAI_API_KEY"] = execSecret,
                ["REMOVE_ME"] = "exec-value",
            },
            EnvironmentVariablesToUnset = ["REMOVE_ME"],
            EnvironmentContainsSecrets = true,
        };

        try
        {
            if (failEnvironmentPush)
            {
                var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    sandbox.ExecAsync(exec));
                Assert.Contains("push exec environment", failure.Message, StringComparison.Ordinal);
            }
            else
            {
                var result = await sandbox.ExecAsync(exec);
                Assert.True(result.Success, result.Stderr);
                Assert.Equal("secret-visible\n", result.Stdout);
            }

            Assert.Equal(
                $"CODEYBOX_SPEC_SECRET={specSecret}\0OPENAI_API_KEY={execSecret}\0",
                Assert.Single(pushedPayloads));
            Assert.DoesNotContain(
                runner.Commands.SelectMany(static command => command),
                argument => argument.Contains(specSecret, StringComparison.Ordinal)
                    || argument.Contains(execSecret, StringComparison.Ordinal));
            Assert.Equal(failEnvironmentPush ? 0 : 1, wrapperExecCalls);
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
        var pidPullCalls = 0;
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
            {
                if (argv.Any(argument => argument.Contains("/pid-", StringComparison.Ordinal)))
                    pidPullCalls++;
                return Task.FromResult(Failure());
            }
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
        var liveOptions = FastLifecycleOptions();
        var sandbox = CreateSandbox(
            sandboxName,
            root,
            liveOptions,
            runner,
            liveOptionsAccessor: () => liveOptions);
        liveOptions = liveOptions with
        {
            ExecCompletionProbeAttempts = 2,
            ExecPidPollAttempts = 2,
        };

        try
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                sandbox.ExecAsync(new SandboxExec { Argv = ["false"] }));

            Assert.Contains("completion sentinel", failure.Message, StringComparison.Ordinal);
            Assert.Equal(1, wrapperExecCalls);
            Assert.Equal(2, completionPullCalls);
            Assert.Equal(2, pidPullCalls);
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
        Action<string>? onDisposed = null,
        SandboxSpec? spec = null,
        TimeProvider? timeProvider = null,
        Func<Guid>? newGuid = null,
        Func<IncusSandboxOptions>? liveOptionsAccessor = null)
    {
        var sandboxRoot = Path.Combine(root, sandboxName);
        Directory.CreateDirectory(sandboxRoot);
        IncusMountStaging.InitializeOwnedTree(sandboxRoot, sandboxName, DateTimeOffset.UtcNow);
        return new IncusSandbox(
            sandboxName,
            sandboxRoot,
            root,
            spec ?? new SandboxSpec { ImageReference = "local-image" },
            options,
            new IncusCliRunner(runner, timeProvider),
            NullLogger.Instance,
            timings: null,
            WorkItemId.New(),
            "work",
            baselineRef: null,
            resourceUsageStore: null,
            onDisposed ?? (_ => { }),
            timeProvider,
            newGuid,
            liveOptionsAccessor);
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

    private sealed class StickyProviderDeletionRunner(string sandboxName, string stagingRoot) : IProcessRunner
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
            if (argv.SequenceEqual(["incus", "query", "/1.0/projects/codeybox"]))
                return Task.FromResult(Success(ManagedProjectQuery(stagingRoot)));
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

    private sealed class ForeignProjectRunner(
        string stagingRoot,
        bool hasManagedShape,
        bool hasRequiredRestrictions) : IProcessRunner
    {
        internal int InstanceListCalls { get; private set; }
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
                return Task.FromResult(Success("[{\"name\":\"codeybox\"}]"));
            if (argv.SequenceEqual(["incus", "query", "/1.0/projects/codeybox"]))
            {
                return Task.FromResult(Success(ManagedProjectQuery(
                    stagingRoot,
                    hasManagedShape,
                    hasRequiredRestrictions)));
            }
            if (argv.Contains("list", StringComparer.Ordinal))
            {
                InstanceListCalls++;
                return Task.FromResult(Success(
                    $"[{{\"name\":\"codeybox-foreign-project\",\"type\":\"virtual-machine\",\"config\":{{\"{IncusSandboxProvider.ManagedKey}\":\"true\",\"{IncusSandboxProvider.KindKey}\":\"{IncusSandboxProvider.SandboxKind}\"}}}}]"));
            }
            if (argv.Contains("delete", StringComparer.Ordinal))
            {
                DeleteCalls++;
                return Task.FromResult(Success());
            }
            throw new InvalidOperationException($"Unexpected foreign-project command: {string.Join(' ', argv)}");
        }
    }

    private static string ManagedProjectQuery(
        string stagingRoot,
        bool hasManagedShape = true,
        bool hasRequiredRestrictions = true)
    {
        var config = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [IncusProjectSecurity.FeaturesImagesKey] = "false",
            [IncusProjectSecurity.FeaturesProfilesKey] = "true",
            [IncusProjectSecurity.ManagedKey] = hasManagedShape ? "true" : "false",
            [IncusProjectSecurity.SchemaKey] = "1",
            [IncusProjectSecurity.RestrictedKey] = hasRequiredRestrictions ? "true" : "false",
            [IncusProjectSecurity.RestrictedDiskKey] = "allow",
            [IncusProjectSecurity.RestrictedDiskPathsKey] = stagingRoot,
            [IncusProjectSecurity.RestrictedNicKey] = "allow",
            [IncusProjectSecurity.RestrictedSnapshotsKey] = "allow",
            [IncusProjectSecurity.RestrictedVmLowLevelKey] = "block",
        };
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            metadata = new { name = "codeybox", config },
        });
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
