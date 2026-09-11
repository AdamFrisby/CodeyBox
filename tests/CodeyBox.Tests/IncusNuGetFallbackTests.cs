using System.Formats.Tar;
using CodeyBox.Core;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Incus;

namespace CodeyBox.Tests;

public sealed class IncusNuGetFallbackTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("codeybox-incus-fallback-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void PlanGuestPaths_EmptyUnlessSharingWithNuGetSeeds()
    {
        var options = BaseOptions();
        Assert.Equal(
            [NuGetFallbackCache.DefaultGuestFallbackPath],
            IncusNuGetFallback.PlanGuestPaths(options));
        Assert.Empty(IncusNuGetFallback.PlanGuestPaths(options with { ShareNuGetPackageSeedsAsFallback = false }));
        Assert.Empty(IncusNuGetFallback.PlanGuestPaths(options with
        {
            PackageCacheSeeds =
            [
                new BaselinePackageCacheSeed { HostSourcePath = "/host/other", VmDestPath = "/opt/tool-cache" },
            ],
        }));
        Assert.True(IncusNuGetFallback.IsSharingEnabled(options));
        Assert.False(IncusNuGetFallback.IsSharingEnabled(options with { ShareNuGetPackageSeedsAsFallback = false }));
    }

    [Fact]
    public void PlanGuestPaths_SuffixesAdditionalSeeds()
    {
        var options = BaseOptions() with
        {
            PackageCacheSeeds =
            [
                new BaselinePackageCacheSeed { HostSourcePath = "/host/a", VmDestPath = "/home/ubuntu/.nuget/packages" },
                new BaselinePackageCacheSeed { HostSourcePath = "/host/b", VmDestPath = "/home/ubuntu/.nuget/packages" },
            ],
        };
        Assert.Equal(
            [
                NuGetFallbackCache.DefaultGuestFallbackPath,
                NuGetFallbackCache.DefaultGuestFallbackPath + "-2",
            ],
            IncusNuGetFallback.PlanGuestPaths(options));
    }

    [Fact]
    public void PlanBakeSteps_MaterializesFallbackInsideImage()
    {
        var steps = IncusNuGetFallback.PlanBakeSteps(BaseOptions());
        var step = Assert.Single(steps);
        Assert.Equal(NuGetSeedProvisioning.CopyToFallback, step.Mode);
        Assert.Equal(NuGetFallbackCache.DefaultGuestFallbackPath, step.EffectiveVmDestPath);
        var legacy = IncusNuGetFallback.PlanBakeSteps(BaseOptions() with { ShareNuGetPackageSeedsAsFallback = false });
        Assert.All(legacy, s => Assert.Equal(NuGetSeedProvisioning.CopyToVmDest, s.Mode));
        Assert.All(legacy, s => Assert.Null(s.EffectiveVmDestPath));
    }

    [Fact]
    public void PlanFullLaunch_SharesNuGetSeedByReference()
    {
        var seedDir = CreateSeedDir("seed");
        var stagingRoot = CreateStagingRoot();
        var options = BaseOptions() with
        {
            AllowedHostMountRoots = [_root],
            PackageCacheSeeds =
            [
                new BaselinePackageCacheSeed { HostSourcePath = seedDir, VmDestPath = "/home/ubuntu/.nuget/packages" },
            ],
        };
        var plan = IncusNuGetFallback.PlanFullLaunch(options, stagingRoot, _ => null);
        Assert.Empty(plan.Warnings);
        var step = Assert.Single(plan.Steps);
        Assert.Equal(NuGetSeedProvisioning.MountedShare, step.Mode);
        var mount = Assert.Single(plan.FallbackMounts);
        Assert.True(mount.ReadOnly);
        Assert.False(mount.SnapshotForIsolation);
        Assert.Equal(seedDir, mount.HostPath);
        Assert.Equal(NuGetFallbackCache.DefaultGuestFallbackPath, mount.SandboxPath);
        Assert.Equal([NuGetFallbackCache.DefaultGuestFallbackPath], plan.FallbackGuestPaths);

        // The shared mount must reach the guest as a kernel read-only device.
        var deviceArgs = IncusCommandBuilder.BuildDeviceAdd(
            options, "codeybox-test", "m007", mount.HostPath!, mount.SandboxPath, readOnly: true);
        Assert.Contains("readonly=true", deviceArgs);
        Assert.Contains("io.bus=virtiofs", deviceArgs);
    }

    [Fact]
    public void PlanFullLaunch_CopiesFileSeedsAndWarnsOnMissingOrDisallowed()
    {
        var seedDir = CreateSeedDir("seed");
        var packageFile = Path.Combine(_root, "package.tar");
        File.WriteAllText(packageFile, "payload");
        var stagingRoot = CreateStagingRoot();
        var options = BaseOptions() with
        {
            AllowedHostMountRoots = [Path.Combine(_root, "elsewhere")],
            PackageCacheSeeds =
            [
                new BaselinePackageCacheSeed { HostSourcePath = packageFile, VmDestPath = "/home/ubuntu/.nuget/packages" },
                new BaselinePackageCacheSeed { HostSourcePath = Path.Combine(_root, "absent"), VmDestPath = "/home/ubuntu/.nuget/packages" },
                new BaselinePackageCacheSeed { HostSourcePath = seedDir, VmDestPath = "/home/ubuntu/.nuget/packages" },
                new BaselinePackageCacheSeed { HostSourcePath = "/host/other", VmDestPath = "/opt/tool-cache" },
            ],
        };
        var plan = IncusNuGetFallback.PlanFullLaunch(options, stagingRoot, _ => null);
        Assert.Equal(NuGetSeedProvisioning.CopyToVmDest, plan.Steps[0].Mode);
        Assert.Equal(NuGetSeedProvisioning.SkippedMissing, plan.Steps[1].Mode);
        Assert.Equal(NuGetSeedProvisioning.CopyToVmDest, plan.Steps[2].Mode);
        Assert.Equal(NuGetSeedProvisioning.CopyToVmDest, plan.Steps[3].Mode);
        Assert.Equal(2, plan.Warnings.Count);
        // Only the successfully shared seed keeps a fallback path; the copy
        // and skipped seeds must not advertise a folder that will not exist.
        Assert.Empty(plan.FallbackMounts);
        Assert.Empty(plan.FallbackGuestPaths);
    }

    [Fact]
    public void PlanFullLaunch_DisabledKeepsLegacyCopyForEverySeed()
    {
        var seedDir = CreateSeedDir("seed");
        var options = BaseOptions() with
        {
            ShareNuGetPackageSeedsAsFallback = false,
            AllowedHostMountRoots = [_root],
            PackageCacheSeeds =
            [
                new BaselinePackageCacheSeed { HostSourcePath = seedDir, VmDestPath = "/home/ubuntu/.nuget/packages" },
            ],
        };
        var plan = IncusNuGetFallback.PlanFullLaunch(options, CreateStagingRoot(), _ => null);
        Assert.Empty(plan.Warnings);
        Assert.Empty(plan.FallbackMounts);
        Assert.Empty(plan.FallbackGuestPaths);
        Assert.Equal(NuGetSeedProvisioning.CopyToVmDest, Assert.Single(plan.Steps).Mode);
    }

    [Fact]
    public void ApplyToSpec_MergesMountsAndEnvironmentWithoutMutatingInput()
    {
        var seedDir = CreateSeedDir("seed");
        var spec = new SandboxSpec
        {
            ImageReference = "images:ubuntu/24.04/cloud",
            Mounts = [new SandboxMount { SandboxPath = "/repo", HostPath = seedDir, ReadOnly = true }],
            Environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [NuGetFallbackCache.FallbackPackagesEnvironmentVariable] = "/operator/cache",
            },
            Network = new SandboxNetworkPolicy { ProfileName = "internet-only" },
        };
        var mount = NuGetFallbackCache.BuildFallbackMount(seedDir, NuGetFallbackCache.DefaultGuestFallbackPath);
        var augmented = IncusNuGetFallback.ApplyToSpec(
            spec, [mount], [NuGetFallbackCache.DefaultGuestFallbackPath]);
        Assert.Equal(2, augmented.Mounts.Count);
        Assert.Equal(
            "/operator/cache;" + NuGetFallbackCache.DefaultGuestFallbackPath,
            augmented.Environment[NuGetFallbackCache.FallbackPackagesEnvironmentVariable]);
        Assert.Single(spec.Mounts);
        Assert.Equal("/operator/cache", spec.Environment[NuGetFallbackCache.FallbackPackagesEnvironmentVariable]);
        Assert.Same(spec, IncusNuGetFallback.ApplyToSpec(spec, [], []));
    }

    [Fact]
    public void ComputeConfigHash_IsSensitiveToFallbackKnobs()
    {
        var options = BaseOptions();
        var baseline = IncusBaselineNaming.ComputeConfigHash(options, "internet-only", SandboxProfileFlavor.Headless);
        var renamed = IncusBaselineNaming.ComputeConfigHash(
            options with { NuGetFallbackGuestPath = "/opt/other-fallback" },
            "internet-only",
            SandboxProfileFlavor.Headless);
        var disabled = IncusBaselineNaming.ComputeConfigHash(
            options with { ShareNuGetPackageSeedsAsFallback = false },
            "internet-only",
            SandboxProfileFlavor.Headless);
        Assert.Equal(64, baseline.Length);
        Assert.NotEqual(baseline, renamed);
        Assert.NotEqual(baseline, disabled);
    }

    [Fact]
    public void OptionsValidate_RejectsBadFallbackPathsAndCacheCollisions()
    {
        var relative = IncusSandboxOptions.Validate(BaseOptions() with { NuGetFallbackGuestPath = "relative" });
        Assert.Contains(relative, e => e.Contains("NuGetFallbackGuestPath", StringComparison.Ordinal));
        var underHome = IncusSandboxOptions.Validate(BaseOptions() with
        {
            NuGetFallbackGuestPath = "/home/ubuntu/.nuget/fallback",
        });
        Assert.Contains(underHome, e => e.Contains("NuGetFallbackGuestPath", StringComparison.Ordinal));
        var colliding = IncusSandboxOptions.Validate(BaseOptions() with
        {
            NuGetFallbackGuestPath = "/opt/codeybox/nuget-fallback-packages",
            PackageCacheSeeds =
            [
                new BaselinePackageCacheSeed { HostSourcePath = "/host/x", VmDestPath = "/opt/codeybox/nuget-fallback-packages/extra" },
            ],
        });
        Assert.Contains(colliding, e => e.Contains("fallback folder", StringComparison.Ordinal));
        Assert.Empty(IncusSandboxOptions.Validate(BaseOptions()));
    }

    [Fact]
    public void Plans_CapFallbackPositionsBeyondMaximum()
    {
        // Two seeds beyond the configured maximum: the planners degrade
        // gracefully to the copy path (or skip it) instead of throwing, while
        // options validation still rejects the oversized seed list.
        var options = BaseOptions() with
        {
            AllowedHostMountRoots = [_root],
            PackageCacheSeeds = Enumerable.Range(0, NuGetFallbackCache.MaximumFallbackPaths + 2)
                .Select(i => new BaselinePackageCacheSeed
                {
                    HostSourcePath = Path.Combine(_root, $"seed-{i}"),
                    VmDestPath = "/home/ubuntu/.nuget/packages",
                })
                .ToList(),
        };
        foreach (var i in Enumerable.Range(0, NuGetFallbackCache.MaximumFallbackPaths + 2))
            Directory.CreateDirectory(Path.Combine(_root, $"seed-{i}"));
        Assert.Equal(
            NuGetFallbackCache.MaximumFallbackPaths,
            IncusNuGetFallback.PlanGuestPaths(options).Count);
        var bake = IncusNuGetFallback.PlanBakeSteps(options);
        Assert.Equal(
            NuGetFallbackCache.MaximumFallbackPaths,
            bake.Count(s => s.Mode == NuGetSeedProvisioning.CopyToFallback));
        Assert.Equal(2, bake.Count(s => s.Mode == NuGetSeedProvisioning.CopyToVmDest));
        var launch = IncusNuGetFallback.PlanFullLaunch(options, CreateStagingRoot(), _ => null);
        Assert.Equal(
            NuGetFallbackCache.MaximumFallbackPaths,
            launch.FallbackMounts.Count);
        Assert.NotEmpty(IncusSandboxOptions.Validate(options));
    }

    [Fact]
    public void ComputePackageSeedFingerprint_TracksContent()
    {
        var seedDir = CreateSeedDir("seed");
        File.WriteAllText(Path.Combine(seedDir, "a.txt"), "v1");
        var options = BaseOptions() with
        {
            PackageCacheSeeds =
            [
                new BaselinePackageCacheSeed { HostSourcePath = seedDir, VmDestPath = "/home/ubuntu/.nuget/packages" },
            ],
        };
        var first = IncusBaselineProvisioning.ComputePackageSeedFingerprint(
            options, options.PackageCacheSeeds[0], _ => null, CancellationToken.None);
        Assert.Equal(64, first.Length);
        File.WriteAllText(Path.Combine(seedDir, "a.txt"), "v2");
        Assert.NotEqual(
            first,
            IncusBaselineProvisioning.ComputePackageSeedFingerprint(
                options, options.PackageCacheSeeds[0], _ => null, CancellationToken.None));
        Assert.NotEqual(
            first,
            IncusBaselineProvisioning.ComputePackageSeedFingerprint(
                options with { MaxPackageCacheSeedEntries = 7 }, options.PackageCacheSeeds[0], _ => null, CancellationToken.None));
    }

    [Fact]
    public async Task SharedArchiveCache_BuildsOncePerHostAcrossSandboxes()
    {
        var stagingRoot = CreateStagingRoot();
        var seedDir = CreateSeedDir("seed");
        File.WriteAllText(Path.Combine(seedDir, "a.txt"), "payload");
        var options = BaseOptions();
        var seed = new BaselinePackageCacheSeed { HostSourcePath = seedDir, VmDestPath = "/home/ubuntu/.nuget/packages" };
        var cache = new IncusSharedPackageArchiveCache();
        var builds = 0;
        async Task<string> ObtainAsync()
        {
            return await cache.ObtainAsync(
                options,
                stagingRoot,
                seed,
                index: 0,
                _ => null,
                _ => Assert.Fail("no eviction failures expected"),
                (request, ct) =>
                {
                    Interlocked.Increment(ref builds);
                    Assert.Equal(seedDir, request.Seed.HostSourcePath);
                    return DefaultBuild(request, ct);
                });
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => ObtainAsync()));
        Assert.Equal(1, builds);
        Assert.Single(results.Distinct(StringComparer.Ordinal));
        Assert.True(File.Exists(results[0]));
        // The published archive really carries the seed: extract and compare.
        var extractDir = Path.Combine(_root, "extracted");
        Directory.CreateDirectory(extractDir);
        await using (var archive = File.OpenRead(results[0]))
        {
            await System.Formats.Tar.TarFile.ExtractToDirectoryAsync(archive, extractDir, overwriteFiles: true);
        }
        Assert.Equal("payload", File.ReadAllText(Path.Combine(extractDir, "a.txt")));
        // A second host (staging root) builds independently: per-host, not global.
        var otherRoot = CreateStagingRoot();
        _ = await cache.ObtainAsync(
            options,
            otherRoot,
            seed,
            0,
            _ => null,
            _ => { },
            (request, ct) =>
            {
                Interlocked.Increment(ref builds);
                return DefaultBuild(request, ct);
            });
        Assert.Equal(2, builds);
        // Changed content rebuilds exactly once and evicts the stale archive.
        File.WriteAllText(Path.Combine(seedDir, "a.txt"), "changed");
        var rebuilt = await cache.ObtainAsync(
            options,
            stagingRoot,
            seed,
            0,
            _ => null,
            _ => { },
            (request, ct) =>
            {
                Interlocked.Increment(ref builds);
                return DefaultBuild(request, ct);
            });
        Assert.Equal(3, builds);
        Assert.NotEqual(results[0], rebuilt);
        Assert.False(Directory.Exists(Path.GetDirectoryName(results[0])));
    }

    private static Task DefaultBuild(
        IncusSharedPackageArchiveCache.ArchiveBuildRequest request,
        CancellationToken ct)
    {
        var localAggregateBytes = 0L;
        IncusBaselineProvisioning.CreatePackageArchive(
            request.Seed.HostSourcePath,
            request.ArchivePath,
            IncusBaselineProvisioning.ResolvePackageSeedByteLimit(request.Options, request.Seed),
            request.Options.MaxAggregatePackageCacheSeedBytes,
            request.Options.MaxPackageCacheSeedEntries,
            ref localAggregateBytes,
            request.EnvironmentVariableReader,
            ct);
        return Task.CompletedTask;
    }

    private IncusSandboxOptions BaseOptions() => new()
    {
        ProjectName = "codeybox-tests",
        StoragePoolName = "codeybox-zfs",
        DefaultImage = "images:ubuntu/24.04/cloud",
        UseBaselineImages = true,
        DiskGuard = null,
        BootLaunchDelay = TimeSpan.Zero,
        NetworkProfiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["internet-only"] = "cb-net",
        },
        GuestHome = "/home/ubuntu",
        PackageCacheSeeds =
        [
            new BaselinePackageCacheSeed { HostSourcePath = "/host/seed", VmDestPath = "/home/ubuntu/.nuget/packages" },
        ],
    };

    private string CreateSeedDir(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(path);
        return path;
    }

    private string CreateStagingRoot()
    {
        var path = Path.Combine(_root, "staging-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }
}
