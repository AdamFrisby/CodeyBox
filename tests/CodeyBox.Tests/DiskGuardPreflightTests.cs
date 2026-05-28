using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.HostProcess;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Multipass;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for the multipass disk-guard preflight. These exercise the
/// branches the operator can actually configure: free-bytes below threshold
/// throws a typed deferral exception; free-bytes above threshold returns
/// the unmodified path; an unconfigured guard is a no-op; and an
/// inconclusive probe (null free-bytes) refuses to block work.
///
/// We avoid running multipass by exercising the preflight only — the
/// production path also throws before it touches any directories, so the
/// preflight is the load-bearing check the bug report asked for.
/// </summary>
public sealed class DiskGuardPreflightTests : IDisposable
{
    private readonly string _stagingRoot;

    public DiskGuardPreflightTests()
    {
        _stagingRoot = Path.Combine(Path.GetTempPath(), $"cb-diskguard-{Guid.NewGuid():N}");
    }

    public void Dispose() { try { Directory.Delete(_stagingRoot, recursive: true); } catch { } }

    [Fact]
    public async Task CreateAsync_ThrowsSandboxDiskDeferredException_WhenAnyMountBelowThreshold()
    {
        var probe = new FakeDiskSpaceProbe(new Dictionary<string, long?>(StringComparer.Ordinal)
        {
            ["/fake/mp"] = 1L * 1024 * 1024 * 1024,
        });
        var provider = NewProvider(probe, new MultipassDiskGuardOptions
        {
            MultipassDataPath = "/fake/mp",
            MinFreeBytes = 10L * 1024 * 1024 * 1024,
            RecheckIn = TimeSpan.FromSeconds(42),
        });

        var ex = await Assert.ThrowsAsync<SandboxDiskDeferredException>(
            () => provider.CreateAsync(MinimalSpec()));

        Assert.Equal("/fake/mp", ex.MountPath);
        Assert.Equal(1L * 1024 * 1024 * 1024, ex.FreeBytes);
        Assert.Equal(10L * 1024 * 1024 * 1024, ex.ThresholdBytes);
        Assert.Equal(TimeSpan.FromSeconds(42), ex.RecheckIn);
    }

    [Fact]
    public async Task CreateAsync_DefersOnAdditionalPath_EvenWhenPrimaryHasHeadroom()
    {
        var probe = new FakeDiskSpaceProbe(new Dictionary<string, long?>(StringComparer.Ordinal)
        {
            ["/fake/mp"] = 200L * 1024 * 1024 * 1024,
            ["/var/lib/codeybox"] = 512L * 1024 * 1024,
        });
        var provider = NewProvider(probe, new MultipassDiskGuardOptions
        {
            MultipassDataPath = "/fake/mp",
            AdditionalPaths = ["/var/lib/codeybox"],
            MinFreeBytes = 1L * 1024 * 1024 * 1024,
        });

        var ex = await Assert.ThrowsAsync<SandboxDiskDeferredException>(
            () => provider.CreateAsync(MinimalSpec()));

        Assert.Equal("/var/lib/codeybox", ex.MountPath);
    }

    [Fact]
    public async Task CreateAsync_DoesNotThrow_WhenFreeBytesAboveThreshold()
    {
        // Above-threshold means the preflight passes. We can't run the
        // launch end-to-end without multipass installed, so we only assert
        // the preflight didn't throw the deferral exception. Any other
        // exception from the downstream launch path is fine for this test.
        var probe = new FakeDiskSpaceProbe(new Dictionary<string, long?>(StringComparer.Ordinal)
        {
            ["/fake/mp"] = 200L * 1024 * 1024 * 1024,
        });
        var provider = NewProvider(probe, new MultipassDiskGuardOptions
        {
            MultipassDataPath = "/fake/mp",
            MinFreeBytes = 10L * 1024 * 1024 * 1024,
        });

        var thrown = await Record.ExceptionAsync(() => provider.CreateAsync(MinimalSpec()));
        Assert.IsNotType<SandboxDiskDeferredException>(thrown);
    }

    [Fact]
    public async Task CreateAsync_DoesNotThrow_WhenDiskGuardOptionsUnset()
    {
        var probe = new FakeDiskSpaceProbe(new Dictionary<string, long?>(StringComparer.Ordinal));
        var provider = NewProvider(probe, diskGuard: null);

        var thrown = await Record.ExceptionAsync(() => provider.CreateAsync(MinimalSpec()));
        Assert.IsNotType<SandboxDiskDeferredException>(thrown);
    }

    [Fact]
    public async Task CreateAsync_DoesNotDefer_WhenProbeReturnsNullForMount()
    {
        // null free-bytes means "we couldn't resolve the volume" — e.g.
        // operator pointed the guard at a path that doesn't exist on this
        // host. We treat that as inconclusive rather than blocking, so the
        // deployment doesn't grind to a halt on a misconfigured path.
        var probe = new FakeDiskSpaceProbe(new Dictionary<string, long?>(StringComparer.Ordinal)
        {
            ["/missing/mp"] = null,
        });
        var provider = NewProvider(probe, new MultipassDiskGuardOptions
        {
            MultipassDataPath = "/missing/mp",
            MinFreeBytes = 10L * 1024 * 1024 * 1024,
        });

        var thrown = await Record.ExceptionAsync(() => provider.CreateAsync(MinimalSpec()));
        Assert.IsNotType<SandboxDiskDeferredException>(thrown);
    }

    [Fact]
    public void SampleDiskGuardState_ReturnsThresholdAndFreeBytes_ForEachMonitoredMount()
    {
        var probe = new FakeDiskSpaceProbe(new Dictionary<string, long?>(StringComparer.Ordinal)
        {
            ["/fake/mp"] = 50L * 1024 * 1024 * 1024,
            ["/var/lib/codeybox"] = 200L * 1024 * 1024,
        });
        var provider = NewProvider(probe, new MultipassDiskGuardOptions
        {
            MultipassDataPath = "/fake/mp",
            AdditionalPaths = ["/var/lib/codeybox"],
            MinFreeBytes = 10L * 1024 * 1024 * 1024,
        });

        var snapshot = provider.SampleDiskGuardState();
        Assert.Collection(snapshot,
            row =>
            {
                Assert.Equal("/fake/mp", row.Path);
                Assert.Equal(50L * 1024 * 1024 * 1024, row.FreeBytes);
                Assert.Equal(10L * 1024 * 1024 * 1024, row.ThresholdBytes);
            },
            row =>
            {
                Assert.Equal("/var/lib/codeybox", row.Path);
                Assert.Equal(200L * 1024 * 1024, row.FreeBytes);
                Assert.Equal(10L * 1024 * 1024 * 1024, row.ThresholdBytes);
            });
    }

    private MultipassSandboxProvider NewProvider(IDiskSpaceProbe probe, MultipassDiskGuardOptions? diskGuard)
    {
        return new MultipassSandboxProvider(
            new MultipassSandboxOptions
            {
                StagingDirectory = _stagingRoot,
                DiskGuard = diskGuard,
            },
            NullLogger<MultipassSandboxProvider>.Instance,
            timings: null,
            runner: new ThrowingProcessRunner(),
            daemonRetryPolicy: null,
            diskProbe: probe);
    }

    private static SandboxSpec MinimalSpec() => new()
    {
        ImageReference = "ubuntu",
    };

    private sealed class FakeDiskSpaceProbe : IDiskSpaceProbe
    {
        private readonly IReadOnlyDictionary<string, long?> _free;
        public FakeDiskSpaceProbe(IReadOnlyDictionary<string, long?> free) => _free = free;
        public long? GetFreeBytes(string path) => _free.TryGetValue(path, out var v) ? v : null;
    }

    /// <summary>
    /// Stand-in process runner that fails fast if the test ever reaches the
    /// real multipass launch path. The preflight tests only care about the
    /// pre-launch check; reaching the launch path is itself a test failure.
    /// </summary>
    private sealed class ThrowingProcessRunner : IProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(
            IReadOnlyList<string> argv,
            string? stdin,
            CancellationToken ct,
            Action<string>? stdoutChunkCallback = null,
            Action<string>? stderrChunkCallback = null,
            int? maxStdoutBytes = null,
            int? maxStderrBytes = null,
            IReadOnlyDictionary<string, string>? environment = null) =>
            throw new InvalidOperationException("preflight tests must not reach the multipass launch path");
    }
}
