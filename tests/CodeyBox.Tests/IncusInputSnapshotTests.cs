using System.Collections;
using CodeyBox.Core;
using CodeyBox.Sandbox.Incus;

namespace CodeyBox.Tests;

public sealed class IncusInputSnapshotTests
{
    [Fact]
    public void CaptureSpec_SnapshotsMutableCollectionsAndNestedRecords()
    {
        var mounts = new List<SandboxMount>
        {
            new() { SandboxPath = "/work", Tmpfs = true, ReadOnly = false },
        };
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["BEFORE"] = "value",
        };
        var allowedHosts = new List<string> { "before.example" };
        var source = new SandboxSpec
        {
            ImageReference = "local-image",
            Mounts = mounts,
            Environment = environment,
            Network = new SandboxNetworkPolicy
            {
                AllowedHosts = allowedHosts,
                ProfileName = "internet-only",
            },
        };

        var snapshot = IncusInputSnapshot.CaptureSpec(source);
        mounts.Clear();
        environment.Clear();
        allowedHosts.Clear();

        Assert.Single(snapshot.Mounts);
        Assert.Equal("value", snapshot.Environment["BEFORE"]);
        Assert.Equal(["before.example"], snapshot.Network.AllowedHosts);
        Assert.NotSame(source.Network, snapshot.Network);
        Assert.NotSame(source.Limits, snapshot.Limits);
    }

    [Fact]
    public void CaptureSpec_RejectsDeceptiveMountListThatEnumeratesPastBound()
    {
        var source = new SandboxSpec
        {
            ImageReference = "local-image",
            Mounts = new DeceptiveReadOnlyList<SandboxMount>(
                Enumerable.Range(0, IncusMountStaging.MaximumMounts + 1)
                    .Select(index => new SandboxMount
                    {
                        SandboxPath = $"/mount-{index}",
                        Tmpfs = true,
                        ReadOnly = false,
                    })),
        };

        var error = Assert.Throws<ArgumentException>(() => IncusInputSnapshot.CaptureSpec(source));

        Assert.Contains("cannot contain more than", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureOptions_PreservesCaseInsensitiveProfilesAndRejectsCaseDuplicates()
    {
        var source = new IncusSandboxOptions
        {
            NetworkProfiles = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Internet-Only"] = "cb-net",
            },
        };
        var snapshot = IncusInputSnapshot.CaptureOptions(source);
        Assert.Equal("cb-net", snapshot.NetworkProfiles["internet-only"]);

        var duplicates = source with
        {
            NetworkProfiles = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["profile"] = "cb-one",
                ["PROFILE"] = "cb-two",
            },
        };
        Assert.Throws<ArgumentException>(() => IncusInputSnapshot.CaptureOptions(duplicates));
    }

    [Fact]
    public void PublicOptionsValidation_RejectsDeceptiveExtraRuncmdEnumeration()
    {
        var options = new IncusSandboxOptions
        {
            ExtraRuncmd = new DeceptiveReadOnlyList<string>(
                Enumerable.Repeat("true", IncusSandboxOptions.MaximumExtraRuncmdCount + 1)),
        };

        var errors = IncusSandboxOptions.Validate(options);

        Assert.Contains(errors, error => error.Contains("cannot contain more than", StringComparison.Ordinal));
    }

    [Fact]
    public void StrictUtf8Boundary_RejectsOversizedTextAndIsolatedSurrogates()
    {
        Assert.Throws<ArgumentException>(() => IncusInputValidation.GetBoundedUtf8ByteCount(
            new string('x', 4097),
            4096,
            "value",
            "value"));
        Assert.Throws<ArgumentException>(() => IncusInputValidation.GetBoundedUtf8ByteCount(
            "valid\ud800invalid",
            4096,
            "value",
            "value"));
    }

    [Fact]
    public void CaptureSpec_RejectsHugeEnvironmentKeyBeforeDictionaryInsertion()
    {
        var environment = new DeceptiveReadOnlyDictionary(
            new KeyValuePair<string, string>(new string('K', 1024 * 1024), "value"));
        var source = new SandboxSpec
        {
            ImageReference = "local-image",
            Environment = environment,
        };

        var error = Assert.Throws<ArgumentException>(() => IncusInputSnapshot.CaptureSpec(source));

        Assert.Contains("key longer than", error.Message, StringComparison.Ordinal);
    }

    private sealed class DeceptiveReadOnlyList<T>(IEnumerable<T> values) : IReadOnlyList<T>
    {
        public int Count => 0;
        public T this[int index] => throw new InvalidOperationException("Indexer must not be trusted.");
        public IEnumerator<T> GetEnumerator() => values.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class DeceptiveReadOnlyDictionary(KeyValuePair<string, string> entry) :
        IReadOnlyDictionary<string, string>
    {
        public int Count => 0;
        public IEnumerable<string> Keys => throw new InvalidOperationException("Keys must not be trusted.");
        public IEnumerable<string> Values => throw new InvalidOperationException("Values must not be trusted.");
        public string this[string key] => throw new KeyNotFoundException();
        public bool ContainsKey(string key) => false;
        public bool TryGetValue(string key, out string value)
        {
            value = string.Empty;
            return false;
        }
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        {
            yield return entry;
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
