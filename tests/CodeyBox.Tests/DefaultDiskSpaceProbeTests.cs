using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Direct coverage for <see cref="DefaultDiskSpaceProbe"/>. Other disk-guard
/// tests substitute a fake probe; this suite drives the production
/// DriveInfo-backed implementation against real temp directories so the
/// load-bearing branches (null/whitespace fast-path, walk-up-to-existing
/// ancestor, DriveInfo.AvailableFreeSpace, exception → null swallow) stay
/// wired correctly.
/// </summary>
public sealed class DefaultDiskSpaceProbeTests : IDisposable
{
    private readonly string _root;

    public DefaultDiskSpaceProbeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"cb-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose() { CodeyBox.Tests.TestTempArtifacts.DeleteDirectory(_root); }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void GetFreeBytes_ReturnsNull_ForNullOrWhitespacePath(string? path)
    {
        var probe = new DefaultDiskSpaceProbe();

        Assert.Null(probe.GetFreeBytes(path!));
    }

    [Fact]
    public void GetFreeBytes_ReturnsAvailableFreeSpace_ForExistingDirectory()
    {
        var probe = new DefaultDiskSpaceProbe();

        var free = probe.GetFreeBytes(_root);

        // We can't predict the exact value (it's the host's free disk), but
        // the contract says: a real, existing directory must return a real,
        // non-negative number — never null, never the swallow-exception path.
        Assert.NotNull(free);
        Assert.True(free!.Value >= 0,
            $"DriveInfo.AvailableFreeSpace must be non-negative; got {free.Value}");
    }

    [Fact]
    public void GetFreeBytes_WalksUpToExistingAncestor_ForMissingLeafPath()
    {
        // Mirror the production foot-gun the walk-up logic exists to handle:
        // operator points the guard at a staging path that doesn't exist yet
        // on a fresh host (e.g. /var/snap/multipass/common/data before any
        // VM has been launched). The probe must resolve to the nearest
        // existing ancestor instead of returning null.
        var missingLeaf = Path.Combine(_root, "does-not-exist-yet", "nested", "deeper");
        var probe = new DefaultDiskSpaceProbe();

        var free = probe.GetFreeBytes(missingLeaf);

        Assert.NotNull(free);
        Assert.True(free!.Value >= 0);
    }

    [Fact]
    public void GetFreeBytes_ReturnsNull_WhenNoAncestorExistsWithinDepthLimit()
    {
        // Build a path whose final 16 segments all guarantee not to exist on
        // any reasonable host, forcing the walk-up cap to trip. With every
        // ancestor synthetic, the probe cannot resolve and must return null
        // rather than throw — keeping the preflight inconclusive (not
        // blocking) when configuration is genuinely broken.
        var prefix = $"/__codeybox_nonexistent_{Guid.NewGuid():N}";
        var deep = prefix + string.Concat(Enumerable.Repeat("/x", 32));
        var probe = new DefaultDiskSpaceProbe();

        Assert.Null(probe.GetFreeBytes(deep));
    }

    [Fact]
    public void GetFreeBytes_ReturnsNull_OnInvalidPathCharacters()
    {
        // Path.GetFullPath throws for paths containing invalid characters
        // (e.g. an embedded NUL byte). The probe must swallow that into
        // null so a misconfigured operator path doesn't crash CreateAsync.
        var probe = new DefaultDiskSpaceProbe();

        Assert.Null(probe.GetFreeBytes("/tmp/\0invalid"));
    }
}
