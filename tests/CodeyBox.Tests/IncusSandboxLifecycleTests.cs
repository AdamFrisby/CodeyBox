using CodeyBox.Core;
using CodeyBox.HostProcess;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Incus;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using ControllableTimeProvider = Microsoft.Extensions.Time.Testing.FakeTimeProvider;

namespace CodeyBox.Tests;

public sealed class IncusSandboxLifecycleTests
{
    [Fact]
    public async Task GuestLinkRemoval_RejectsChangedTargetBeforeRootUnlink()
    {
        var removed = false;
        var runner = new ScriptedLifecycleRunner((argv, _, _) =>
        {
            if (IsGuestCommand(argv, "test") && argv.Contains("-L", StringComparer.Ordinal))
                return Task.FromResult(Success());
            if (IsGuestCommand(argv, "readlink"))
                return Task.FromResult(Success("/unauthorized/target\n"));
            if (IsGuestCommand(argv, "rm"))
            {
                removed = true;
                return Task.FromResult(Success());
            }
            throw new InvalidOperationException($"Unexpected guest-link command: {string.Join(' ', argv)}");
        });

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IncusGuestLinkLifecycle.RemoveForIsolatedValidationAsync(
                new IncusCliRunner(runner),
                FastLifecycleOptions(),
                "codeybox-link-target",
                [new IncusGuestLink("/authorized/target", "/safe/link")],
                CancellationToken.None));

        Assert.False(removed);
    }

    [Fact]
    public async Task GuestLinkRemoval_RejectsAliasedParentBeforeRootUnlink()
    {
        var runner = new ScriptedLifecycleRunner(
            (argv, _, _) => throw new InvalidOperationException(
                $"No guest-link mutation was expected: {string.Join(' ', argv)}"),
            canonicalPathResolver: path => string.Equals(path, "/safe", StringComparison.Ordinal)
                ? "/redirected"
                : path);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IncusGuestLinkLifecycle.RemoveForIsolatedValidationAsync(
                new IncusCliRunner(runner),
                FastLifecycleOptions(),
                "codeybox-link-parent",
                [new IncusGuestLink("/authorized/target", "/safe/link")],
                CancellationToken.None));

        Assert.DoesNotContain(runner.Commands, command => IsGuestCommand(command, "rm"));
    }

    [Fact]
    public async Task GuestLinkCreation_PositivelyVerifiesExactCreatedLink()
    {
        var linkCreated = false;
        var runner = new ScriptedLifecycleRunner((argv, _, _) =>
        {
            if (IsGuestCommand(argv, "mkdir"))
            {
                return Task.FromResult(Success());
            }
            if (IsGuestCommand(argv, "ln"))
            {
                linkCreated = true;
                return Task.FromResult(Success());
            }
            if (IsGuestCommand(argv, "test") && argv.Contains("-L", StringComparer.Ordinal))
            {
                var negated = argv.Contains("!", StringComparer.Ordinal);
                return Task.FromResult(negated == !linkCreated ? Success() : Failure());
            }
            if (IsGuestCommand(argv, "test") && argv.Contains("-e", StringComparer.Ordinal))
                return Task.FromResult(!linkCreated ? Success() : Failure());
            if (IsGuestCommand(argv, "readlink"))
                return Task.FromResult(Success("/authorized/target\n"));
            throw new InvalidOperationException($"Unexpected guest-link command: {string.Join(' ', argv)}");
        });

        await IncusGuestLinkLifecycle.CreateAsync(
            new IncusCliRunner(runner),
            FastLifecycleOptions(),
            "codeybox-link-create",
            [new IncusGuestLink("/authorized/target", "/safe/link")],
            CancellationToken.None);

        Assert.True(linkCreated);
        Assert.Contains(runner.Commands, command => IsGuestCommand(command, "readlink"));
    }

    [Fact]
    public async Task GuestLinkReconciliation_RetriesPartialRemoveAndCreateIdempotently()
    {
        var links = new[]
        {
            new IncusGuestLink("/device/one", "/safe/one"),
            new IncusGuestLink("/device/two", "/safe/two"),
        };
        var present = links.ToDictionary(static link => link.LinkPath, _ => true, StringComparer.Ordinal);
        var failSecondRemove = true;
        var failSecondCreate = true;
        var runner = new ScriptedLifecycleRunner((argv, _, _) =>
        {
            var path = argv[^1];
            if (IsGuestCommand(argv, "test") && argv.Contains("-L", StringComparer.Ordinal))
            {
                var negated = argv.Contains("!", StringComparer.Ordinal);
                return Task.FromResult(negated == !present[path] ? Success() : Failure());
            }
            if (IsGuestCommand(argv, "test") && argv.Contains("-e", StringComparer.Ordinal))
                return Task.FromResult(!present[path] ? Success() : Failure());
            if (IsGuestCommand(argv, "readlink"))
            {
                var target = links.Single(link => string.Equals(link.LinkPath, path, StringComparison.Ordinal)).Target;
                return Task.FromResult(Success(target + "\n"));
            }
            if (IsGuestCommand(argv, "rm"))
            {
                if (string.Equals(path, links[1].LinkPath, StringComparison.Ordinal) && failSecondRemove)
                {
                    failSecondRemove = false;
                    throw new TimeoutException("simulated interruption between link removals");
                }
                present[path] = false;
                return Task.FromResult(Success());
            }
            if (IsGuestCommand(argv, "mkdir"))
                return Task.FromResult(Success());
            if (IsGuestCommand(argv, "ln"))
            {
                var linkPath = argv[^1];
                if (string.Equals(linkPath, links[1].LinkPath, StringComparison.Ordinal) && failSecondCreate)
                {
                    failSecondCreate = false;
                    throw new TimeoutException("simulated interruption between link creations");
                }
                present[linkPath] = true;
                return Task.FromResult(Success());
            }
            throw new InvalidOperationException($"Unexpected link reconciliation command: {string.Join(' ', argv)}");
        });
        var cli = new IncusCliRunner(runner);
        var options = FastLifecycleOptions();

        var removeInterruption = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            IncusGuestLinkLifecycle.RemoveForIsolatedValidationAsync(
                cli, options, "codeybox-link-reconcile", links, CancellationToken.None));
        Assert.IsType<TimeoutException>(removeInterruption.InnerException);
        await IncusGuestLinkLifecycle.RemoveForIsolatedValidationAsync(
            cli, options, "codeybox-link-reconcile", links, CancellationToken.None);
        Assert.All(present.Values, Assert.False);

        var createInterruption = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            IncusGuestLinkLifecycle.CreateAsync(
                cli, options, "codeybox-link-reconcile", links, CancellationToken.None));
        Assert.IsType<TimeoutException>(createInterruption.InnerException);
        await IncusGuestLinkLifecycle.CreateAsync(
            cli, options, "codeybox-link-reconcile", links, CancellationToken.None);
        Assert.All(present.Values, Assert.True);
    }

    [Fact]
    public void RecoveryAuthorization_RejectsDeletedAndRecreatedHostSourceInode()
    {
        var allowedRoot = Path.Combine(
            Path.GetTempPath(),
            $"codeybox-incus-recovery-inode-{Guid.NewGuid():N}");
        var source = Path.Combine(allowedRoot, "source");
        Directory.CreateDirectory(source);
        var options = FastLifecycleOptions() with { AllowedHostMountRoots = [allowedRoot] };
        var spec = new SandboxSpec
        {
            ImageReference = "local-image",
            Mounts =
            [
                new SandboxMount
                {
                    HostPath = source,
                    SandboxPath = "/repo",
                    ReadOnly = true,
                },
            ],
        };
        var authorization = IncusRecoveryAuthorization.CaptureValidated(
            bridge: null,
            [new IncusPreparedMount(source, "/repo", ReadOnly: true)],
            ["/repo"],
            guestLinks: [],
            options);
        var manifest = IncusRecoveryManifest.Create(
            "codeybox-recovery-inode",
            spec,
            options,
            IncusRecoveryManifestCodec.ComputeTokenSha256("private-token"),
            baselineRef: null,
            authorization);
        Directory.Delete(source);
        Directory.CreateDirectory(source);

        try
        {
            var rejected = Assert.Throws<IOException>(() =>
                manifest.RestoreAuthorization(options));

            Assert.Contains("inode identity", rejected.Message, StringComparison.Ordinal);
        }
        finally
        {
            authorization.Dispose();
            Directory.Delete(allowedRoot, recursive: true);
        }
    }

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
            NetworkProfiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["internet-only"] = "cb-net",
            },
        };
        var spec = new SandboxSpec
        {
            ImageReference = "local-image",
            Network = new SandboxNetworkPolicy { ProfileName = "internet-only" },
        };
        var authorization = RecoveryAuthorization(options, spec, bridge: "cb-net");
        var recoveryState = CreateRecoveryState(
            sandboxRoot,
            sandboxName,
            spec,
            options,
            authorization,
            "baseline-ref");
        runner.SetRecoveryBinding(
            recoveryState.Manifest.LeaseTokenSha256,
            recoveryState.ManifestHash);
        var sandbox = new IncusSandbox(
            sandboxName,
            sandboxRoot,
            root,
            spec,
            options,
            new IncusCliRunner(runner, time),
            NullLogger.Instance,
            timings: null,
            workItemId,
            "work",
            "baseline-ref",
            store,
            _ => Interlocked.Increment(ref disposed),
            authorization,
            recoveryState.Lease,
            recoveryState.Manifest,
            recoveryState.Store,
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
        var options = new IncusSandboxOptions { CaptureResourceMetrics = false, DiskGuard = null };
        var spec = new SandboxSpec { ImageReference = "local-image" };
        var authorization = RecoveryAuthorization(options, spec);
        var recoveryState = CreateRecoveryState(
            sandboxRoot,
            sandboxName,
            spec,
            options,
            authorization);
        runner.SetRecoveryBinding(
            recoveryState.Manifest.LeaseTokenSha256,
            recoveryState.ManifestHash);
        var sandbox = new IncusSandbox(
            sandboxName,
            sandboxRoot,
            root,
            spec,
            options,
            new IncusCliRunner(runner),
            NullLogger.Instance,
            timings: null,
            WorkItemId.New(),
            "work",
            baselineRef: null,
            resourceUsageStore: null,
            _ => Interlocked.Increment(ref inactive),
            authorization,
            recoveryState.Lease,
            recoveryState.Manifest,
            recoveryState.Store);
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
        var options = new IncusSandboxOptions
        {
            CaptureResourceMetrics = false,
            DiskGuard = null,
            OperationTimeout = TimeSpan.FromMilliseconds(100),
            ReadinessPollInterval = TimeSpan.FromMilliseconds(10),
        };
        var spec = new SandboxSpec { ImageReference = "local-image" };
        var authorization = RecoveryAuthorization(options, spec);
        var recoveryState = CreateRecoveryState(
            sandboxRoot,
            sandboxName,
            spec,
            options,
            authorization);
        runner.SetRecoveryBinding(
            recoveryState.Manifest.LeaseTokenSha256,
            recoveryState.ManifestHash);
        var sandbox = new IncusSandbox(
            sandboxName,
            sandboxRoot,
            root,
            spec,
            options,
            new IncusCliRunner(runner),
            NullLogger.Instance,
            timings: null,
            WorkItemId.New(),
            "work",
            baselineRef: null,
            resourceUsageStore: null,
            _ => Interlocked.Increment(ref inactive),
            authorization,
            recoveryState.Lease,
            recoveryState.Manifest,
            recoveryState.Store);
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
                // OperationTimeout is a REAL-CLOCK per-command deadline as well as the
                // absence-poll deadline. The intended phase-1 TimeoutException comes from
                // WaitForInstanceAbsenceAsync (the fake VM never disappears while
                // CompleteDeletion is false). A very short deadline (e.g. 100ms) also
                // covers each instant fake command, so under a starved thread pool the
                // deadline could fire before the `delete` command even runs — the first
                // DisposeLeakedAsync would then throw before DeleteCalls is incremented,
                // and the final Assert.Equal(2, DeleteCalls) would see 1. Use a generous
                // deadline so the delete always issues first; absence is still never
                // observed, so the poll still times out as the test requires.
                OperationTimeout = TimeSpan.FromSeconds(2),
                ReadinessPollInterval = TimeSpan.FromMilliseconds(25),
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
    public async Task Create_WithRetainedRecoveryLease_AdoptsAcrossProcessRestartAndKeepsPreservationArmed()
    {
        var fixture = PrepareRetainedAdoptionFixture("codeybox-retained-adopt");
        var runner = new RetainedAdoptionRunner(
            fixture.Options.StagingDirectory!,
            fixture.SandboxName,
            fixture.Manifest.LeaseTokenSha256,
            fixture.ManifestHash);
        var provider = new IncusSandboxProvider(
            () => fixture.Options,
            NullLogger<IncusSandboxProvider>.Instance,
            timings: null,
            runner);

        try
        {
            var adopted = await provider.CreateAsync(fixture.RequestSpec);

            Assert.Equal(fixture.SandboxName, adopted.Id);
            Assert.Equal("RUNNING", runner.Status);
            Assert.Equal(1, runner.ForcedStopCalls);
            Assert.Equal(1, runner.StartCalls);

            await adopted.DisposeAsync();

            Assert.Equal(0, runner.DeleteCalls);
            Assert.True(Directory.Exists(Path.Combine(
                fixture.Options.StagingDirectory!,
                fixture.SandboxName)));
        }
        finally
        {
            if (Directory.Exists(fixture.Options.StagingDirectory))
                Directory.Delete(fixture.Options.StagingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task AdoptedRecoveryLease_JournalsEachNewExecBeforeDispatchAndDeletesOnlyAfterDisarm()
    {
        var fixture = PrepareRetainedAdoptionFixture("codeybox-retained-exec-journal");
        var runner = new RetainedAdoptionRunner(
            fixture.Options.StagingDirectory!,
            fixture.SandboxName,
            fixture.Manifest.LeaseTokenSha256,
            fixture.ManifestHash)
        {
            BlockExec = true,
        };
        var provider = new IncusSandboxProvider(
            () => fixture.Options,
            NullLogger<IncusSandboxProvider>.Instance,
            timings: null,
            runner);

        try
        {
            var adopted = await provider.CreateAsync(fixture.RequestSpec)
                .WaitAsync(TimeSpan.FromSeconds(10));
            var execTask = adopted.ExecAsync(new SandboxExec { Argv = ["git", "status"] });
            await runner.ExecStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var wrapperCommand = runner.Commands.Single(command =>
                IsGuestCommand(command, IncusCloudInit.ExecWrapperPath)
                && command.Any(argument => argument.Contains("/env-", StringComparison.Ordinal)));
            var environmentPath = wrapperCommand.Single(argument =>
                argument.StartsWith($"{IncusCloudInit.ControlDirectory}/env-", StringComparison.Ordinal));
            var runId = environmentPath[(environmentPath.LastIndexOf('-') + 1)..];
            var retainedBytes = File.ReadAllBytes(Path.Combine(
                fixture.Options.StagingDirectory!,
                fixture.SandboxName,
                ".codeybox-recovery-retained.json"));
            var retained = IncusRecoveryManifestCodec.Deserialize(retainedBytes);

            Assert.True(retained.Retained);
            Assert.Equal(runId, Assert.IsType<IncusRecoveryPendingExec>(retained.PendingExec).RunId);

            runner.CompleteExec(Success("checkpoint-prepared\n"));
            var result = await execTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(result.Success, result.Stderr);

            var preserve = Assert.IsAssignableFrom<IPreserveOnDisposeSandbox>(adopted);
            preserve.DisablePreserveOnDispose();
            await adopted.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(1, runner.DeleteCalls);
            Assert.False(Directory.Exists(Path.Combine(
                fixture.Options.StagingDirectory!,
                fixture.SandboxName)));
        }
        finally
        {
            runner.CompleteExec(Failure("test cleanup\n"));
            if (Directory.Exists(fixture.Options.StagingDirectory))
                Directory.Delete(fixture.Options.StagingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Create_WithRetainedRecoveryLease_ExclusiveHostLeaseElectsOneAdopterAndAllowsRetry()
    {
        var fixture = PrepareRetainedAdoptionFixture("codeybox-retained-election");
        var sandboxRoot = Path.Combine(
            fixture.Options.StagingDirectory!,
            fixture.SandboxName);
        var runner = new RetainedAdoptionRunner(
            fixture.Options.StagingDirectory!,
            fixture.SandboxName,
            fixture.Manifest.LeaseTokenSha256,
            fixture.ManifestHash);
        var provider = new IncusSandboxProvider(
            () => fixture.Options,
            NullLogger<IncusSandboxProvider>.Instance,
            timings: null,
            runner);

        try
        {
            using (var electedElsewhere = IncusRecoveryManifestStore.Acquire(sandboxRoot))
            {
                var conflict = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    provider.CreateAsync(fixture.RequestSpec));

                Assert.Contains("already owned", conflict.Message, StringComparison.Ordinal);
                Assert.Equal(0, runner.ForcedStopCalls);
                Assert.Equal(0, runner.StartCalls);
            }

            var adopted = await provider.CreateAsync(fixture.RequestSpec);

            Assert.Equal(fixture.SandboxName, adopted.Id);
            Assert.Equal(1, runner.StartCalls);
            await adopted.DisposeAsync();
            Assert.Equal(0, runner.DeleteCalls);
        }
        finally
        {
            if (Directory.Exists(fixture.Options.StagingDirectory))
                Directory.Delete(fixture.Options.StagingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task RecoveryLease_Acquire_RetriesTransientContentionThenSucceedsWhenReleased()
    {
        var (stagingRoot, sandboxRoot) = PrepareOwnedSandboxRoot("codeybox-lease-retry-transient");
        try
        {
            var held = IncusRecoveryManifestStore.Acquire(sandboxRoot);
            // Model an unrelated fork that briefly holds an inherited copy of the
            // O_CLOEXEC lease descriptor and then drops it. A generous attempt
            // budget keeps this deterministic even under CPU starvation.
            var releaser = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(120));
                held.Dispose();
            });

            using var acquired = IncusRecoveryManifestStore.Acquire(
                sandboxRoot,
                maxAttempts: 400,
                retryDelay: TimeSpan.FromMilliseconds(25));

            await releaser;
            Assert.NotNull(acquired);
        }
        finally
        {
            if (Directory.Exists(stagingRoot))
                Directory.Delete(stagingRoot, recursive: true);
        }
    }

    [Fact]
    public void RecoveryLease_Acquire_ThrowsWhenLeaseIsGenuinelyHeldForTheWholeBudget()
    {
        var (stagingRoot, sandboxRoot) = PrepareOwnedSandboxRoot("codeybox-lease-retry-held");
        try
        {
            using var held = IncusRecoveryManifestStore.Acquire(sandboxRoot);

            var rejected = Assert.Throws<InvalidOperationException>(() =>
                IncusRecoveryManifestStore.Acquire(
                    sandboxRoot,
                    maxAttempts: 3,
                    retryDelay: TimeSpan.FromMilliseconds(5)));

            Assert.Contains("already owned", rejected.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(stagingRoot))
                Directory.Delete(stagingRoot, recursive: true);
        }
    }

    private static (string StagingRoot, string SandboxRoot) PrepareOwnedSandboxRoot(string sandboxName)
    {
        var stagingRoot = Path.Combine(
            Path.GetTempPath(),
            $"codeybox-incus-lease-{Guid.NewGuid():N}");
        IncusMountStaging.EnsureOwnedStagingRoot(stagingRoot);
        var sandboxRoot = Path.Combine(stagingRoot, sandboxName);
        Directory.CreateDirectory(sandboxRoot);
        IncusMountStaging.InitializeOwnedTree(sandboxRoot, sandboxName, DateTimeOffset.UtcNow);
        return (stagingRoot, sandboxRoot);
    }

    [Fact]
    public async Task Create_WithRetainedRecoveryLease_TopologyTamperFailsClosedAndCanBeReadopted()
    {
        var fixture = PrepareRetainedAdoptionFixture("codeybox-retained-topology");
        var tamperedRunner = new RetainedAdoptionRunner(
            fixture.Options.StagingDirectory!,
            fixture.SandboxName,
            fixture.Manifest.LeaseTokenSha256,
            fixture.ManifestHash)
        {
            TopologyOverride = EffectiveTopologyJson(
                mountSource: "/unexpected/source",
                mountPath: "/repo",
                recoveryTokenHash: fixture.Manifest.LeaseTokenSha256,
                recoveryManifestHash: fixture.ManifestHash),
        };
        var provider = new IncusSandboxProvider(
            () => fixture.Options,
            NullLogger<IncusSandboxProvider>.Instance,
            timings: null,
            tamperedRunner);

        try
        {
            await Assert.ThrowsAsync<SandboxExecutionUnavailableException>(() =>
                provider.CreateAsync(fixture.RequestSpec));

            Assert.Equal(1, tamperedRunner.ForcedStopCalls);
            Assert.Equal(0, tamperedRunner.StartCalls);
            Assert.Equal(0, tamperedRunner.DeleteCalls);

            var healthyRunner = new RetainedAdoptionRunner(
                fixture.Options.StagingDirectory!,
                fixture.SandboxName,
                fixture.Manifest.LeaseTokenSha256,
                fixture.ManifestHash);
            var healthyProvider = new IncusSandboxProvider(
                () => fixture.Options,
                NullLogger<IncusSandboxProvider>.Instance,
                timings: null,
                healthyRunner);
            var adopted = await healthyProvider.CreateAsync(fixture.RequestSpec);

            Assert.Equal(fixture.SandboxName, adopted.Id);
            Assert.Equal(1, healthyRunner.StartCalls);
            await adopted.DisposeAsync();
            Assert.Equal(0, healthyRunner.DeleteCalls);
        }
        finally
        {
            if (Directory.Exists(fixture.Options.StagingDirectory))
                Directory.Delete(fixture.Options.StagingDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Create_WithRetainedRecoveryLease_RejectsWrongCapabilityOrWorkItemWithoutMutatingVm(
        bool changeToken)
    {
        var fixture = PrepareRetainedAdoptionFixture("codeybox-retained-reject");
        var runner = new RetainedAdoptionRunner(
            fixture.Options.StagingDirectory!,
            fixture.SandboxName,
            fixture.Manifest.LeaseTokenSha256,
            fixture.ManifestHash);
        var provider = new IncusSandboxProvider(
            () => fixture.Options,
            NullLogger<IncusSandboxProvider>.Instance,
            timings: null,
            runner);
        var request = changeToken
            ? fixture.RequestSpec with
            {
                RecoveryLease = new SandboxRecoveryLease(
                    fixture.Lease.ProviderId,
                    fixture.Lease.SandboxId,
                    fixture.Lease.Token + "-wrong"),
            }
            : fixture.RequestSpec with { TimingWorkItemId = WorkItemId.New() };

        try
        {
            Exception rejected = changeToken
                ? await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CreateAsync(request))
                : await Assert.ThrowsAsync<InvalidDataException>(() => provider.CreateAsync(request));

            Assert.Contains(
                changeToken ? "capability binding" : "different sandbox specification or work item",
                rejected.Message,
                StringComparison.Ordinal);
            Assert.Equal(0, runner.ForcedStopCalls);
            Assert.Equal(0, runner.StartCalls);
            Assert.Equal(0, runner.DeleteCalls);
            Assert.True(Directory.Exists(Path.Combine(
                fixture.Options.StagingDirectory!,
                fixture.SandboxName)));
        }
        finally
        {
            if (Directory.Exists(fixture.Options.StagingDirectory))
                Directory.Delete(fixture.Options.StagingDirectory, recursive: true);
        }
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
            if (IsGuestCommand(argv, IncusCloudInit.ExecWrapperPath)
                && argv.Any(argument => argument.Contains("/env-", StringComparison.Ordinal)))
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
                [IncusCloudInit.DotnetCliHomeEnvironmentVariable] = "/tmp/caller-override",
            },
            EnvironmentVariablesToUnset =
            [
                "REMOVE_ME",
                IncusCloudInit.DotnetCliHomeEnvironmentVariable,
            ],
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
                $"CODEYBOX_SPEC_SECRET={specSecret}\0" +
                $"{IncusCloudInit.DotnetCliHomeEnvironmentVariable}={IncusCloudInit.DotnetCliHome}\0" +
                $"OPENAI_API_KEY={execSecret}\0",
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

    [Fact]
    public async Task Exec_ExecutionUnavailable_RecoversExactStoppedVmAndAllowsNativeResume()
    {
        const string sandboxName = "codeybox-exec-recovery";
        var root = Path.Combine(Path.GetTempPath(), $"codeybox-incus-exec-recovery-{Guid.NewGuid():N}");
        var wrapperExecCalls = 0;
        var forcedStopCalls = 0;
        var startCalls = 0;
        var restoredCredentialTmpfsCalls = 0;
        var status = "RUNNING";
        var options = FastLifecycleOptions();
        var runner = new ScriptedLifecycleRunner((argv, _, _) =>
        {
            if (IsFileCommand(argv, "push"))
                return Task.FromResult(Success());
            if (IsGuestCommand(argv, IncusCloudInit.ExecWrapperPath)
                && argv.Any(argument => argument.Contains("/env-", StringComparison.Ordinal)))
            {
                wrapperExecCalls++;
                return Task.FromResult(wrapperExecCalls == 1
                    ? new ProcessRunResult(
                        255,
                        "partial-agent-output\n",
                        "guest execution transport disappeared\n",
                        ExecutionUnavailable: true)
                    : Success("resumed-agent-output\n"));
            }
            if (IsFileCommand(argv, "pull")
                && argv.Any(argument => argument.Contains("/complete-", StringComparison.Ordinal))
                && wrapperExecCalls > 1)
            {
                return Task.FromResult(Success("0\n"));
            }
            if (IsFileCommand(argv, "pull"))
                return Task.FromResult(Failure());
            if (argv.Contains("stop", StringComparer.Ordinal) && argv.Contains("--force", StringComparer.Ordinal))
            {
                forcedStopCalls++;
                status = "STOPPED";
                return Task.FromResult(Success());
            }
            if (argv.Contains("start", StringComparer.Ordinal))
            {
                startCalls++;
                status = "RUNNING";
                return Task.FromResult(Success());
            }
            if (argv.Contains("list", StringComparer.Ordinal))
                return Task.FromResult(OwnedInstanceList(sandboxName, status));
            if (IsGuestCommand(argv, "mount") && argv.Contains("tmpfs", StringComparer.Ordinal))
            {
                restoredCredentialTmpfsCalls++;
                return Task.FromResult(Success());
            }
            if (IsGuestCommand(argv, "findmnt"))
                return Task.FromResult(Success("tmpfs\n"));
            if (IsGuestCommand(argv, "stat"))
                return Task.FromResult(Success("1000:1000:700\n"));
            if (IsFileCommand(argv, "delete") || IsGuestCommand(argv, "/bin/true") || IsGuestCommand(argv, "test"))
                return Task.FromResult(Success());
            if (IsGuestCommand(argv, "install"))
                return Task.FromResult(Success());
            throw new InvalidOperationException($"Unexpected Incus exec-recovery test command: {string.Join(' ', argv)}");
        });
        var sandbox = CreateSandbox(
            sandboxName,
            root,
            options,
            runner,
            spec: new SandboxSpec
            {
                ImageReference = "local-image",
                Mounts =
                [
                    new SandboxMount
                    {
                        SandboxPath = SandboxConventions.CredentialsDir,
                        Tmpfs = true,
                        SizeBytes = 1024,
                    },
                ],
            },
            newGuid: SequenceGuids(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Guid.Parse("22222222-2222-2222-2222-222222222222")),
            recoveryMounts:
            [
                new IncusPreparedMount(
                    HostSource: null,
                    GuestPath: SandboxConventions.CredentialsDir,
                    ReadOnly: false,
                    TmpfsSizeBytes: 1024),
            ]);

        try
        {
            var interrupted = await sandbox.ExecAsync(new SandboxExec { Argv = ["agent"] });

            Assert.False(interrupted.Success);
            Assert.True(interrupted.ExecutionUnavailable);
            Assert.Equal(255, interrupted.ExitCode);
            Assert.Equal("partial-agent-output\n", interrupted.Stdout);
            Assert.Equal("guest execution transport disappeared\n", interrupted.Stderr);
            Assert.Equal(1, forcedStopCalls);
            Assert.Equal(1, startCalls);
            Assert.Equal(1, restoredCredentialTmpfsCalls);
            var recoveryDeletes = runner.Commands
                .Where(command => IsFileCommand(command, "delete"))
                .Select(static command => command[^1])
                .ToArray();
            Assert.Equal(3, recoveryDeletes.Length);
            Assert.Contains(recoveryDeletes, path => path.EndsWith(
                "/env-11111111111111111111111111111111",
                StringComparison.Ordinal));
            Assert.Contains(recoveryDeletes, path => path.EndsWith(
                "/pid-11111111111111111111111111111111",
                StringComparison.Ordinal));
            Assert.Contains(recoveryDeletes, path => path.EndsWith(
                "/complete-11111111111111111111111111111111",
                StringComparison.Ordinal));

            var resumed = await sandbox.ExecAsync(new SandboxExec { Argv = ["agent", "resume"] });

            Assert.True(resumed.Success, resumed.Stderr);
            Assert.Equal("resumed-agent-output\n", resumed.Stdout);
            Assert.Equal(2, wrapperExecCalls);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Exec_ExecutionUnavailable_DoesNotRestartWhenEffectiveBridgeChanged()
    {
        const string sandboxName = "codeybox-exec-recovery-bridge";
        var root = Path.Combine(Path.GetTempPath(), $"codeybox-incus-exec-recovery-bridge-{Guid.NewGuid():N}");
        var status = "RUNNING";
        var startCalls = 0;
        var runner = new ScriptedLifecycleRunner((argv, _, _) =>
        {
            if (IsFileCommand(argv, "push"))
                return Task.FromResult(Success());
            if (IsGuestCommand(argv, IncusCloudInit.ExecWrapperPath))
            {
                return Task.FromResult(new ProcessRunResult(
                    255,
                    string.Empty,
                    "transport unavailable\n",
                    ExecutionUnavailable: true));
            }
            if (IsFileCommand(argv, "pull"))
                return Task.FromResult(Failure());
            if (argv.Contains("stop", StringComparer.Ordinal) && argv.Contains("--force", StringComparer.Ordinal))
            {
                status = "STOPPED";
                return Task.FromResult(Success());
            }
            if (argv.Contains("list", StringComparer.Ordinal))
                return Task.FromResult(OwnedInstanceList(sandboxName, status));
            if (argv.Contains("start", StringComparer.Ordinal))
            {
                startCalls++;
                status = "RUNNING";
                return Task.FromResult(Success());
            }
            throw new InvalidOperationException($"Unexpected Incus bridge-recovery test command: {string.Join(' ', argv)}");
        }, effectiveTopologyJson: () => EffectiveTopologyJson(bridge: "cb-evil"));
        var options = FastLifecycleOptions() with
        {
            ExecPidPollAttempts = 1,
            NetworkProfiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["restricted"] = "cb-auth",
            },
        };
        var spec = new SandboxSpec
        {
            ImageReference = "local-image",
            Network = new SandboxNetworkPolicy { ProfileName = "restricted" },
        };
        var sandbox = CreateSandbox(
            sandboxName,
            root,
            options,
            runner,
            spec: spec,
            recoveryBridge: "cb-auth");

        try
        {
            var interrupted = await sandbox.ExecAsync(new SandboxExec { Argv = ["agent"] });

            Assert.True(interrupted.ExecutionUnavailable);
            Assert.Equal(0, startCalls);
            Assert.DoesNotContain(
                runner.Commands,
                command => command.Contains("remove", StringComparer.Ordinal)
                    && command.Contains("device", StringComparer.Ordinal));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Exec_ExecutionUnavailable_RevalidatesCanonicalGuestPathBeforeHostDeviceRestart()
    {
        const string sandboxName = "codeybox-exec-recovery-canonical";
        var root = Path.Combine(Path.GetTempPath(), $"codeybox-incus-exec-recovery-canonical-{Guid.NewGuid():N}");
        var allowedRoot = Path.Combine(Path.GetTempPath(), $"codeybox-incus-exec-recovery-source-{Guid.NewGuid():N}");
        var source = Path.Combine(allowedRoot, "repo");
        Directory.CreateDirectory(source);
        var status = "RUNNING";
        var deviceAttached = true;
        var startCalls = 0;
        var removeCalls = 0;
        var addCalls = 0;
        var runner = new ScriptedLifecycleRunner((argv, _, _) =>
        {
            if (IsFileCommand(argv, "push"))
                return Task.FromResult(Success());
            if (IsGuestCommand(argv, IncusCloudInit.ExecWrapperPath)
                && argv.Any(argument => argument.Contains("/env-", StringComparison.Ordinal)))
            {
                return Task.FromResult(new ProcessRunResult(
                    255,
                    string.Empty,
                    "transport unavailable\n",
                    ExecutionUnavailable: true));
            }
            if (IsFileCommand(argv, "pull"))
                return Task.FromResult(Failure());
            if (argv.Contains("stop", StringComparer.Ordinal) && argv.Contains("--force", StringComparer.Ordinal))
            {
                status = "STOPPED";
                return Task.FromResult(Success());
            }
            if (argv.Contains("list", StringComparer.Ordinal))
                return Task.FromResult(OwnedInstanceList(sandboxName, status));
            if (argv.Contains("remove", StringComparer.Ordinal) && argv.Contains("device", StringComparer.Ordinal))
            {
                removeCalls++;
                deviceAttached = false;
                return Task.FromResult(Success());
            }
            if (argv.Contains("add", StringComparer.Ordinal) && argv.Contains("device", StringComparer.Ordinal))
            {
                addCalls++;
                deviceAttached = true;
                return Task.FromResult(Success());
            }
            if (argv.Contains("start", StringComparer.Ordinal))
            {
                startCalls++;
                status = "RUNNING";
                return Task.FromResult(Success());
            }
            if (IsGuestCommand(argv, "/bin/true"))
                return Task.FromResult(Success());
            throw new InvalidOperationException($"Unexpected Incus canonical-recovery test command: {string.Join(' ', argv)}");
        },
        effectiveTopologyJson: () => deviceAttached
            ? EffectiveTopologyJson(mountSource: source, mountPath: "/repo")
            : EffectiveTopologyJson(),
        canonicalPathResolver: path => string.Equals(path, "/repo", StringComparison.Ordinal) ? "/etc" : path);
        var options = FastLifecycleOptions() with
        {
            ExecPidPollAttempts = 1,
            AllowedHostMountRoots = [allowedRoot],
        };
        var spec = new SandboxSpec
        {
            ImageReference = "local-image",
            Mounts =
            [
                new SandboxMount
                {
                    HostPath = source,
                    SandboxPath = "/repo",
                    ReadOnly = true,
                },
            ],
        };
        var sandbox = CreateSandbox(
            sandboxName,
            root,
            options,
            runner,
            spec: spec,
            recoveryMounts: [new IncusPreparedMount(source, "/repo", ReadOnly: true)]);

        try
        {
            var interrupted = await sandbox.ExecAsync(new SandboxExec { Argv = ["agent"] });

            Assert.True(interrupted.ExecutionUnavailable);
            Assert.Equal(1, startCalls);
            Assert.Equal(1, removeCalls);
            Assert.Equal(0, addCalls);
            Assert.Equal("STOPPED", status);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
            if (Directory.Exists(allowedRoot))
                Directory.Delete(allowedRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Exec_ExecutionUnavailable_TwoPhaseHostDeviceReadmissionAllowsResume()
    {
        const string sandboxName = "codeybox-exec-recovery-readmission";
        var root = Path.Combine(Path.GetTempPath(), $"codeybox-incus-exec-recovery-readmission-{Guid.NewGuid():N}");
        var allowedRoot = Path.Combine(Path.GetTempPath(), $"codeybox-incus-exec-recovery-readmission-source-{Guid.NewGuid():N}");
        var source = Path.Combine(allowedRoot, "repo");
        Directory.CreateDirectory(source);
        using var sourceIdentityPin = IncusSafeFile.PinDirectoryNoFollow(source);
        var sourceInode = sourceIdentityPin.Identity.Inode;
        var status = "RUNNING";
        var deviceAttached = true;
        var wrapperExecCalls = 0;
        var startCalls = 0;
        var removeCalls = 0;
        var addCalls = 0;
        var runner = new ScriptedLifecycleRunner((argv, _, _) =>
        {
            if (IsFileCommand(argv, "push"))
                return Task.FromResult(Success());
            if (IsGuestCommand(argv, IncusCloudInit.ExecWrapperPath)
                && argv.Any(argument => argument.Contains("/env-", StringComparison.Ordinal)))
            {
                wrapperExecCalls++;
                return Task.FromResult(wrapperExecCalls == 1
                    ? new ProcessRunResult(
                        255,
                        "partial\n",
                        "transport unavailable\n",
                        ExecutionUnavailable: true)
                    : Success("resumed-with-readmitted-mount\n"));
            }
            if (IsFileCommand(argv, "pull")
                && argv.Any(argument => argument.Contains("/complete-", StringComparison.Ordinal))
                && wrapperExecCalls > 1)
            {
                return Task.FromResult(Success("0\n"));
            }
            if (IsFileCommand(argv, "pull"))
                return Task.FromResult(Failure());
            if (argv.Contains("stop", StringComparer.Ordinal) && argv.Contains("--force", StringComparer.Ordinal))
            {
                status = "STOPPED";
                return Task.FromResult(Success());
            }
            if (argv.Contains("list", StringComparer.Ordinal))
                return Task.FromResult(OwnedInstanceList(sandboxName, status));
            if (argv.Contains("remove", StringComparer.Ordinal) && argv.Contains("device", StringComparer.Ordinal))
            {
                removeCalls++;
                deviceAttached = false;
                return Task.FromResult(Success());
            }
            if (argv.Contains("add", StringComparer.Ordinal) && argv.Contains("device", StringComparer.Ordinal))
            {
                addCalls++;
                deviceAttached = true;
                return Task.FromResult(Success());
            }
            if (argv.Contains("start", StringComparer.Ordinal))
            {
                startCalls++;
                status = "RUNNING";
                return Task.FromResult(Success());
            }
            if (IsGuestCommand(argv, "findmnt") && argv.Contains("FSTYPE", StringComparer.Ordinal))
                return Task.FromResult(Success("virtiofs\n"));
            if (IsGuestCommand(argv, "findmnt") && argv.Contains("OPTIONS", StringComparer.Ordinal))
                return Task.FromResult(Success("ro\n"));
            if (argv.Contains("device", StringComparer.Ordinal)
                && argv.Contains("get", StringComparer.Ordinal)
                && argv.Contains("source", StringComparer.Ordinal))
            {
                return Task.FromResult(Success(source + "\n"));
            }
            if (argv.Contains("device", StringComparer.Ordinal)
                && argv.Contains("get", StringComparer.Ordinal)
                && argv.Contains("io.bus", StringComparer.Ordinal))
            {
                return Task.FromResult(Success("virtiofs\n"));
            }
            if (IsGuestCommand(argv, "stat") && argv.Contains("%i", StringComparer.Ordinal))
                return Task.FromResult(Success(sourceInode.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n"));
            if (IsFileCommand(argv, "delete")
                || IsGuestCommand(argv, "/bin/true")
                || IsGuestCommand(argv, "install")
                || IsGuestCommand(argv, "test"))
            {
                return Task.FromResult(Success());
            }
            throw new InvalidOperationException($"Unexpected Incus readmission test command: {string.Join(' ', argv)}");
        }, effectiveTopologyJson: () => deviceAttached
            ? EffectiveTopologyJson(mountSource: source, mountPath: "/repo")
            : EffectiveTopologyJson());
        var options = FastLifecycleOptions() with
        {
            ExecPidPollAttempts = 1,
            AllowedHostMountRoots = [allowedRoot],
        };
        var spec = new SandboxSpec
        {
            ImageReference = "local-image",
            Mounts =
            [
                new SandboxMount
                {
                    HostPath = source,
                    SandboxPath = "/repo",
                    ReadOnly = true,
                },
            ],
        };
        var sandbox = CreateSandbox(
            sandboxName,
            root,
            options,
            runner,
            spec: spec,
            newGuid: SequenceGuids(
                Guid.Parse("77777777-7777-7777-7777-777777777777"),
                Guid.Parse("88888888-8888-8888-8888-888888888888")),
            recoveryMounts: [new IncusPreparedMount(source, "/repo", ReadOnly: true)]);

        try
        {
            var interrupted = await sandbox.ExecAsync(new SandboxExec { Argv = ["agent"] });

            Assert.True(interrupted.ExecutionUnavailable);
            Assert.Equal(2, startCalls);
            Assert.Equal(1, removeCalls);
            Assert.Equal(1, addCalls);
            Assert.True(deviceAttached);
            Assert.Equal("RUNNING", status);

            var resumed = await sandbox.ExecAsync(new SandboxExec { Argv = ["agent", "resume"] });

            Assert.True(resumed.Success, resumed.Stderr);
            Assert.Equal("resumed-with-readmitted-mount\n", resumed.Stdout);
            Assert.Equal(2, wrapperExecCalls);
            var topologyTransitions = runner.Commands
                .Where(command => command.Contains("device", StringComparer.Ordinal)
                    && (command.Contains("remove", StringComparer.Ordinal)
                        || command.Contains("add", StringComparer.Ordinal)))
                .Select(command => command.Contains("remove", StringComparer.Ordinal) ? "remove" : "add")
                .ToArray();
            Assert.Equal(["remove", "add"], topologyTransitions);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
            if (Directory.Exists(allowedRoot))
                Directory.Delete(allowedRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Exec_ExecutionUnavailable_ReauthorizesIntentionalExecutableSymlink()
    {
        const string sandboxName = "codeybox-exec-recovery-tool-link";
        const string executableTarget = "/opt/codeybox/tool";
        const string executableLink = "/usr/local/bin/codeybox-tool";
        var root = Path.Combine(Path.GetTempPath(), $"codeybox-incus-exec-recovery-tool-link-{Guid.NewGuid():N}");
        var status = "RUNNING";
        var wrapperCalls = 0;
        var readLinkCalls = 0;
        var runner = new ScriptedLifecycleRunner((argv, _, _) =>
        {
            if (IsFileCommand(argv, "push"))
                return Task.FromResult(Success());
            if (IsGuestCommand(argv, IncusCloudInit.ExecWrapperPath)
                && argv.Any(argument => argument.Contains("/env-", StringComparison.Ordinal)))
            {
                wrapperCalls++;
                return Task.FromResult(wrapperCalls == 1
                    ? new ProcessRunResult(
                        255,
                        string.Empty,
                        "transport unavailable\n",
                        ExecutionUnavailable: true)
                    : Success("resumed\n"));
            }
            if (IsFileCommand(argv, "pull")
                && argv.Any(argument => argument.Contains("/complete-", StringComparison.Ordinal))
                && wrapperCalls > 1)
            {
                return Task.FromResult(Success("0\n"));
            }
            if (IsFileCommand(argv, "pull"))
                return Task.FromResult(Failure());
            if (argv.Contains("stop", StringComparer.Ordinal) && argv.Contains("--force", StringComparer.Ordinal))
            {
                status = "STOPPED";
                return Task.FromResult(Success());
            }
            if (argv.Contains("start", StringComparer.Ordinal))
            {
                status = "RUNNING";
                return Task.FromResult(Success());
            }
            if (argv.Contains("list", StringComparer.Ordinal))
                return Task.FromResult(OwnedInstanceList(sandboxName, status));
            if (IsGuestCommand(argv, "readlink"))
            {
                readLinkCalls++;
                return Task.FromResult(Success(executableTarget + "\n"));
            }
            if (IsFileCommand(argv, "delete")
                || IsGuestCommand(argv, "/bin/true")
                || IsGuestCommand(argv, "install")
                || IsGuestCommand(argv, "test"))
            {
                return Task.FromResult(Success());
            }
            throw new InvalidOperationException(
                $"Unexpected executable-link recovery command: {string.Join(' ', argv)}");
        });
        var options = FastLifecycleOptions() with
        {
            ExecPidPollAttempts = 1,
            ExecutableProvisions =
            [
                new BaselineExecutableProvision
                {
                    HostSourcePath = "/host/not-read-by-this-test",
                    VmDestPath = executableTarget,
                    VmSymlinks = [executableLink],
                },
            ],
        };
        var sandbox = CreateSandbox(
            sandboxName,
            root,
            options,
            runner,
            newGuid: SequenceGuids(
                Guid.Parse("12345678-1234-1234-1234-123456789001"),
                Guid.Parse("12345678-1234-1234-1234-123456789002")));

        try
        {
            var interrupted = await sandbox.ExecAsync(new SandboxExec { Argv = ["agent"] });

            Assert.True(interrupted.ExecutionUnavailable);
            Assert.Equal("RUNNING", status);
            Assert.Equal(2, readLinkCalls);

            var resumed = await sandbox.ExecAsync(new SandboxExec { Argv = ["agent", "resume"] });

            Assert.True(resumed.Success, resumed.Stderr);
            Assert.Equal("resumed\n", resumed.Stdout);
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
    public async Task InfrastructureFailure_TimeoutOrUncertainEnvironmentPush_PublishesIdempotentRecoveryLease(
        bool failEnvironmentPush)
    {
        const string sandboxName = "codeybox-exec-retain-timeout";
        var root = Path.Combine(Path.GetTempPath(), $"codeybox-incus-exec-retain-timeout-{Guid.NewGuid():N}");
        var status = "RUNNING";
        var runner = new ScriptedLifecycleRunner((argv, _, _) =>
        {
            if (IsFileCommand(argv, "push"))
            {
                if (failEnvironmentPush)
                    throw new TimeoutException("environment push completion is unknown");
                return Task.FromResult(Success());
            }
            if (IsGuestCommand(argv, IncusCloudInit.ExecWrapperPath))
                throw new TimeoutException("exec transport timed out");
            if (IsFileCommand(argv, "pull"))
                return Task.FromResult(Failure());
            if (IsFileCommand(argv, "delete"))
                return Task.FromResult(Success());
            if (argv.Contains("stop", StringComparer.Ordinal) && argv.Contains("--force", StringComparer.Ordinal))
            {
                status = "STOPPED";
                return Task.FromResult(Success());
            }
            if (argv.Contains("list", StringComparer.Ordinal))
                return Task.FromResult(OwnedInstanceList(sandboxName, status));
            if (argv.Contains("start", StringComparer.Ordinal))
                return Task.FromResult(Failure("infrastructure remains unavailable\n"));
            if (IsGuestCommand(argv, "test"))
            {
                if (failEnvironmentPush
                    && argv.Contains("!", StringComparer.Ordinal)
                    && argv.Any(argument => argument.Contains("/env-", StringComparison.Ordinal)))
                {
                    return Task.FromResult(Failure());
                }
                return Task.FromResult(Success());
            }
            throw new InvalidOperationException(
                $"Unexpected infrastructure-retention command: {string.Join(' ', argv)}");
        });
        var options = FastLifecycleOptions() with
        {
            StagingDirectory = root,
            ExecPidPollAttempts = 1,
            ExecControlFileCleanupAttempts = 1,
        };
        IncusMountStaging.EnsureOwnedStagingRoot(root);
        var inactive = 0;
        var sandbox = CreateSandbox(
            sandboxName,
            root,
            options,
            runner,
            onDisposed: _ => Interlocked.Increment(ref inactive),
            newGuid: SequenceGuids(
                Guid.Parse("99999999-9999-9999-9999-999999999991"),
                Guid.Parse("99999999-9999-9999-9999-999999999992")));
        SandboxLiveCounter.Increment();
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                sandbox.ExecAsync(new SandboxExec { Argv = ["agent"] }));
            var commandsBeforeRetention = runner.Commands.Count;

            var lease = await sandbox.RetainForInfrastructureRecoveryAsync();
            var repeatedLease = await sandbox.RetainForInfrastructureRecoveryAsync();

            Assert.NotNull(lease);
            Assert.Equal(lease, repeatedLease);
            Assert.Equal(sandboxName, lease.SandboxId);
            Assert.Equal(commandsBeforeRetention, runner.Commands.Count);

            await sandbox.DisposeAsync();

            Assert.Equal(1, inactive);
            Assert.True(Directory.Exists(Path.Combine(root, sandboxName)));
            Assert.DoesNotContain(
                runner.Commands,
                command => command.Contains("delete", StringComparer.Ordinal)
                    && !IsFileCommand(command, "delete"));

            var sandboxRoot = Path.Combine(root, sandboxName);
            var baseManifestPath = Directory.EnumerateFiles(
                    sandboxRoot,
                    ".codeybox-recovery-*.json",
                    SearchOption.TopDirectoryOnly)
                .Single(path => !string.Equals(
                    Path.GetFileName(path),
                    ".codeybox-recovery-retained.json",
                    StringComparison.Ordinal));
            var baseManifestName = Path.GetFileName(baseManifestPath);
            const string manifestPrefix = ".codeybox-recovery-";
            const string manifestSuffix = ".json";
            var manifestHash = baseManifestName.Substring(
                manifestPrefix.Length,
                baseManifestName.Length - manifestPrefix.Length - manifestSuffix.Length);
            var adoptionRunner = new RetainedAdoptionRunner(
                root,
                sandboxName,
                IncusRecoveryManifestCodec.ComputeTokenSha256(lease.Token),
                manifestHash);
            var restartedProvider = new IncusSandboxProvider(
                () => options,
                NullLogger<IncusSandboxProvider>.Instance,
                timings: null,
                adoptionRunner);

            var adopted = await restartedProvider.CreateAsync(new SandboxSpec
            {
                ImageReference = "local-image",
                RecoveryLease = lease,
            });

            Assert.Equal(sandboxName, adopted.Id);
            Assert.Equal("RUNNING", adoptionRunner.Status);
            await adopted.DisposeAsync();
            Assert.Equal(0, adoptionRunner.DeleteCalls);
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
    public async Task Exec_ExecutionUnavailable_DoesNotRestartWhenAuthorizedHostSourceDisappeared()
    {
        const string sandboxName = "codeybox-exec-recovery-source";
        var root = Path.Combine(Path.GetTempPath(), $"codeybox-incus-exec-recovery-source-vm-{Guid.NewGuid():N}");
        var allowedRoot = Path.Combine(Path.GetTempPath(), $"codeybox-incus-exec-recovery-source-host-{Guid.NewGuid():N}");
        var source = Path.Combine(allowedRoot, "repo");
        Directory.CreateDirectory(source);
        var status = "RUNNING";
        var startCalls = 0;
        var topologyQueries = 0;
        var runner = new ScriptedLifecycleRunner((argv, _, _) =>
        {
            if (IsFileCommand(argv, "push"))
                return Task.FromResult(Success());
            if (IsGuestCommand(argv, IncusCloudInit.ExecWrapperPath))
            {
                return Task.FromResult(new ProcessRunResult(
                    255,
                    string.Empty,
                    "transport unavailable\n",
                    ExecutionUnavailable: true));
            }
            if (IsFileCommand(argv, "pull"))
                return Task.FromResult(Failure());
            if (argv.Contains("stop", StringComparer.Ordinal) && argv.Contains("--force", StringComparer.Ordinal))
            {
                status = "STOPPED";
                if (Directory.Exists(source))
                    Directory.Delete(source);
                return Task.FromResult(Success());
            }
            if (argv.Contains("list", StringComparer.Ordinal))
                return Task.FromResult(OwnedInstanceList(sandboxName, status));
            if (argv.Contains("start", StringComparer.Ordinal))
            {
                startCalls++;
                return Task.FromResult(Success());
            }
            throw new InvalidOperationException($"Unexpected Incus source-recovery test command: {string.Join(' ', argv)}");
        }, effectiveTopologyJson: () =>
        {
            topologyQueries++;
            return EffectiveTopologyJson(mountSource: source, mountPath: "/repo");
        });
        var options = FastLifecycleOptions() with
        {
            ExecPidPollAttempts = 1,
            AllowedHostMountRoots = [allowedRoot],
        };
        var spec = new SandboxSpec
        {
            ImageReference = "local-image",
            Mounts =
            [
                new SandboxMount
                {
                    HostPath = source,
                    SandboxPath = "/repo",
                    ReadOnly = true,
                },
            ],
        };
        var sandbox = CreateSandbox(
            sandboxName,
            root,
            options,
            runner,
            spec: spec,
            recoveryMounts: [new IncusPreparedMount(source, "/repo", ReadOnly: true)]);

        try
        {
            var interrupted = await sandbox.ExecAsync(new SandboxExec { Argv = ["agent"] });

            Assert.True(interrupted.ExecutionUnavailable);
            Assert.Equal(0, startCalls);
            Assert.Equal(0, topologyQueries);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
            if (Directory.Exists(allowedRoot))
                Directory.Delete(allowedRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Exec_ExecutionUnavailable_ImmediateRecoveryFailure_IsRetriedByNextExec()
    {
        const string sandboxName = "codeybox-exec-lazy-recovery";
        var root = Path.Combine(Path.GetTempPath(), $"codeybox-incus-exec-lazy-recovery-{Guid.NewGuid():N}");
        var time = new ControllableTimeProvider(
            new DateTimeOffset(2026, 7, 12, 3, 0, 0, TimeSpan.Zero));
        var wrapperExecCalls = 0;
        var forcedStopCalls = 0;
        var startCalls = 0;
        var status = "RUNNING";
        var runner = new ScriptedLifecycleRunner((argv, _, _) =>
        {
            if (IsFileCommand(argv, "push"))
                return Task.FromResult(Success());
            if (IsGuestCommand(argv, IncusCloudInit.ExecWrapperPath)
                && argv.Any(argument => argument.Contains("/env-", StringComparison.Ordinal)))
            {
                wrapperExecCalls++;
                return Task.FromResult(wrapperExecCalls == 1
                    ? new ProcessRunResult(
                        255,
                        "partial-agent-output\n",
                        "guest execution transport disappeared\n",
                        ExecutionUnavailable: true)
                    : Success("resumed-agent-output\n"));
            }
            if (IsFileCommand(argv, "pull")
                && argv.Any(argument => argument.Contains("/complete-", StringComparison.Ordinal))
                && wrapperExecCalls > 1)
            {
                return Task.FromResult(Success("0\n"));
            }
            if (IsFileCommand(argv, "pull"))
                return Task.FromResult(Failure());
            if (argv.Contains("stop", StringComparer.Ordinal) && argv.Contains("--force", StringComparer.Ordinal))
            {
                forcedStopCalls++;
                status = "STOPPED";
                return Task.FromResult(Success());
            }
            if (argv.Contains("start", StringComparer.Ordinal))
            {
                startCalls++;
                if (startCalls == 1)
                    return Task.FromResult(Failure("temporary start failure\n"));
                status = "RUNNING";
                return Task.FromResult(Success());
            }
            if (argv.Contains("list", StringComparer.Ordinal))
                return Task.FromResult(OwnedInstanceList(sandboxName, status));
            if (IsFileCommand(argv, "delete")
                || IsGuestCommand(argv, "/bin/true")
                || IsGuestCommand(argv, "install")
                || IsGuestCommand(argv, "test"))
            {
                return Task.FromResult(Success());
            }
            throw new InvalidOperationException($"Unexpected Incus lazy-recovery test command: {string.Join(' ', argv)}");
        });
        var liveOptions = FastLifecycleOptions() with { ExecPidPollAttempts = 1 };
        var sandbox = CreateSandbox(
            sandboxName,
            root,
            liveOptions,
            runner,
            timeProvider: time,
            newGuid: SequenceGuids(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Guid.Parse("22222222-2222-2222-2222-222222222222")),
            liveOptionsAccessor: () => liveOptions);

        try
        {
            var interrupted = await sandbox.ExecAsync(new SandboxExec { Argv = ["agent"] });

            Assert.True(interrupted.ExecutionUnavailable);
            Assert.Equal(1, forcedStopCalls);
            Assert.Equal(1, startCalls);

            // The delayed policy is read from the live accessor at the next
            // exec boundary, after the immediate attempt has already failed.
            liveOptions = liveOptions with
            {
                InterruptedExecRecoveryRetryAttempts = 1,
                InterruptedExecRecoveryRetryDelay = TimeSpan.FromSeconds(1),
            };
            var resumedTask = sandbox.ExecAsync(new SandboxExec { Argv = ["agent", "resume"] });
            await AdvanceUntilCompletedAsync(time, resumedTask, TimeSpan.FromSeconds(1));
            var resumed = await resumedTask;

            Assert.True(resumed.Success, resumed.Stderr);
            Assert.Equal("resumed-agent-output\n", resumed.Stdout);
            Assert.Equal(2, startCalls);
            Assert.Equal(2, wrapperExecCalls);
            Assert.Contains(
                runner.Commands,
                command => IsFileCommand(command, "delete")
                    && command[^1].EndsWith("/env-11111111111111111111111111111111", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Exec_ExecutionUnavailable_PostStartPreparationFailure_IsRestoppedBeforeDelayedRetry()
    {
        const string sandboxName = "codeybox-exec-post-start-recovery";
        var root = Path.Combine(Path.GetTempPath(), $"codeybox-incus-exec-post-start-recovery-{Guid.NewGuid():N}");
        var time = new ControllableTimeProvider(
            new DateTimeOffset(2026, 7, 12, 3, 30, 0, TimeSpan.Zero));
        var wrapperExecCalls = 0;
        var forcedStopCalls = 0;
        var startCalls = 0;
        var runtimePreparationCalls = 0;
        var status = "RUNNING";
        var runner = new ScriptedLifecycleRunner((argv, _, _) =>
        {
            if (IsFileCommand(argv, "push"))
                return Task.FromResult(Success());
            if (IsGuestCommand(argv, IncusCloudInit.ExecWrapperPath)
                && argv.Any(argument => argument.Contains("/env-", StringComparison.Ordinal)))
            {
                wrapperExecCalls++;
                return Task.FromResult(wrapperExecCalls == 1
                    ? new ProcessRunResult(
                        255,
                        "partial-agent-output\n",
                        "guest execution transport disappeared\n",
                        ExecutionUnavailable: true)
                    : Success("resumed-agent-output\n"));
            }
            if (IsFileCommand(argv, "pull")
                && argv.Any(argument => argument.Contains("/complete-", StringComparison.Ordinal))
                && wrapperExecCalls > 1)
            {
                return Task.FromResult(Success("0\n"));
            }
            if (IsFileCommand(argv, "pull"))
                return Task.FromResult(Failure());
            if (argv.Contains("stop", StringComparer.Ordinal) && argv.Contains("--force", StringComparer.Ordinal))
            {
                forcedStopCalls++;
                status = "STOPPED";
                return Task.FromResult(Success());
            }
            if (argv.Contains("start", StringComparer.Ordinal))
            {
                startCalls++;
                if (!string.Equals(status, "STOPPED", StringComparison.Ordinal))
                    return Task.FromResult(Failure("VM must be stopped before recovery start\n"));
                status = "RUNNING";
                return Task.FromResult(Success());
            }
            if (argv.Contains("list", StringComparer.Ordinal))
                return Task.FromResult(OwnedInstanceList(sandboxName, status));
            if (IsGuestCommand(argv, "/bin/true"))
                return Task.FromResult(Success());
            if (IsGuestCommand(argv, "install"))
            {
                runtimePreparationCalls++;
                return Task.FromResult(runtimePreparationCalls == 1
                    ? Failure("runtime preparation failed\n")
                    : Success());
            }
            if (IsFileCommand(argv, "delete") || IsGuestCommand(argv, "test"))
                return Task.FromResult(Success());
            throw new InvalidOperationException($"Unexpected Incus post-start recovery test command: {string.Join(' ', argv)}");
        });
        var options = FastLifecycleOptions() with
        {
            ExecPidPollAttempts = 1,
            InterruptedExecRecoveryRetryAttempts = 1,
            InterruptedExecRecoveryRetryDelay = TimeSpan.FromSeconds(1),
        };
        var sandbox = CreateSandbox(
            sandboxName,
            root,
            options,
            runner,
            timeProvider: time,
            newGuid: SequenceGuids(
                Guid.Parse("33333333-3333-3333-3333-333333333333"),
                Guid.Parse("44444444-4444-4444-4444-444444444444")));

        try
        {
            var interrupted = await sandbox.ExecAsync(new SandboxExec { Argv = ["agent"] });

            Assert.True(interrupted.ExecutionUnavailable);
            Assert.Equal(1, startCalls);
            Assert.Equal(2, forcedStopCalls);
            Assert.Equal("STOPPED", status);

            var resumedTask = sandbox.ExecAsync(new SandboxExec { Argv = ["agent", "resume"] });
            await AdvanceUntilCompletedAsync(time, resumedTask, TimeSpan.FromSeconds(1));
            var resumed = await resumedTask;

            Assert.True(resumed.Success, resumed.Stderr);
            Assert.Equal("resumed-agent-output\n", resumed.Stdout);
            Assert.Equal(2, startCalls);
            Assert.Equal(2, forcedStopCalls);
            Assert.Equal(3, runtimePreparationCalls);
            var lifecycleTransitions = runner.Commands
                .Where(command => command.Contains("start", StringComparer.Ordinal)
                    || (command.Contains("stop", StringComparer.Ordinal)
                        && command.Contains("--force", StringComparer.Ordinal)))
                .Select(command => command.Contains("start", StringComparer.Ordinal) ? "start" : "stop")
                .ToArray();
            Assert.Equal(["stop", "start", "stop", "start"], lifecycleTransitions);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Exec_ExecutionUnavailable_ExhaustedWindowCanRecoverAtLaterExecBoundary()
    {
        const string sandboxName = "codeybox-exec-lazy-recovery-exhausted";
        var root = Path.Combine(Path.GetTempPath(), $"codeybox-incus-exec-lazy-recovery-exhausted-{Guid.NewGuid():N}");
        var time = new ControllableTimeProvider(
            new DateTimeOffset(2026, 7, 12, 4, 0, 0, TimeSpan.Zero));
        var wrapperExecCalls = 0;
        var startCalls = 0;
        var infrastructureHealthy = false;
        var status = "RUNNING";
        var runner = new ScriptedLifecycleRunner((argv, _, _) =>
        {
            if (IsFileCommand(argv, "push"))
                return Task.FromResult(Success());
            if (IsGuestCommand(argv, IncusCloudInit.ExecWrapperPath)
                && argv.Any(argument => argument.Contains("/env-", StringComparison.Ordinal)))
            {
                wrapperExecCalls++;
                return Task.FromResult(wrapperExecCalls == 1
                    ? new ProcessRunResult(
                        255,
                        "partial\n",
                        "transport unavailable\n",
                        ExecutionUnavailable: true)
                    : Success("resumed-after-infrastructure-recovery\n"));
            }
            if (IsFileCommand(argv, "pull")
                && argv.Any(argument => argument.Contains("/complete-", StringComparison.Ordinal))
                && wrapperExecCalls > 1)
            {
                return Task.FromResult(Success("0\n"));
            }
            if (IsFileCommand(argv, "pull"))
                return Task.FromResult(Failure());
            if (argv.Contains("stop", StringComparer.Ordinal) && argv.Contains("--force", StringComparer.Ordinal))
            {
                status = "STOPPED";
                return Task.FromResult(Success());
            }
            if (argv.Contains("start", StringComparer.Ordinal))
            {
                startCalls++;
                if (!infrastructureHealthy)
                    return Task.FromResult(Failure("start remains unavailable\n"));
                status = "RUNNING";
                return Task.FromResult(Success());
            }
            if (argv.Contains("list", StringComparer.Ordinal))
                return Task.FromResult(OwnedInstanceList(sandboxName, status));
            if (IsFileCommand(argv, "delete")
                || IsGuestCommand(argv, "/bin/true")
                || IsGuestCommand(argv, "install")
                || IsGuestCommand(argv, "test"))
            {
                return Task.FromResult(Success());
            }
            throw new InvalidOperationException($"Unexpected Incus lazy-exhaustion test command: {string.Join(' ', argv)}");
        });
        var options = FastLifecycleOptions() with
        {
            ExecPidPollAttempts = 1,
            InterruptedExecRecoveryRetryAttempts = 2,
            InterruptedExecRecoveryRetryDelay = TimeSpan.FromSeconds(1),
        };
        var sandbox = CreateSandbox(
            sandboxName,
            root,
            options,
            runner,
            timeProvider: time);

        try
        {
            var interrupted = await sandbox.ExecAsync(new SandboxExec { Argv = ["agent"] });
            Assert.True(interrupted.ExecutionUnavailable);
            Assert.Equal(1, startCalls);

            var rejectedTask = sandbox.ExecAsync(new SandboxExec { Argv = ["agent", "resume"] });
            await AdvanceUntilCompletedAsync(time, rejectedTask, TimeSpan.FromSeconds(1));
            var rejected = await rejectedTask;

            Assert.True(rejected.ExecutionUnavailable);
            Assert.Contains("bounded attempt window", rejected.Stderr, StringComparison.Ordinal);
            Assert.Equal(3, startCalls);
            Assert.Equal(1, wrapperExecCalls);

            infrastructureHealthy = true;
            var resumedTask = sandbox.ExecAsync(new SandboxExec { Argv = ["agent", "resume-again"] });
            await AdvanceUntilCompletedAsync(time, resumedTask, TimeSpan.FromSeconds(1));
            var resumed = await resumedTask;

            Assert.True(resumed.Success, resumed.Stderr);
            Assert.Equal("resumed-after-infrastructure-recovery\n", resumed.Stdout);
            Assert.Equal(4, startCalls);
            Assert.Equal(2, wrapperExecCalls);
            Assert.DoesNotContain(
                runner.Commands,
                command => command.Contains("delete", StringComparer.Ordinal)
                    && !command.Contains("file", StringComparer.Ordinal));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Exec_ExecutionUnavailable_CancelledDelayedRecovery_PreservesPendingPoison()
    {
        const string sandboxName = "codeybox-exec-lazy-recovery-cancelled";
        var root = Path.Combine(Path.GetTempPath(), $"codeybox-incus-exec-lazy-recovery-cancelled-{Guid.NewGuid():N}");
        var time = new ControllableTimeProvider(
            new DateTimeOffset(2026, 7, 12, 5, 0, 0, TimeSpan.Zero));
        var startCalls = 0;
        var status = "RUNNING";
        var runner = new ScriptedLifecycleRunner((argv, _, _) =>
        {
            if (IsFileCommand(argv, "push"))
                return Task.FromResult(Success());
            if (IsGuestCommand(argv, IncusCloudInit.ExecWrapperPath))
            {
                return Task.FromResult(new ProcessRunResult(
                    255,
                    string.Empty,
                    "transport unavailable\n",
                    ExecutionUnavailable: true));
            }
            if (IsFileCommand(argv, "pull"))
                return Task.FromResult(Failure());
            if (argv.Contains("stop", StringComparer.Ordinal) && argv.Contains("--force", StringComparer.Ordinal))
            {
                status = "STOPPED";
                return Task.FromResult(Success());
            }
            if (argv.Contains("start", StringComparer.Ordinal))
            {
                startCalls++;
                return Task.FromResult(Failure("temporary start failure\n"));
            }
            if (argv.Contains("list", StringComparer.Ordinal))
                return Task.FromResult(OwnedInstanceList(sandboxName, status));
            throw new InvalidOperationException($"Unexpected Incus lazy-cancellation test command: {string.Join(' ', argv)}");
        });
        var liveOptions = FastLifecycleOptions() with
        {
            ExecPidPollAttempts = 1,
            InterruptedExecRecoveryRetryAttempts = 2,
            InterruptedExecRecoveryRetryDelay = TimeSpan.FromSeconds(10),
        };
        var sandbox = CreateSandbox(
            sandboxName,
            root,
            liveOptions,
            runner,
            timeProvider: time,
            liveOptionsAccessor: () => liveOptions);

        try
        {
            var interrupted = await sandbox.ExecAsync(new SandboxExec { Argv = ["agent"] });
            Assert.True(interrupted.ExecutionUnavailable);
            Assert.Equal(1, startCalls);

            using var cts = new CancellationTokenSource();
            var retryTask = sandbox.ExecAsync(
                new SandboxExec { Argv = ["agent", "resume"] },
                cts.Token);
            Assert.False(retryTask.IsCompleted);
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retryTask);
            Assert.Equal(1, startCalls);

            liveOptions = liveOptions with { InterruptedExecRecoveryRetryAttempts = 0 };
            var commandCount = runner.Commands.Count;
            var stillPoisoned = await sandbox.ExecAsync(new SandboxExec { Argv = ["agent", "resume-again"] });
            Assert.True(stillPoisoned.ExecutionUnavailable);
            Assert.Contains("bounded attempt window", stillPoisoned.Stderr, StringComparison.Ordinal);
            Assert.Equal(commandCount, runner.Commands.Count);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Exec_ExecutionUnavailable_RemainsPoisonedWhenRecoveredControlFileAbsenceCannotBeProved()
    {
        const string sandboxName = "codeybox-exec-recovery-cleanup-failure";
        var root = Path.Combine(Path.GetTempPath(), $"codeybox-incus-exec-recovery-cleanup-{Guid.NewGuid():N}");
        var status = "RUNNING";
        var environmentAbsenceChecks = 0;
        var runner = new ScriptedLifecycleRunner((argv, _, _) =>
        {
            if (IsFileCommand(argv, "push"))
                return Task.FromResult(Success());
            if (IsGuestCommand(argv, IncusCloudInit.ExecWrapperPath)
                && argv.Any(argument => argument.Contains("/env-", StringComparison.Ordinal)))
            {
                return Task.FromResult(new ProcessRunResult(
                    255,
                    "partial\n",
                    "transport unavailable\n",
                    ExecutionUnavailable: true));
            }
            if (IsFileCommand(argv, "pull"))
                return Task.FromResult(Failure());
            if (argv.Contains("stop", StringComparer.Ordinal) && argv.Contains("--force", StringComparer.Ordinal))
            {
                status = "STOPPED";
                return Task.FromResult(Success());
            }
            if (argv.Contains("start", StringComparer.Ordinal))
            {
                status = "RUNNING";
                return Task.FromResult(Success());
            }
            if (argv.Contains("list", StringComparer.Ordinal))
                return Task.FromResult(OwnedInstanceList(sandboxName, status));
            if (IsFileCommand(argv, "delete") || IsGuestCommand(argv, "/bin/true") || IsGuestCommand(argv, "install"))
                return Task.FromResult(Success());
            if (IsGuestCommand(argv, "test"))
            {
                if (argv.Contains("!", StringComparer.Ordinal)
                    && argv.Any(argument => argument.Contains("/env-", StringComparison.Ordinal)))
                {
                    environmentAbsenceChecks++;
                    return Task.FromResult(Failure());
                }
                return Task.FromResult(Success());
            }
            throw new InvalidOperationException($"Unexpected Incus cleanup-failure test command: {string.Join(' ', argv)}");
        });
        var options = FastLifecycleOptions() with
        {
            ExecPidPollAttempts = 1,
            ExecControlFileCleanupAttempts = 2,
        };
        var sandbox = CreateSandbox(
            sandboxName,
            root,
            options,
            runner,
            newGuid: SequenceGuids(
                Guid.Parse("33333333-3333-3333-3333-333333333333"),
                Guid.Parse("44444444-4444-4444-4444-444444444444")));

        try
        {
            var interrupted = await sandbox.ExecAsync(new SandboxExec { Argv = ["agent"] });

            Assert.True(interrupted.ExecutionUnavailable);
            Assert.Equal("partial\n", interrupted.Stdout);
            Assert.Equal(2, environmentAbsenceChecks);
            var cleanupDeletes = runner.Commands
                .Where(command => IsFileCommand(command, "delete"))
                .Select(static command => command[^1])
                .ToArray();
            Assert.Equal(4, cleanupDeletes.Length);
            Assert.Equal(2, cleanupDeletes.Count(path => path.EndsWith(
                "/env-33333333333333333333333333333333",
                StringComparison.Ordinal)));
            var commandCount = runner.Commands.Count;

            var poisoned = await sandbox.ExecAsync(new SandboxExec { Argv = ["agent", "resume"] });

            Assert.True(poisoned.ExecutionUnavailable);
            Assert.Contains("bounded attempt window", poisoned.Stderr, StringComparison.Ordinal);
            Assert.Equal(commandCount, runner.Commands.Count);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Exec_ExecutionUnavailable_DoesNotRestartWhenExactInstanceOwnershipChanged()
    {
        const string sandboxName = "codeybox-exec-recovery-owner-change";
        var root = Path.Combine(Path.GetTempPath(), $"codeybox-incus-exec-recovery-owner-{Guid.NewGuid():N}");
        var startCalls = 0;
        var runner = new ScriptedLifecycleRunner((argv, _, _) =>
        {
            if (IsFileCommand(argv, "push"))
                return Task.FromResult(Success());
            if (IsGuestCommand(argv, IncusCloudInit.ExecWrapperPath)
                && argv.Any(argument => argument.Contains("/env-", StringComparison.Ordinal)))
            {
                return Task.FromResult(new ProcessRunResult(
                    255,
                    string.Empty,
                    "transport unavailable\n",
                    ExecutionUnavailable: true));
            }
            if (IsFileCommand(argv, "pull") || argv.Contains("stop", StringComparer.Ordinal))
                return Task.FromResult(Failure());
            if (argv.Contains("list", StringComparer.Ordinal))
            {
                return Task.FromResult(Success(
                    $"[{{\"name\":\"{sandboxName}\",\"type\":\"virtual-machine\",\"status\":\"STOPPED\"," +
                    $"\"config\":{{\"{IncusSandboxProvider.ManagedKey}\":\"false\"," +
                    $"\"{IncusSandboxProvider.KindKey}\":\"{IncusSandboxProvider.SandboxKind}\"}}}}]"));
            }
            if (argv.Contains("start", StringComparer.Ordinal))
            {
                startCalls++;
                return Task.FromResult(Success());
            }
            throw new InvalidOperationException($"Unexpected Incus owner-change test command: {string.Join(' ', argv)}");
        });
        var options = FastLifecycleOptions() with { ExecPidPollAttempts = 1 };
        var sandbox = CreateSandbox(
            sandboxName,
            root,
            options,
            runner,
            newGuid: SequenceGuids(
                Guid.Parse("55555555-5555-5555-5555-555555555555"),
                Guid.Parse("66666666-6666-6666-6666-666666666666")));

        try
        {
            var interrupted = await sandbox.ExecAsync(new SandboxExec { Argv = ["agent"] });

            Assert.True(interrupted.ExecutionUnavailable);
            Assert.Equal(0, startCalls);
            Assert.DoesNotContain(runner.Commands, command => IsFileCommand(command, "delete"));
            var commandCount = runner.Commands.Count;

            var poisoned = await sandbox.ExecAsync(new SandboxExec { Argv = ["agent", "resume"] });

            Assert.True(poisoned.ExecutionUnavailable);
            Assert.Contains("bounded attempt window", poisoned.Stderr, StringComparison.Ordinal);
            Assert.Equal(commandCount, runner.Commands.Count);
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
    public async Task Exec_OrdinaryFailureWithoutMatchingCompletionSentinel_ReturnsTypedInterruptionAndFailsClosedWhenRestartFails(
        bool returnMismatchedSentinel)
    {
        const string sandboxName = "codeybox-exec-sentinel";
        var root = Path.Combine(Path.GetTempPath(), $"codeybox-incus-exec-sentinel-{Guid.NewGuid():N}");
        var completionPullCalls = 0;
        var pidPullCalls = 0;
        var wrapperExecCalls = 0;
        var forcedStopCalls = 0;
        var startCalls = 0;
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
            if (argv.Contains("start", StringComparer.Ordinal))
            {
                startCalls++;
                return Task.FromResult(Failure("restart unavailable\n"));
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
            var interruption = await sandbox.ExecAsync(new SandboxExec { Argv = ["false"] });

            Assert.False(interruption.Success);
            Assert.True(interruption.ExecutionUnavailable);
            Assert.Equal("ordinary command failure\n", interruption.Stderr);
            Assert.Equal(1, wrapperExecCalls);
            Assert.Equal(2, completionPullCalls);
            Assert.Equal(2, pidPullCalls);
            Assert.Equal(1, forcedStopCalls);
            Assert.Equal(1, startCalls);
            Assert.DoesNotContain(runner.Commands, command => IsFileCommand(command, "delete"));
            var commandCount = runner.Commands.Count;

            var poisoned = await sandbox.ExecAsync(new SandboxExec { Argv = ["true"] });

            Assert.True(poisoned.ExecutionUnavailable);
            Assert.Contains("bounded attempt window", poisoned.Stderr, StringComparison.Ordinal);
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
        InterruptedExecRecoveryRetryAttempts = 0,
    };

    private static async Task AdvanceUntilCompletedAsync(
        ControllableTimeProvider time,
        Task task,
        TimeSpan advance)
    {
        const int maximumSchedulerTurns = 100;
        for (var turn = 0; turn < maximumSchedulerTurns && !task.IsCompleted; turn++)
        {
            time.Advance(advance);
            // This delay only yields to continuations released by the injected
            // fake clock; it does not drive the recovery delay or its outcome.
            await Task.Delay(TimeSpan.FromMilliseconds(1));
        }

        Assert.True(task.IsCompleted, "Incus delayed recovery did not complete after advancing the injected clock.");
    }

    private static IncusSandbox CreateSandbox(
        string sandboxName,
        string root,
        IncusSandboxOptions options,
        IProcessRunner runner,
        Action<string>? onDisposed = null,
        SandboxSpec? spec = null,
        TimeProvider? timeProvider = null,
        Func<Guid>? newGuid = null,
        Func<IncusSandboxOptions>? liveOptionsAccessor = null,
        IReadOnlyList<IncusPreparedMount>? recoveryMounts = null,
        string? recoveryBridge = null)
    {
        var sandboxRoot = Path.Combine(root, sandboxName);
        Directory.CreateDirectory(sandboxRoot);
        IncusMountStaging.InitializeOwnedTree(sandboxRoot, sandboxName, DateTimeOffset.UtcNow);
        var effectiveSpec = spec ?? new SandboxSpec { ImageReference = "local-image" };
        var authorization = RecoveryAuthorization(
            options,
            effectiveSpec,
            recoveryMounts,
            recoveryBridge);
        var recoveryState = CreateRecoveryState(
            sandboxRoot,
            sandboxName,
            effectiveSpec,
            options,
            authorization);
        if (runner is ScriptedLifecycleRunner scripted)
        {
            scripted.SetRecoveryBinding(
                recoveryState.Manifest.LeaseTokenSha256,
                recoveryState.ManifestHash);
        }
        return new IncusSandbox(
            sandboxName,
            sandboxRoot,
            root,
            effectiveSpec,
            options,
            new IncusCliRunner(runner, timeProvider),
            NullLogger.Instance,
            timings: null,
            WorkItemId.New(),
            "work",
            baselineRef: null,
            resourceUsageStore: null,
            onDisposed ?? (_ => { }),
            authorization,
            recoveryState.Lease,
            recoveryState.Manifest,
            recoveryState.Store,
            timeProvider,
            newGuid,
            liveOptionsAccessor);
    }

    private static IncusRecoveryAuthorization RecoveryAuthorization(
        IncusSandboxOptions options,
        SandboxSpec? spec = null,
        IReadOnlyList<IncusPreparedMount>? mounts = null,
        string? bridge = null)
    {
        var effectiveSpec = spec ?? new SandboxSpec { ImageReference = "local-image" };
        return IncusRecoveryAuthorization.CaptureValidated(
            bridge,
            mounts ?? [],
            effectiveSpec.Mounts.Select(static mount => mount.SandboxPath).ToArray(),
            [],
            options);
    }

    private static RecoveryTestState CreateRecoveryState(
        string sandboxRoot,
        string sandboxName,
        SandboxSpec spec,
        IncusSandboxOptions options,
        IncusRecoveryAuthorization authorization,
        string? baselineRef = null)
    {
        var token = $"test-recovery-token-{sandboxName}";
        var lease = new SandboxRecoveryLease(IncusSandboxProvider.ProviderId, sandboxName, token);
        var manifest = IncusRecoveryManifest.Create(
            sandboxName,
            spec,
            options,
            IncusRecoveryManifestCodec.ComputeTokenSha256(token),
            baselineRef,
            authorization);
        var store = IncusRecoveryManifestStore.Acquire(sandboxRoot);
        var manifestHash = store.Write(
            manifest,
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        return new RecoveryTestState(lease, manifest, manifestHash, store);
    }

    private sealed record RecoveryTestState(
        SandboxRecoveryLease Lease,
        IncusRecoveryManifest Manifest,
        string ManifestHash,
        IncusRecoveryManifestStore Store);

    private static RetainedAdoptionFixture PrepareRetainedAdoptionFixture(string sandboxName)
    {
        var stagingRoot = Path.Combine(
            Path.GetTempPath(),
            $"codeybox-incus-retained-adoption-{Guid.NewGuid():N}");
        var options = FastLifecycleOptions() with
        {
            StagingDirectory = stagingRoot,
            UseBaselineImages = false,
            // This fixture drives the real filesystem preflight and recovery
            // adoption against a synchronous mock CLI, so the operation deadline
            // only guards against a hang — it is never expected to elapse. The
            // 250 ms FastLifecycleOptions default is a real wall-clock timer, and
            // under full-suite CPU starvation it can fire between arming the
            // deadline and the mock returning, aborting a correct run. Give it
            // generous headroom; no assertion here depends on the timeout value.
            OperationTimeout = TimeSpan.FromSeconds(30),
        };
        var originalSpec = SandboxConventions.WithTimingEnvironment(new SandboxSpec
        {
            ImageReference = "local-image",
            TimingWorkItemId = WorkItemId.New(),
            TimingPhase = "work",
        });
        IncusMountStaging.EnsureOwnedStagingRoot(stagingRoot);
        var sandboxRoot = Path.Combine(stagingRoot, sandboxName);
        Directory.CreateDirectory(sandboxRoot);
        IncusMountStaging.InitializeOwnedTree(
            sandboxRoot,
            sandboxName,
            DateTimeOffset.UtcNow);
        var authorization = RecoveryAuthorization(options, originalSpec);
        var state = CreateRecoveryState(
            sandboxRoot,
            sandboxName,
            originalSpec,
            options,
            authorization);
        var oldRunId = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        var retained = state.Manifest.Retain(new IncusRecoveryPendingExec(
            oldRunId,
            $"{IncusCloudInit.ControlDirectory}/env-{oldRunId}",
            $"{IncusCloudInit.ControlDirectory}/pid-{oldRunId}",
            $"{IncusCloudInit.ControlDirectory}/complete-{oldRunId}",
            HostDevicesDetached: false));
        state.Store.WriteRetained(
            retained,
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
        state.Store.Dispose();
        authorization.Dispose();
        return new RetainedAdoptionFixture(
            sandboxName,
            options,
            originalSpec with { RecoveryLease = state.Lease },
            state.Lease,
            retained,
            state.ManifestHash);
    }

    private sealed record RetainedAdoptionFixture(
        string SandboxName,
        IncusSandboxOptions Options,
        SandboxSpec RequestSpec,
        SandboxRecoveryLease Lease,
        IncusRecoveryManifest Manifest,
        string ManifestHash);

    private static ProcessRunResult Success(string stdout = "") =>
        new(0, stdout, string.Empty);

    private static ProcessRunResult Failure(string stderr = "") =>
        new(1, string.Empty, stderr);

    private static Func<Guid> SequenceGuids(params Guid[] values)
    {
        var remaining = new Queue<Guid>(values);
        return () => remaining.Count > 0
            ? remaining.Dequeue()
            : throw new InvalidOperationException("The deterministic GUID sequence was exhausted.");
    }

    private static ProcessRunResult OwnedInstanceList(string sandboxName, string status) =>
        string.IsNullOrEmpty(status)
            ? Success("[]")
            : Success(OwnedInstanceJson(sandboxName, status));

    private static string OwnedInstanceJson(
        string sandboxName,
        string status,
        string? recoveryTokenHash = null,
        string? recoveryManifestHash = null)
    {
        var config = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [IncusSandboxProvider.ManagedKey] = "true",
            [IncusSandboxProvider.KindKey] = IncusSandboxProvider.SandboxKind,
        };
        if (recoveryTokenHash is not null && recoveryManifestHash is not null)
        {
            config[IncusSandboxProvider.RecoveryTokenHashKey] = recoveryTokenHash;
            config[IncusSandboxProvider.RecoveryManifestHashKey] = recoveryManifestHash;
        }
        return JsonSerializer.Serialize(new[]
        {
            new
            {
                name = sandboxName,
                type = "virtual-machine",
                status,
                config,
            },
        });
    }

    private static string EffectiveTopologyJson(
        string storagePool = "codeybox-zfs",
        string? bridge = null,
        string? mountSource = null,
        string? mountPath = null,
        string? recoveryTokenHash = null,
        string? recoveryManifestHash = null) =>
        JsonSerializer.Serialize(new
        {
            type = "virtual-machine",
            config = RecoveryBindingConfig(recoveryTokenHash, recoveryManifestHash),
            expanded_config = new { },
            profiles = Array.Empty<string>(),
            expanded_devices = BuildEffectiveDevices(storagePool, bridge, mountSource, mountPath),
        });

    private static Dictionary<string, string> RecoveryBindingConfig(
        string? recoveryTokenHash,
        string? recoveryManifestHash)
    {
        var config = new Dictionary<string, string>(StringComparer.Ordinal);
        if (recoveryTokenHash is not null && recoveryManifestHash is not null)
        {
            config[IncusSandboxProvider.RecoveryTokenHashKey] = recoveryTokenHash;
            config[IncusSandboxProvider.RecoveryManifestHashKey] = recoveryManifestHash;
        }
        return config;
    }

    private static Dictionary<string, Dictionary<string, string>> BuildEffectiveDevices(
        string storagePool,
        string? bridge,
        string? mountSource,
        string? mountPath)
    {
        var devices = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal)
        {
            ["root"] = new(StringComparer.Ordinal)
            {
                ["type"] = "disk",
                ["path"] = "/",
                ["pool"] = storagePool,
            },
        };
        if (bridge is not null)
        {
            devices["codeybox-net"] = new(StringComparer.Ordinal)
            {
                ["type"] = "nic",
                ["nictype"] = "bridged",
                ["parent"] = bridge,
                ["name"] = "eth0",
            };
        }
        if (mountSource is not null && mountPath is not null)
        {
            devices["m000"] = new(StringComparer.Ordinal)
            {
                ["type"] = "disk",
                ["source"] = mountSource,
                ["path"] = mountPath,
                ["io.bus"] = "virtiofs",
                ["readonly"] = "true",
            };
        }
        return devices;
    }

    private static bool IsFileCommand(IReadOnlyList<string> argv, string verb) =>
        argv.Contains("file", StringComparer.Ordinal) && argv.Contains(verb, StringComparer.Ordinal);

    private static bool IsGuestCommand(IReadOnlyList<string> argv, string executable) =>
        argv.Contains("exec", StringComparer.Ordinal) && argv.Contains(executable, StringComparer.Ordinal);

    private sealed class ScriptedLifecycleRunner(
        Func<IReadOnlyList<string>, string?, CancellationToken, Task<ProcessRunResult>> handler,
        Func<string>? effectiveTopologyJson = null,
        Func<string, string>? canonicalPathResolver = null)
        : IProcessRunner
    {
        private string? _recoveryTokenHash;
        private string? _recoveryManifestHash;
        internal List<IReadOnlyList<string>> Commands { get; } = [];

        internal void SetRecoveryBinding(string tokenHash, string manifestHash)
        {
            _recoveryTokenHash = tokenHash;
            _recoveryManifestHash = manifestHash;
        }

        public async Task<ProcessRunResult> RunAsync(
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
            if (argv.Count == 3
                && string.Equals(argv[1], "query", StringComparison.Ordinal)
                && argv[2].StartsWith("/1.0/instances/", StringComparison.Ordinal))
            {
                var topology = effectiveTopologyJson?.Invoke() ?? EffectiveTopologyJson();
                return Success(InjectRecoveryBinding(topology));
            }
            if (IsGuestCommand(argv, "/usr/bin/realpath"))
            {
                var canonical = canonicalPathResolver?.Invoke(argv[^1]) ?? argv[^1];
                return Success(canonical + "\n");
            }
            var result = await handler(argv, stdin, ct);
            return result with { Stdout = InjectRecoveryBinding(result.Stdout) };
        }

        private string InjectRecoveryBinding(string json)
        {
            if (_recoveryTokenHash is null
                || _recoveryManifestHash is null
                || string.IsNullOrEmpty(json)
                || json.Contains(IncusSandboxProvider.RecoveryTokenHashKey, StringComparison.Ordinal))
            {
                return json;
            }
            var sandboxKind =
                $"\"{IncusSandboxProvider.KindKey}\":\"{IncusSandboxProvider.SandboxKind}\"";
            var withOwnedBinding = json.Replace(
                sandboxKind,
                sandboxKind
                + $",\"{IncusSandboxProvider.RecoveryTokenHashKey}\":\"{_recoveryTokenHash}\""
                + $",\"{IncusSandboxProvider.RecoveryManifestHashKey}\":\"{_recoveryManifestHash}\"",
                StringComparison.Ordinal);
            if (!ReferenceEquals(withOwnedBinding, json)
                && !string.Equals(withOwnedBinding, json, StringComparison.Ordinal))
            {
                return withOwnedBinding;
            }
            return json.Replace(
                "\"config\":{}",
                $"\"config\":{{\"{IncusSandboxProvider.RecoveryTokenHashKey}\":\"{_recoveryTokenHash}\",\"{IncusSandboxProvider.RecoveryManifestHashKey}\":\"{_recoveryManifestHash}\"}}",
                StringComparison.Ordinal);
        }
    }

    private sealed class MetricsLifecycleRunner(string sandboxName) : IProcessRunner
    {
        private bool _deleted;
        private string? _recoveryTokenHash;
        private string? _recoveryManifestHash;
        internal List<IReadOnlyList<string>> Commands { get; } = [];

        internal void SetRecoveryBinding(string tokenHash, string manifestHash) =>
            (_recoveryTokenHash, _recoveryManifestHash) = (tokenHash, manifestHash);

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
                    : OwnedInstanceJson(sandboxName, "RUNNING", _recoveryTokenHash, _recoveryManifestHash);
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
        private string? _recoveryTokenHash;
        private string? _recoveryManifestHash;
        internal int DeleteCalls { get; private set; }

        internal void SetRecoveryBinding(string tokenHash, string manifestHash) =>
            (_recoveryTokenHash, _recoveryManifestHash) = (tokenHash, manifestHash);

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
                    : OwnedInstanceJson(sandboxName, "RUNNING", _recoveryTokenHash, _recoveryManifestHash);
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
        private string? _recoveryTokenHash;
        private string? _recoveryManifestHash;
        internal bool CompleteDeletion { get; set; }
        internal int DeleteCalls { get; private set; }

        internal void SetRecoveryBinding(string tokenHash, string manifestHash) =>
            (_recoveryTokenHash, _recoveryManifestHash) = (tokenHash, manifestHash);

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
                    : OwnedInstanceJson(sandboxName, "RUNNING", _recoveryTokenHash, _recoveryManifestHash);
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

    private sealed class RetainedAdoptionRunner(
        string stagingRoot,
        string sandboxName,
        string tokenHash,
        string manifestHash) : IProcessRunner
    {
        private bool _deleted;
        private readonly TaskCompletionSource _execStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<ProcessRunResult> _execCompletion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal string Status { get; private set; } = "RUNNING";
        internal int ForcedStopCalls { get; private set; }
        internal int StartCalls { get; private set; }
        internal int DeleteCalls { get; private set; }
        internal bool BlockExec { get; init; }
        internal string? TopologyOverride { get; init; }
        internal TaskCompletionSource ExecStarted => _execStarted;
        internal List<IReadOnlyList<string>> Commands { get; } = [];

        internal void CompleteExec(ProcessRunResult result) =>
            _execCompletion.TrySetResult(result);

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
            if (argv.SequenceEqual(["incus", "query", "/1.0"]))
            {
                return Task.FromResult(Success(
                    "{\"metadata\":{\"api_extensions\":[\"disk_io_bus_cache_filesystem\",\"projects_restrictions\"]," +
                    "\"environment\":{\"kernel_version\":\"6.14.0-test\"}}}"));
            }
            if (argv.SequenceEqual(["incus", "project", "list", "--format=json"]))
                return Task.FromResult(Success("[{\"name\":\"codeybox\"}]"));
            if (argv.SequenceEqual(["incus", "query", "/1.0/projects/codeybox"]))
                return Task.FromResult(Success(ManagedProjectQuery(stagingRoot)));
            if (argv.Contains("storage", StringComparer.Ordinal)
                && argv.Contains("list", StringComparer.Ordinal))
            {
                return Task.FromResult(Success(
                    "[{\"name\":\"codeybox-zfs\",\"driver\":\"zfs\",\"config\":{}}]"));
            }
            if (argv.Count == 3
                && string.Equals(argv[1], "query", StringComparison.Ordinal)
                && argv[2].StartsWith("/1.0/instances/", StringComparison.Ordinal))
            {
                return Task.FromResult(Success(
                    TopologyOverride
                    ?? EffectiveTopologyJson(
                        recoveryTokenHash: tokenHash,
                        recoveryManifestHash: manifestHash)));
            }
            if (argv.Contains("list", StringComparer.Ordinal))
            {
                return Task.FromResult(_deleted
                    ? Success("[]")
                    : Success(OwnedInstanceJson(
                        sandboxName,
                        Status,
                        tokenHash,
                        manifestHash)));
            }
            if (argv.Contains("stop", StringComparer.Ordinal)
                && argv.Contains("--force", StringComparer.Ordinal))
            {
                ForcedStopCalls++;
                Status = "STOPPED";
                return Task.FromResult(Success());
            }
            if (argv.Contains("start", StringComparer.Ordinal))
            {
                StartCalls++;
                Status = "RUNNING";
                return Task.FromResult(Success());
            }
            if (IsFileCommand(argv, "push"))
                return Task.FromResult(Success());
            if (IsGuestCommand(argv, IncusCloudInit.ExecWrapperPath)
                && argv.Any(argument => argument.Contains("/env-", StringComparison.Ordinal))
                && argv.Contains("git", StringComparer.Ordinal)
                && argv.Contains("status", StringComparer.Ordinal)
                && BlockExec)
            {
                _execStarted.TrySetResult();
                return _execCompletion.Task;
            }
            if (IsFileCommand(argv, "pull")
                && argv.Any(argument => argument.Contains("/complete-", StringComparison.Ordinal)))
            {
                return Task.FromResult(Success("0\n"));
            }
            if (IsFileCommand(argv, "pull"))
                return Task.FromResult(Failure());
            if (IsFileCommand(argv, "delete")
                || IsGuestCommand(argv, "/bin/true")
                || IsGuestCommand(argv, "install")
                || IsGuestCommand(argv, "test"))
            {
                return Task.FromResult(Success());
            }
            if (argv.Contains("delete", StringComparer.Ordinal))
            {
                DeleteCalls++;
                _deleted = true;
                return Task.FromResult(Success());
            }
            throw new InvalidOperationException(
                $"Unexpected retained-adoption command: {string.Join(' ', argv)}");
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
