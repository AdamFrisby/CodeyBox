using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

public sealed class NuGetFallbackCacheTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("codeybox-nuget-fallback-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void GuestFallbackPathForSeed_ReturnsBaseThenSuffixedPaths()
    {
        Assert.Equal("/opt/codeybox/nuget-fallback-packages", NuGetFallbackCache.GuestFallbackPathForSeed("/opt/codeybox/nuget-fallback-packages", 0));
        Assert.Equal("/opt/codeybox/nuget-fallback-packages-2", NuGetFallbackCache.GuestFallbackPathForSeed("/opt/codeybox/nuget-fallback-packages", 1));
        Assert.Equal("/opt/codeybox/nuget-fallback-packages-9", NuGetFallbackCache.GuestFallbackPathForSeed("/opt/codeybox/nuget-fallback-packages", 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => NuGetFallbackCache.GuestFallbackPathForSeed("/opt/fb", -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => NuGetFallbackCache.GuestFallbackPathForSeed("/opt/fb", NuGetFallbackCache.MaximumFallbackPaths));
        Assert.Throws<ArgumentException>(() => NuGetFallbackCache.GuestFallbackPathForSeed("relative", 0));
    }

    [Fact]
    public void BuildFallbackMount_IsReadOnlySharedByReference()
    {
        var mount = NuGetFallbackCache.BuildFallbackMount("/host/seed", "/opt/codeybox/nuget-fallback-packages");
        Assert.Equal("/host/seed", mount.HostPath);
        Assert.Equal("/opt/codeybox/nuget-fallback-packages", mount.SandboxPath);
        Assert.True(mount.ReadOnly);
        Assert.False(mount.SnapshotForIsolation);
        Assert.False(mount.Tmpfs);
        Assert.Throws<ArgumentException>(() => NuGetFallbackCache.BuildFallbackMount("relative", "/opt/fb"));
        Assert.Throws<ArgumentException>(() => NuGetFallbackCache.BuildFallbackMount("/host/seed", "relative"));
    }

    [Fact]
    public void MergeFallbackEnvironment_PreservesExistingFirstAndAppendsNew()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = "/usr/bin",
            [NuGetFallbackCache.FallbackPackagesEnvironmentVariable] = "/existing/cache",
        };
        var merged = NuGetFallbackCache.MergeFallbackEnvironment(
            environment,
            ["/opt/codeybox/nuget-fallback-packages", "/existing/cache"]);
        Assert.Equal("/usr/bin", merged["PATH"]);
        Assert.Equal(
            "/existing/cache;/opt/codeybox/nuget-fallback-packages",
            merged[NuGetFallbackCache.FallbackPackagesEnvironmentVariable]);
        // Input is not mutated.
        Assert.False(environment.ContainsKey(NuGetFallbackCache.FallbackPackagesEnvironmentVariable)
            && environment[NuGetFallbackCache.FallbackPackagesEnvironmentVariable].Contains("/opt/codeybox", StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => NuGetFallbackCache.MergeFallbackEnvironment(environment, ["relative"]));
        Assert.Throws<ArgumentOutOfRangeException>(() => NuGetFallbackCache.MergeFallbackEnvironment(
            environment,
            Enumerable.Range(0, NuGetFallbackCache.MaximumFallbackPaths + 1).Select(i => $"/fb-{i}").ToArray()));
    }

    [Fact]
    public void MergeFallbackEnvironment_EmptyExistingProducesSingleEntry()
    {
        var merged = NuGetFallbackCache.MergeFallbackEnvironment(
            new Dictionary<string, string>(StringComparer.Ordinal),
            ["/opt/codeybox/nuget-fallback-packages"]);
        Assert.Equal(
            "/opt/codeybox/nuget-fallback-packages",
            merged[NuGetFallbackCache.FallbackPackagesEnvironmentVariable]);
        Assert.Equal(
            ["/opt/codeybox/nuget-fallback-packages"],
            NuGetFallbackCache.SplitFallbackEnvironmentValue(merged[NuGetFallbackCache.FallbackPackagesEnvironmentVariable]));
        Assert.Empty(NuGetFallbackCache.SplitFallbackEnvironmentValue(null));
        Assert.Empty(NuGetFallbackCache.SplitFallbackEnvironmentValue("  ; "));
    }

    [Fact]
    public void PartitionNuGetSeeds_SplitsByGuestNuGetHome()
    {
        var seeds = new List<BaselinePackageCacheSeed>
        {
            new() { HostSourcePath = "/host/nuget-seed", VmDestPath = "/home/ubuntu/.nuget/packages" },
            new() { HostSourcePath = "/host/other", VmDestPath = "/opt/tool-cache" },
            new() { HostSourcePath = "/host/nuget-seed-2", VmDestPath = "/home/ubuntu/.nuget/packages-2" },
        };
        var partition = NuGetFallbackCache.PartitionNuGetSeeds(seeds, "/home/ubuntu");
        Assert.Equal([0, 2], partition.NuGetSeeds.Select(s => s.Index).ToArray());
        Assert.Equal([1], partition.OtherSeeds.Select(s => s.Index).ToArray());
        Assert.Same(seeds[0], partition.NuGetSeeds[0].Seed);
    }

    [Fact]
    public void NormalizeIdentity_FoldsIdCase()
    {
        Assert.Equal("newtonsoft.json/13.0.3", NuGetFallbackCoverage.NormalizeIdentity("Newtonsoft.Json", "13.0.3"));
        Assert.Equal("a/1.0.0", NuGetFallbackCoverage.NormalizeIdentity("A", "1.0.0"));
        Assert.Throws<ArgumentException>(() => NuGetFallbackCoverage.NormalizeIdentity("a/b", "1.0.0"));
    }

    [Fact]
    public void EnumeratePackageIdentities_ListsTwoLevelLayoutOnly()
    {
        var cache = Path.Combine(_root, "cache");
        Directory.CreateDirectory(Path.Combine(cache, "Newtonsoft.Json", "13.0.3"));
        Directory.CreateDirectory(Path.Combine(cache, "Xunit", "2.9.0"));
        File.WriteAllText(Path.Combine(cache, "Newtonsoft.Json", "13.0.3", "package.nupkg"), "x");
        Directory.CreateDirectory(Path.Combine(cache, "Newtonsoft.Json", "13.0.3", "lib", "netstandard2.0"));
        File.WriteAllText(Path.Combine(cache, "stray.txt"), "x");
        Directory.CreateDirectory(Path.Combine(cache, "Lonely"));
        var identities = NuGetFallbackCoverage.EnumeratePackageIdentities(cache);
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal) { "newtonsoft.json/13.0.3", "xunit/2.9.0" },
            identities);
        Assert.Empty(NuGetFallbackCoverage.EnumeratePackageIdentities(Path.Combine(_root, "missing")));
        Assert.Throws<IOException>(() => NuGetFallbackCoverage.EnumeratePackageIdentities(cache, maxEntries: 1));
    }

    [Fact]
    public void TryParseAssetsLibraries_KeepsPackagesAndSkipsProjects()
    {
        const string json = """
            {
              "libraries": {
                "Newtonsoft.Json/13.0.3": { "type": "package" },
                "MyApp/1.0.0": { "type": "project" },
                "NotAnIdentity": { "type": "package" }
              }
            }
            """;
        Assert.True(NuGetFallbackCoverage.TryParseAssetsLibraries(json, out var libraries));
        Assert.Equal(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["newtonsoft.json/13.0.3"] = "package",
                ["myapp/1.0.0"] = "project",
            },
            libraries);
        Assert.False(NuGetFallbackCoverage.TryParseAssetsLibraries("not json", out _));
        Assert.False(NuGetFallbackCoverage.TryParseAssetsLibraries("{}", out _));
        Assert.False(NuGetFallbackCoverage.TryParseAssetsLibraries("{\"libraries\":[]}", out _));
        Assert.False(NuGetFallbackCoverage.TryParseAssetsLibraries(json, out _, maxBytes: 10));
    }

    [Fact]
    public void Classify_CountsSharedVersusFetched()
    {
        var libraries = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["a/1.0.0"] = "package",
            ["b/2.0.0"] = "package",
            ["c/3.0.0"] = "project",
        };
        var report = NuGetFallbackCoverage.Classify(
            libraries,
            new HashSet<string>(StringComparer.Ordinal) { "a/1.0.0" },
            fallbackPackagesAvailable: 5);
        Assert.Equal(1, report.SharedPackages);
        Assert.Equal(1, report.FetchedPackages);
        Assert.Equal(2, report.TotalPackages);
        Assert.Equal(5, report.FallbackPackagesAvailable);
        Assert.Contains("1 shared", report.Format(), StringComparison.Ordinal);
        Assert.Contains("1 fetched", report.Format(), StringComparison.Ordinal);
    }

    [Fact]
    public void ComputeFromDirectories_EndToEndOverRealLayout()
    {
        var fallback = Path.Combine(_root, "fallback");
        Directory.CreateDirectory(Path.Combine(fallback, "A", "1.0.0"));
        Directory.CreateDirectory(Path.Combine(fallback, "B", "2.0.0"));
        const string assets = """{"libraries":{"A/1.0.0":{"type":"package"},"C/3.0.0":{"type":"package"}}}""";
        var report = NuGetFallbackCoverage.ComputeFromDirectories(fallback, assets);
        Assert.Equal(1, report.SharedPackages);
        Assert.Equal(1, report.FetchedPackages);
        Assert.Equal(2, report.TotalPackages);
        Assert.Equal(2, report.FallbackPackagesAvailable);
        var badAssets = NuGetFallbackCoverage.ComputeFromDirectories(fallback, "nope");
        Assert.Equal(0, badAssets.TotalPackages);
        Assert.Equal(2, badAssets.FallbackPackagesAvailable);
    }

    [Fact]
    public void ComputeDirectoryFingerprint_IsStableAndContentSensitive()
    {
        var dir = Path.Combine(_root, "seed");
        var packageDir = Path.Combine(dir, "A", "1.0.0");
        Directory.CreateDirectory(packageDir);
        var file = Path.Combine(packageDir, "a.nupkg");
        File.WriteAllText(file, "v1");
        var first = SharedHostPackageCache.ComputeDirectoryFingerprint(dir);
        Assert.Equal(64, first.Length);
        Assert.Equal(first, SharedHostPackageCache.ComputeDirectoryFingerprint(dir));
        File.WriteAllText(file, "v2-longer");
        Assert.NotEqual(first, SharedHostPackageCache.ComputeDirectoryFingerprint(dir));
        File.SetLastWriteTimeUtc(file, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var touched = SharedHostPackageCache.ComputeDirectoryFingerprint(dir);
        File.SetLastWriteTimeUtc(file, new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.NotEqual(touched, SharedHostPackageCache.ComputeDirectoryFingerprint(dir));
    }

    [Fact]
    public async Task SharedHostPackageCache_PopulatesOncePerHostAcrossSandboxes()
    {
        var cache = new SharedHostPackageCache(Path.Combine(_root, "store"));
        var builds = 0;
        var fingerprint = new string('a', 64);
        async Task<string> EnsureAsync()
        {
            return await cache.EnsurePopulatedAsync(
                "package-cache-000",
                fingerprint,
                (directory, ct) =>
                {
                    Interlocked.Increment(ref builds);
                    File.WriteAllText(Path.Combine(directory, "package-cache.tar"), "archive-bytes");
                    return Task.CompletedTask;
                });
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => EnsureAsync()));
        Assert.Equal(1, builds);
        Assert.Single(results.Distinct(StringComparer.Ordinal));
        Assert.Equal("archive-bytes", File.ReadAllText(Path.Combine(results[0], "package-cache.tar")));
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(Path.Combine(_root, "store", "package-cache-000"));
            Assert.True((mode & UnixFileMode.GroupRead) == 0, "shared cache must be operator-only");
            Assert.True((mode & UnixFileMode.OtherRead) == 0, "shared cache must be operator-only");
        }
    }

    [Fact]
    public async Task SharedHostPackageCache_RepopulatesOnFingerprintChangeAndEvictsStale()
    {
        var cache = new SharedHostPackageCache(Path.Combine(_root, "store"));
        var builds = 0;
        Task Populate(string marker, string directory, CancellationToken ct)
        {
            Interlocked.Increment(ref builds);
            File.WriteAllText(Path.Combine(directory, "package-cache.tar"), marker);
            return Task.CompletedTask;
        }
        var first = await cache.EnsurePopulatedAsync("key", new string('a', 64), (d, ct) => Populate("v1", d, ct));
        var second = await cache.EnsurePopulatedAsync("key", new string('b', 64), (d, ct) => Populate("v2", d, ct));
        Assert.Equal(2, builds);
        Assert.NotEqual(first, second);
        var eviction = cache.TryEvictStaleFingerprints("key", new string('b', 64));
        Assert.Equal(1, eviction.Evicted);
        Assert.Empty(eviction.Failures);
        Assert.False(Directory.Exists(Path.GetDirectoryName(first)));
        // A tampered completion marker forces repopulation instead of serving garbage.
        File.WriteAllText(Path.Combine(second, ".complete"), new string('c', 64));
        _ = await cache.EnsurePopulatedAsync("key", new string('b', 64), (d, ct) => Populate("v3", d, ct));
        Assert.Equal(3, builds);
    }

    [Fact]
    public async Task SharedHostPackageCache_CleansTempAfterFailedPopulate()
    {
        var cache = new SharedHostPackageCache(Path.Combine(_root, "store"));
        var attempts = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.EnsurePopulatedAsync(
            "key",
            new string('a', 64),
            (directory, ct) =>
            {
                attempts++;
                File.WriteAllText(Path.Combine(directory, "partial"), "x");
                throw new InvalidOperationException("boom");
            }));
        Assert.Equal(1, attempts);
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(_root, "store", "key", new string('a', 64))));
        await Assert.ThrowsAsync<ArgumentException>(() => cache.EnsurePopulatedAsync(
            "../escape", new string('a', 64), (d, ct) => Task.CompletedTask));
        await Assert.ThrowsAsync<ArgumentException>(() => cache.EnsurePopulatedAsync(
            "key", "not-a-fingerprint", (d, ct) => Task.CompletedTask));
    }
}
