using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class SandboxLifecycleScopedDefaultContractTests
{
    [Fact]
    public async Task DisposeDefault_UnscopedSnapshotDelegatesByName()
    {
        var implementation = new RecordingManagedLifecycle();
        IManagedSandboxLifecycle lifecycle = implementation;

        await lifecycle.DisposeLeakedAsync(Snapshot("unscoped"), CancellationToken.None);

        Assert.Equal(["unscoped"], implementation.DisposedNames);
    }

    [Theory]
    [InlineData("provider-a", null)]
    [InlineData(null, "host-a")]
    [InlineData("provider-a", "host-a")]
    public async Task DisposeDefault_ScopedSnapshotFailsClosed(
        string? lifecycleProviderId,
        string? hostId)
    {
        var implementation = new RecordingManagedLifecycle();
        IManagedSandboxLifecycle lifecycle = implementation;
        var snapshot = Snapshot("scoped") with
        {
            LifecycleProviderId = lifecycleProviderId,
            HostId = hostId,
        };

        await Assert.ThrowsAsync<NotSupportedException>(
            () => lifecycle.DisposeLeakedAsync(snapshot, CancellationToken.None));

        Assert.Empty(implementation.DisposedNames);
    }

    [Fact]
    public async Task ResumeDefault_UnscopedSnapshotDelegatesByName()
    {
        var implementation = new RecordingSuspendingProvider();
        ISuspendingSandboxProvider provider = implementation;

        await provider.ResumeSandboxAsync(Snapshot("unscoped"), CancellationToken.None);

        Assert.Equal(["unscoped"], implementation.ResumedNames);
    }

    [Theory]
    [InlineData("provider-a", null)]
    [InlineData(null, "host-a")]
    [InlineData("provider-a", "host-a")]
    public async Task ResumeDefault_ScopedSnapshotFailsClosed(
        string? lifecycleProviderId,
        string? hostId)
    {
        var implementation = new RecordingSuspendingProvider();
        ISuspendingSandboxProvider provider = implementation;
        var snapshot = Snapshot("scoped") with
        {
            LifecycleProviderId = lifecycleProviderId,
            HostId = hostId,
        };

        await Assert.ThrowsAsync<NotSupportedException>(
            () => provider.ResumeSandboxAsync(snapshot, CancellationToken.None));

        Assert.Empty(implementation.ResumedNames);
    }

    private static ManagedSandboxInfo Snapshot(string name) =>
        new(name, CreatedAt: null, DiskBytes: null, IsTrackedActive: false);

    private sealed class RecordingManagedLifecycle : IManagedSandboxLifecycle
    {
        public string Name => "recording";
        public List<string> DisposedNames { get; } = [];

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);

        public Task DisposeLeakedAsync(string name, CancellationToken ct)
        {
            DisposedNames.Add(name);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSuspendingProvider : ISuspendingSandboxProvider
    {
        public List<string> ResumedNames { get; } = [];

        public Task ResumeSandboxAsync(string name, CancellationToken ct)
        {
            ResumedNames.Add(name);
            return Task.CompletedTask;
        }
    }
}
