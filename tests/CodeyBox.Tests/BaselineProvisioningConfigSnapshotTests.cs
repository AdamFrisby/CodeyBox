using System.Collections;
using CodeyBox.Api;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

public sealed class BaselineProvisioningConfigSnapshotTests
{
    [Fact]
    public void MultipassSnapshots_EnforceObservedCollectionBoundsAndKeepProviderKey()
    {
        var seeds = new DeceptiveCollection<PackageCacheSeedConfig>(
            Enumerable.Range(0, BaselineProvisioningLimits.MaximumPackageCacheSeeds + 1)
                .Select(index => new PackageCacheSeedConfig
                {
                    HostSourcePath = $"/cache/{index}",
                    VmDestPath = $"/var/cache/{index}",
                }));
        var provisions = new DeceptiveCollection<ExecutableProvisionConfig>(
            Enumerable.Range(0, BaselineProvisioningLimits.MaximumExecutableProvisions + 1)
                .Select(index => new ExecutableProvisionConfig
                {
                    HostSourcePath = $"/tools/{index}",
                    VmDestPath = $"/usr/local/bin/tool-{index}",
                }));

        var seedError = Assert.Throws<InvalidOperationException>(() =>
            BaselineProvisioningConfigSnapshot.SnapshotPackageCacheSeeds(
                seeds,
                "CodeyBox:MultipassPackageCacheSeeds"));
        var provisionError = Assert.Throws<InvalidOperationException>(() =>
            BaselineProvisioningConfigSnapshot.SnapshotExecutableProvisions(
                provisions,
                "CodeyBox:MultipassExecutableProvisions"));

        Assert.Contains("CodeyBox:MultipassPackageCacheSeeds", seedError.Message, StringComparison.Ordinal);
        Assert.Contains("CodeyBox:MultipassExecutableProvisions", provisionError.Message, StringComparison.Ordinal);
        Assert.Equal(BaselineProvisioningLimits.MaximumPackageCacheSeeds + 1, seeds.EnumeratedCount);
        Assert.Equal(BaselineProvisioningLimits.MaximumExecutableProvisions + 1, provisions.EnumeratedCount);
    }

    [Fact]
    public void MultipassSnapshots_RejectInvalidTextBeforeItReachesProviderOptions()
    {
        var invalidValues = new[]
        {
            " ",
            "contains\ncontrol",
            "isolated\ud800surrogate",
            new string('\u00e9', BaselineProvisioningLimits.MaximumProvisioningTextUtf8Bytes),
        };

        foreach (var invalid in invalidValues)
        {
            var error = Assert.Throws<InvalidOperationException>(() =>
                BaselineProvisioningConfigSnapshot.SnapshotPackageCacheSeeds(
                [
                    new PackageCacheSeedConfig
                    {
                        HostSourcePath = invalid,
                        VmDestPath = "/var/cache/codeybox",
                    },
                ],
                "CodeyBox:MultipassPackageCacheSeeds"));

            Assert.Contains("CodeyBox:MultipassPackageCacheSeeds", error.Message, StringComparison.Ordinal);
        }

        var symlinkError = Assert.Throws<InvalidOperationException>(() =>
            BaselineProvisioningConfigSnapshot.SnapshotExecutableProvisions(
            [
                new ExecutableProvisionConfig
                {
                    HostSourcePath = "/srv/tool",
                    VmDestPath = "/usr/local/bin/tool",
                    VmSymlinks = [""],
                },
            ],
            "CodeyBox:MultipassExecutableProvisions"));

        Assert.Contains("CodeyBox:MultipassExecutableProvisions:VmSymlinks", symlinkError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageCacheSnapshot_RejectsNonPositiveAndNonFiniteSizeCaps()
    {
        var invalidCaps = new[]
        {
            0,
            -1,
            double.NaN,
            double.PositiveInfinity,
            double.NegativeInfinity,
        };

        foreach (var invalidCap in invalidCaps)
        {
            var error = Assert.Throws<InvalidOperationException>(() =>
                BaselineProvisioningConfigSnapshot.SnapshotPackageCacheSeeds(
                [
                    new PackageCacheSeedConfig
                    {
                        HostSourcePath = "/srv/cache",
                        VmDestPath = "/var/cache/codeybox",
                        MaxSizeMB = invalidCap,
                    },
                ],
                "CodeyBox:MultipassPackageCacheSeeds"));

            Assert.Contains("CodeyBox:MultipassPackageCacheSeeds:MaxSizeMB", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Snapshots_DeepCopyMutableApiConfiguration()
    {
        var seed = new PackageCacheSeedConfig
        {
            HostSourcePath = "/srv/cache",
            VmDestPath = "/var/cache/codeybox",
        };
        var provision = new ExecutableProvisionConfig
        {
            HostSourcePath = "/srv/tool",
            VmDestPath = "/usr/local/bin/tool",
            VmSymlinks = ["/opt/codeybox/bin/tool"],
            Label = string.Empty,
        };

        var seeds = BaselineProvisioningConfigSnapshot.SnapshotPackageCacheSeeds(
            [seed],
            "CodeyBox:Incus:PackageCacheSeeds");
        var provisions = BaselineProvisioningConfigSnapshot.SnapshotExecutableProvisions(
            [provision],
            "CodeyBox:Incus:ExecutableProvisions");
        seed.HostSourcePath = "/changed/cache";
        provision.VmDestPath = "/changed/tool";
        provision.VmSymlinks[0] = "/changed/link";

        Assert.Equal("/srv/cache", Assert.Single(seeds).HostSourcePath);
        var snapshot = Assert.Single(provisions);
        Assert.Equal("/usr/local/bin/tool", snapshot.VmDestPath);
        Assert.Equal(["/opt/codeybox/bin/tool"], snapshot.VmSymlinks);
        Assert.Equal(string.Empty, snapshot.Label);
    }

    private sealed class DeceptiveCollection<T>(IEnumerable<T> values) : IReadOnlyCollection<T>
    {
        public int Count => 0;
        public int EnumeratedCount { get; private set; }

        public IEnumerator<T> GetEnumerator()
        {
            foreach (var value in values)
            {
                EnumeratedCount++;
                yield return value;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
