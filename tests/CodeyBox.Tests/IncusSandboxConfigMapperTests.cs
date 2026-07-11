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
}
