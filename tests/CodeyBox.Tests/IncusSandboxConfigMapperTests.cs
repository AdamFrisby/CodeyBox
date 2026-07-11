using CodeyBox.Api;
using CodeyBox.Sandbox.Incus;
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
        options.Incus = new IncusSandboxConfig
        {
            ExtraRuncmd = [],
            ExtraCloudInit = null,
            UseBaselineImages = true,
        };

        var mapped = IncusSandboxConfigMapper.Build(options);

        Assert.Empty(mapped.ExtraRuncmd);
        Assert.Null(mapped.ExtraCloudInit);
        Assert.True(mapped.UseBaselineImages);
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
