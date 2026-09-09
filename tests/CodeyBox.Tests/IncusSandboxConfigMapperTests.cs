using CodeyBox.Api;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Incus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

public sealed class IncusSandboxConfigMapperTests
{
    [Fact]
    public void Build_DoesNotInheritMultipassProvisioning()
    {
        var options = CreateOptions();
        options.MultipassExtraRuncmd = ["install-from-multipass"];
        options.MultipassExtraCloudInit = "packages: [multipass-only]";
        options.MultipassUseBaselineImages = false;
        options.MultipassPackageCacheSeeds =
        [
            new PackageCacheSeedConfig
            {
                HostSourcePath = "/multipass/cache",
                VmDestPath = "/var/cache/multipass",
            },
        ];
        options.MultipassExecutableProvisions =
        [
            new ExecutableProvisionConfig
            {
                HostSourcePath = "/multipass/tool",
                VmDestPath = "/usr/local/bin/multipass-tool",
            },
        ];
        options.Incus = new IncusSandboxConfig
        {
            ExtraRuncmd = [],
            PackageCacheSeeds = [],
            ExecutableProvisions = [],
            ExtraCloudInit = null,
            UseBaselineImages = true,
        };

        var mapped = IncusSandboxConfigMapper.Build(options);

        Assert.Empty(mapped.ExtraRuncmd);
        Assert.Empty(mapped.PackageCacheSeeds);
        Assert.Empty(mapped.ExecutableProvisions);
        Assert.Null(mapped.ExtraCloudInit);
        Assert.True(mapped.UseBaselineImages);
    }

    [Fact]
    public void Build_MapsAndDeepCopiesIncusBaselineProvisioning()
    {
        var seed = new PackageCacheSeedConfig
        {
            HostSourcePath = "/srv/cache/nuget.tgz",
            VmDestPath = "/var/cache/codeybox/nuget",
            MaxSizeMB = 12.5,
        };
        var provision = new ExecutableProvisionConfig
        {
            HostSourcePath = "/srv/tools/agy",
            VmDestPath = "/home/ubuntu/.local/bin/agy",
            VmSymlinks = ["/usr/local/bin/agy"],
            Label = "antigravity",
        };
        var verificationArgv = new List<string> { "agy", "--version" };
        var options = CreateOptions();
        options.Incus = new IncusSandboxConfig
        {
            PackageCacheSeeds = [seed],
            ExecutableProvisions = [provision],
            MaxExecutableProvisionBytes = 64L * 1024 * 1024,
            MaxAggregateExecutableProvisionBytes = 128L * 1024 * 1024,
            MaxPackageCacheSeedBytes = 256L * 1024 * 1024,
            MaxAggregatePackageCacheSeedBytes = 512L * 1024 * 1024,
            MaxPackageCacheSeedEntries = 456,
        };

        var mapped = IncusSandboxConfigMapper.Build(
            options,
            NullLogger.Instance,
            [new BaselineVerificationCommand("antigravity", verificationArgv, "agy missing")]);
        seed.HostSourcePath = "/changed/cache";
        provision.VmDestPath = "/changed/tool";
        provision.VmSymlinks[0] = "/changed/link";
        verificationArgv[0] = "changed";

        var mappedSeed = Assert.Single(mapped.PackageCacheSeeds);
        Assert.Equal("/srv/cache/nuget.tgz", mappedSeed.HostSourcePath);
        Assert.Equal("/var/cache/codeybox/nuget", mappedSeed.VmDestPath);
        Assert.Equal(12.5, mappedSeed.MaxSizeMB);
        var mappedProvision = Assert.Single(mapped.ExecutableProvisions);
        Assert.Equal("/srv/tools/agy", mappedProvision.HostSourcePath);
        Assert.Equal("/home/ubuntu/.local/bin/agy", mappedProvision.VmDestPath);
        Assert.Equal(["/usr/local/bin/agy"], mappedProvision.VmSymlinks);
        Assert.Equal("antigravity", mappedProvision.Label);
        var verification = Assert.Single(mapped.BaselineVerificationCommands);
        Assert.Equal("antigravity", verification.Label);
        Assert.Equal(["agy", "--version"], verification.Argv);
        Assert.Equal("agy missing", verification.FailureHint);
        Assert.Equal(64L * 1024 * 1024, mapped.MaxExecutableProvisionBytes);
        Assert.Equal(128L * 1024 * 1024, mapped.MaxAggregateExecutableProvisionBytes);
        Assert.Equal(256L * 1024 * 1024, mapped.MaxPackageCacheSeedBytes);
        Assert.Equal(512L * 1024 * 1024, mapped.MaxAggregatePackageCacheSeedBytes);
        Assert.Equal(456, mapped.MaxPackageCacheSeedEntries);
    }

    [Fact]
    public void BaselineProvisioningSnapshots_EnforceObservedCollectionAndUtf8Bounds()
    {
        var tooManySeeds = new DeceptiveReadOnlyCollection<PackageCacheSeedConfig>(
            reportedCount: 0,
            Enumerable.Range(0, IncusSandboxOptions.MaximumPackageCacheSeeds + 1)
                .Select(index => new PackageCacheSeedConfig
                {
                    HostSourcePath = $"/cache/{index}",
                    VmDestPath = $"/var/cache/{index}",
                }));
        var tooManyProvisions = new DeceptiveReadOnlyCollection<ExecutableProvisionConfig>(
            reportedCount: 0,
            Enumerable.Range(0, IncusSandboxOptions.MaximumExecutableProvisions + 1)
                .Select(index => new ExecutableProvisionConfig
                {
                    HostSourcePath = $"/tools/{index}",
                    VmDestPath = $"/usr/local/bin/tool-{index}",
                }));
        var tooManyCommands = new DeceptiveReadOnlyCollection<BaselineVerificationCommand>(
            reportedCount: 0,
            Enumerable.Range(0, IncusSandboxOptions.MaximumBaselineVerificationCommands + 1)
                .Select(index => new BaselineVerificationCommand($"agent-{index}", ["true"])));

        Assert.Throws<InvalidOperationException>(() =>
            IncusSandboxConfigMapper.SnapshotPackageCacheSeeds(tooManySeeds));
        Assert.Throws<InvalidOperationException>(() =>
            IncusSandboxConfigMapper.SnapshotExecutableProvisions(tooManyProvisions));
        Assert.Throws<InvalidOperationException>(() =>
            IncusSandboxConfigMapper.SnapshotBaselineVerificationCommands(tooManyCommands));
        Assert.Equal(IncusSandboxOptions.MaximumPackageCacheSeeds + 1, tooManySeeds.EnumeratedCount);
        Assert.Equal(IncusSandboxOptions.MaximumExecutableProvisions + 1, tooManyProvisions.EnumeratedCount);
        Assert.Equal(IncusSandboxOptions.MaximumBaselineVerificationCommands + 1, tooManyCommands.EnumeratedCount);

        var oversizedUtf8 = new string('\u00e9', IncusSandboxOptions.MaximumProvisioningTextUtf8Bytes);
        var textFailure = Assert.Throws<InvalidOperationException>(() =>
            IncusSandboxConfigMapper.SnapshotPackageCacheSeeds(
            [
                new PackageCacheSeedConfig
                {
                    HostSourcePath = oversizedUtf8,
                    VmDestPath = "/var/cache/seed",
                },
            ]));
        Assert.Contains("UTF-8 bytes", textFailure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BaselineProvisioningSnapshots_BoundNestedSymlinksAndVerificationArgv()
    {
        var symlinks = new DeceptiveReadOnlyCollection<string>(
            reportedCount: 0,
            Enumerable.Range(0, IncusSandboxOptions.MaximumExecutableSymlinks + 1)
                .Select(index => $"/usr/local/bin/tool-{index}"));
        var arguments = Enumerable
            .Range(0, IncusSandboxOptions.MaximumVerificationArgv + 1)
            .Select(index => $"arg-{index}")
            .ToArray();

        Assert.Throws<InvalidOperationException>(() =>
            IncusSandboxConfigMapper.SnapshotExecutableProvisions(
            [
                new ExecutableProvisionConfig
                {
                    HostSourcePath = "/srv/tool",
                    VmDestPath = "/usr/local/bin/tool",
                    VmSymlinks = symlinks.ToList(),
                },
            ]));
        Assert.Throws<InvalidOperationException>(() =>
            IncusSandboxConfigMapper.SnapshotBaselineVerificationCommands(
            [
                new BaselineVerificationCommand("tool", arguments),
            ]));
    }

    [Fact]
    public void CodeyBoxOptions_BindsNestedIncusBaselineProvisioning()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeyBox:Incus:PackageCacheSeeds:0:HostSourcePath"] = "/srv/cache/npm.tgz",
                ["CodeyBox:Incus:PackageCacheSeeds:0:VmDestPath"] = "/var/cache/codeybox/npm",
                ["CodeyBox:Incus:PackageCacheSeeds:0:MaxSizeMB"] = "42.5",
                ["CodeyBox:Incus:ExecutableProvisions:0:HostSourcePath"] = "/srv/tools/agy",
                ["CodeyBox:Incus:ExecutableProvisions:0:VmDestPath"] = "/home/ubuntu/.local/bin/agy",
                ["CodeyBox:Incus:ExecutableProvisions:0:VmSymlinks:0"] = "/usr/local/bin/agy",
                ["CodeyBox:Incus:ExecutableProvisions:0:Label"] = "antigravity",
                ["CodeyBox:Incus:MaxExecutableProvisionBytes"] = "67108864",
                ["CodeyBox:Incus:MaxAggregateExecutableProvisionBytes"] = "134217728",
                ["CodeyBox:Incus:MaxPackageCacheSeedBytes"] = "268435456",
                ["CodeyBox:Incus:MaxAggregatePackageCacheSeedBytes"] = "536870912",
                ["CodeyBox:Incus:MaxPackageCacheSeedEntries"] = "456",
            })
            .Build();

        var options = config.GetSection("CodeyBox").Get<CodeyBoxOptions>();

        Assert.NotNull(options);
        var seed = Assert.Single(options.Incus.PackageCacheSeeds);
        Assert.Equal("/srv/cache/npm.tgz", seed.HostSourcePath);
        Assert.Equal("/var/cache/codeybox/npm", seed.VmDestPath);
        Assert.Equal(42.5, seed.MaxSizeMB);
        var provision = Assert.Single(options.Incus.ExecutableProvisions);
        Assert.Equal("/srv/tools/agy", provision.HostSourcePath);
        Assert.Equal("/home/ubuntu/.local/bin/agy", provision.VmDestPath);
        Assert.Equal(["/usr/local/bin/agy"], provision.VmSymlinks);
        Assert.Equal("antigravity", provision.Label);
        Assert.Equal(64L * 1024 * 1024, options.Incus.MaxExecutableProvisionBytes);
        Assert.Equal(128L * 1024 * 1024, options.Incus.MaxAggregateExecutableProvisionBytes);
        Assert.Equal(256L * 1024 * 1024, options.Incus.MaxPackageCacheSeedBytes);
        Assert.Equal(512L * 1024 * 1024, options.Incus.MaxAggregatePackageCacheSeedBytes);
        Assert.Equal(456, options.Incus.MaxPackageCacheSeedEntries);
    }

    [Fact]
    public void Build_DeepCopiesCollectionsAndIncludesCanonicalMountRoots()
    {
        var gitRoot = Path.Combine(Path.GetTempPath(), "codeybox-config", "..", "repos");
        var explicitRoot = Path.Combine(Path.GetTempPath(), "codeybox-extra");
        var networkProfiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["work"] = "cb-work",
        };
        var extraRuncmd = new List<string> { "apt-get update" };
        var allowedRoots = new List<string> { explicitRoot };
        var options = CreateOptions();
        options.GitRootDirectory = gitRoot;
        options.EnableSharedUpstreamMirror = true;
        options.SharedUpstreamMirrorDirectory = "_mirror";
        options.SandboxNetworkProfiles = networkProfiles;
        options.Incus = new IncusSandboxConfig
        {
            ExtraRuncmd = extraRuncmd,
            AllowedHostMountRoots = allowedRoots,
        };

        var mapped = IncusSandboxConfigMapper.Build(options);
        networkProfiles["work"] = "changed";
        extraRuncmd[0] = "changed";
        allowedRoots[0] = "/changed";

        Assert.Equal("cb-work", mapped.NetworkProfiles["work"]);
        Assert.Equal("apt-get update", Assert.Single(mapped.ExtraRuncmd));
        Assert.Contains(Path.TrimEndingDirectorySeparator(Path.GetFullPath(gitRoot)), mapped.AllowedHostMountRoots);
        Assert.Contains(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(gitRoot, "_mirror"))),
            mapped.AllowedHostMountRoots);
        Assert.Contains(Path.TrimEndingDirectorySeparator(Path.GetFullPath(explicitRoot)), mapped.AllowedHostMountRoots);
        Assert.DoesNotContain("/changed", mapped.AllowedHostMountRoots);
    }

    [Fact]
    public void Build_RejectsOversizedCollectionsBeforeCopyingThem()
    {
        var excessiveProfiles = Enumerable
            .Range(0, IncusSandboxOptions.MaximumNetworkProfiles + 1)
            .ToDictionary(index => $"profile-{index}", _ => "cb-net", StringComparer.Ordinal);
        var options = CreateOptions();
        options.SandboxNetworkProfiles = excessiveProfiles;

        Assert.Throws<InvalidOperationException>(() => IncusSandboxConfigMapper.Build(options));

        options.SandboxNetworkProfiles = new Dictionary<string, string>();
        options.Incus.ExtraRuncmd = Enumerable
            .Repeat("true", IncusSandboxOptions.MaximumExtraRuncmdCount + 1)
            .ToList();
        Assert.Throws<InvalidOperationException>(() => IncusSandboxConfigMapper.Build(options));
    }

    [Fact]
    public void SnapshotCollections_IgnoreDeceptiveCountAndEnforceObservedEntryBounds()
    {
        var oneCommandWithHugeReportedCount = new DeceptiveReadOnlyCollection<string>(
            int.MaxValue,
            ["true"]);
        var tooManyCommandsWithZeroReportedCount = new DeceptiveReadOnlyCollection<string>(
            reportedCount: 0,
            Enumerable.Repeat(
                "true",
                IncusSandboxOptions.MaximumExtraRuncmdCount + 1));
        var tooManyProfilesWithZeroReportedCount = new DeceptiveReadOnlyCollection<KeyValuePair<string, string>>(
            reportedCount: 0,
            Enumerable.Range(0, IncusSandboxOptions.MaximumNetworkProfiles + 1)
                .Select(index => KeyValuePair.Create($"profile-{index}", "cb-net")));

        Assert.Equal(["true"], IncusSandboxConfigMapper.SnapshotExtraRuncmd(oneCommandWithHugeReportedCount));
        Assert.Throws<InvalidOperationException>(() =>
            IncusSandboxConfigMapper.SnapshotExtraRuncmd(tooManyCommandsWithZeroReportedCount));
        Assert.Throws<InvalidOperationException>(() =>
            IncusSandboxConfigMapper.SnapshotNetworkProfiles(tooManyProfilesWithZeroReportedCount));
        Assert.Equal(IncusSandboxOptions.MaximumExtraRuncmdCount + 1, tooManyCommandsWithZeroReportedCount.EnumeratedCount);
        Assert.Equal(IncusSandboxOptions.MaximumNetworkProfiles + 1, tooManyProfilesWithZeroReportedCount.EnumeratedCount);
    }

    [Fact]
    public void Build_RejectsOversizedConfigTextBeforeUtf8AndPathNormalization()
    {
        var options = CreateOptions();
        options.StateDatabasePath = new string('s', 4097);

        var statePathFailure = Assert.Throws<InvalidOperationException>(() =>
            IncusSandboxConfigMapper.Build(options));

        Assert.Contains("StateDatabasePath", statePathFailure.Message, StringComparison.Ordinal);

        options.StateDatabasePath = "/srv/codeybox/state.db";
        options.Incus.ExtraRuncmd =
        [
            new string('x', IncusSandboxOptions.MaximumExtraRuncmdCommandUtf8Bytes + 1),
        ];

        var commandFailure = Assert.Throws<InvalidOperationException>(() =>
            IncusSandboxConfigMapper.Build(options));

        Assert.Contains("ExtraRuncmd command", commandFailure.Message, StringComparison.Ordinal);

        options.Incus.ExtraRuncmd = [];
        options.Incus.AllowedHostMountRoots = [new string('r', 4097)];

        var rootFailure = Assert.Throws<InvalidOperationException>(() =>
            IncusSandboxConfigMapper.Build(options));

        Assert.Contains("host mount root", rootFailure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_MapsProviderBoundsAndUsesPersistentDefaultStagingDirectory()
    {
        var options = CreateOptions();
        options.StateDatabasePath = "/srv/codeybox/state/state.db";
        options.Incus = new IncusSandboxConfig
        {
            ExecTimeout = TimeSpan.FromMinutes(47),
            ImageProvisioningTimeout = TimeSpan.FromMinutes(19),
            ResourceMetricsCaptureTimeout = TimeSpan.FromSeconds(17),
            ResourceMetricsSampleInterval = TimeSpan.FromSeconds(23),
            CliProcessCleanupTimeout = TimeSpan.FromSeconds(29),
            CliProcessGroupExitPollInterval = TimeSpan.FromMilliseconds(31),
            ExecPidPollAttempts = 7,
            ExecControlFileCleanupAttempts = 11,
            ExecCompletionProbeAttempts = 13,
            InterruptedExecRecoveryRetryAttempts = 4,
            InterruptedExecRecoveryRetryDelay = TimeSpan.FromSeconds(9),
            MaxTmpfsDeviceBytes = 1234,
            MaxAggregateTmpfsBytes = 5678,
            MaxSnapshotEntries = 4321,
            MaxReadinessProbeEntries = 321,
        };

        var mapped = IncusSandboxConfigMapper.Build(options);

        Assert.Equal(TimeSpan.FromMinutes(47), mapped.ExecTimeout);
        Assert.Equal(TimeSpan.FromMinutes(19), mapped.ImageProvisioningTimeout);
        Assert.Equal(TimeSpan.FromSeconds(17), mapped.ResourceMetricsCaptureTimeout);
        Assert.Equal(TimeSpan.FromSeconds(23), mapped.ResourceMetricsSampleInterval);
        Assert.Equal(TimeSpan.FromSeconds(29), mapped.CliProcessCleanupTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(31), mapped.CliProcessGroupExitPollInterval);
        Assert.Equal(7, mapped.ExecPidPollAttempts);
        Assert.Equal(11, mapped.ExecControlFileCleanupAttempts);
        Assert.Equal(13, mapped.ExecCompletionProbeAttempts);
        Assert.Equal(4, mapped.InterruptedExecRecoveryRetryAttempts);
        Assert.Equal(TimeSpan.FromSeconds(9), mapped.InterruptedExecRecoveryRetryDelay);
        Assert.Equal(1234, mapped.MaxTmpfsDeviceBytes);
        Assert.Equal(5678, mapped.MaxAggregateTmpfsBytes);
        Assert.Equal(4321, mapped.MaxSnapshotEntries);
        Assert.Equal(321, mapped.MaxReadinessProbeEntries);
        Assert.Equal("/srv/codeybox/state/incus-staging", mapped.StagingDirectory);
    }

    [Fact]
    public void Build_IncludesAbsoluteSharedMirrorOutsideGitRoot()
    {
        var options = CreateOptions();
        options.EnableSharedUpstreamMirror = true;
        options.SharedUpstreamMirrorDirectory = "/srv/mirrors/../codeybox-mirrors";

        var mapped = IncusSandboxConfigMapper.Build(options);

        Assert.Contains("/srv/codeybox-mirrors", mapped.AllowedHostMountRoots);
    }

    [Fact]
    public void Build_DisablesIncusDiskGuardWhenSharedPolicyIsDisabled()
    {
        var options = CreateOptions();
        options.DiskGuard = new DiskGuardOptions
        {
            Enabled = false,
            MinFreeBytes = 999,
            RecheckIn = "00:00:42",
        };

        var mapped = IncusSandboxConfigMapper.Build(options);

        Assert.Null(mapped.DiskGuard);
    }

    [Fact]
    public void Build_MapsIncusDiskGuardFromSharedPolicy()
    {
        var options = CreateOptions();
        options.DiskGuard = new DiskGuardOptions
        {
            Enabled = true,
            MinFreeBytes = 42L * 1024 * 1024,
            RecheckIn = "00:03:00",
            AdditionalPaths = ["/srv/codeybox", "/srv/extra/"],
        };

        var mapped = IncusSandboxConfigMapper.Build(options, NullLogger.Instance);

        var diskGuard = mapped.DiskGuard
            ?? throw new InvalidOperationException("The mapped Incus disk guard is unexpectedly disabled.");
        Assert.Equal(42L * 1024 * 1024, diskGuard.MinFreeBytes);
        Assert.Equal(TimeSpan.FromMinutes(3), diskGuard.RecheckIn);
        Assert.Equal(
            ["/srv/codeybox", "/srv/extra", "/srv/codeybox/incus-staging"],
            diskGuard.HostPaths);
    }

    [Fact]
    public void Build_AllowsConfiguredHostPathLimitsPlusAutomaticPaths()
    {
        var options = CreateOptions();
        options.EnableSharedUpstreamMirror = true;
        options.SharedUpstreamMirrorDirectory = "_mirror";
        options.Incus.AllowedHostMountRoots = Enumerable
            .Range(0, IncusSandboxOptions.MaximumConfiguredHostPathEntries)
            .Select(index => $"/mnt/codeybox-root-{index}")
            .ToList();
        options.DiskGuard = new DiskGuardOptions
        {
            Enabled = true,
            AdditionalPaths = Enumerable
                .Range(0, DiskGuardOptions.MaximumAdditionalPaths)
                .Select(index => $"/mnt/codeybox-disk-{index}")
                .ToList(),
        };

        var mapped = IncusSandboxConfigMapper.Build(options, NullLogger.Instance);
        var snapshot = IncusInputSnapshot.CaptureOptions(mapped);
        var diskGuard = Assert.IsType<IncusDiskGuardOptions>(snapshot.DiskGuard);

        Assert.Equal(
            IncusSandboxOptions.MaximumEffectiveHostPathEntries,
            snapshot.AllowedHostMountRoots.Count);
        Assert.Equal(
            IncusSandboxOptions.MaximumEffectiveHostPathEntries,
            diskGuard.HostPaths.Count);
        Assert.Empty(IncusSandboxOptions.Validate(snapshot));

        Assert.Throws<ArgumentException>(() => IncusInputSnapshot.CaptureOptions(snapshot with
        {
            AllowedHostMountRoots = [.. snapshot.AllowedHostMountRoots, "/mnt/one-too-many"],
        }));
        Assert.Throws<ArgumentException>(() => IncusInputSnapshot.CaptureOptions(snapshot with
        {
            DiskGuard = diskGuard with
            {
                HostPaths = [.. diskGuard.HostPaths, "/mnt/one-too-many"],
            },
        }));
    }

    [Fact]
    public void OptionsValidator_ValidatesIncusWhenSelected()
    {
        var options = CreateOptions();
        options.SandboxProvider = "incus";
        options.Incus.BinaryPath = "";

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("CodeyBox:Incus:BinaryPath", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void OptionsValidator_ValidatesIncusBaselineProvisioningLimits()
    {
        var options = CreateOptions();
        options.SandboxProvider = "incus";
        options.Incus.MaxExecutableProvisionBytes = 0;
        options.Incus.MaxPackageCacheSeedEntries = 0;

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("CodeyBox:Incus:MaxExecutableProvisionBytes", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("MaxPackageCacheSeedEntries", result.FailureMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("process")]
    [InlineData("bubblewrap")]
    [InlineData("sprites")]
    [InlineData("multipass")]
    public void OptionsValidator_IgnoresUnusedIncusConfigForOtherSelectors(string provider)
    {
        var options = CreateOptions();
        options.SandboxProvider = provider;
        options.Incus.BinaryPath = "";

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.DoesNotContain("CodeyBox:Incus:", result.FailureMessage ?? "", StringComparison.Ordinal);
    }

    private static CodeyBoxOptions CreateOptions() => new()
    {
        GitRootDirectory = "/srv/codeybox/repos",
        StateDatabasePath = "/srv/codeybox/state.db",
        DiskGuard = new DiskGuardOptions { Enabled = false },
        SandboxNetworkProfiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        Incus = new IncusSandboxConfig(),
    };

    private sealed class DeceptiveReadOnlyCollection<T>(
        int reportedCount,
        IEnumerable<T> values) : IReadOnlyCollection<T>
    {
        public int Count => reportedCount;
        internal int EnumeratedCount { get; private set; }

        public IEnumerator<T> GetEnumerator()
        {
            foreach (var value in values)
            {
                EnumeratedCount++;
                yield return value;
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
