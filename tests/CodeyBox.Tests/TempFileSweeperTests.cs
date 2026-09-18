using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Coverage for <see cref="TempFileSweeper"/> — the bounded startup sweep
/// that reaps stale <c>codeybox-*</c> temp entries. All tests run through the
/// real filesystem against an isolated root, never the shared temp path.
/// </summary>
public sealed class TempFileSweeperTests
{
    private static string CreateIsolatedRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "codeybox-sweep-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRootBestEffort(string root)
    {
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static TempSweepOptions OptionsFor(string root, TimeSpan maxAge) => new()
    {
        TempDirectory = root,
        MaxAge = maxAge,
    };

    [Fact]
    public void Sweep_RemovesStaleEntry_AndLeavesFreshOneAlone()
    {
        var root = CreateIsolatedRoot();
        try
        {
            var stale = Path.Combine(root, "codeybox-old-" + Guid.NewGuid().ToString("N") + ".db");
            var fresh = Path.Combine(root, "codeybox-new-" + Guid.NewGuid().ToString("N") + ".db");
            var unrelated = Path.Combine(root, "other-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(stale, "stale");
            File.WriteAllText(fresh, "fresh");
            File.WriteAllText(unrelated, "unrelated");
            var old = DateTime.UtcNow - TimeSpan.FromDays(3);
            File.SetLastWriteTimeUtc(stale, old);
            File.SetLastWriteTimeUtc(unrelated, old);

            var summary = new TempFileSweeper(TimeProvider.System, NullLogger<TempFileSweeper>.Instance)
                .Sweep(OptionsFor(root, TimeSpan.FromHours(48)));

            Assert.False(File.Exists(stale), "Stale codeybox-* entry should be removed.");
            Assert.True(File.Exists(fresh), "Fresh codeybox-* entry must survive.");
            Assert.True(File.Exists(unrelated), "Entries outside the codeybox-* prefix must survive.");
            Assert.Equal(1, summary.Removed);
            Assert.Equal(2, summary.Scanned);
        }
        finally
        {
            DeleteRootBestEffort(root);
        }
    }

    [Fact]
    public void Sweep_RemovesStaleDirectory_WithStaleContents()
    {
        var root = CreateIsolatedRoot();
        try
        {
            var dir = Path.Combine(root, "codeybox-staledir-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "payload.bin"), "payload");
            var old = DateTime.UtcNow - TimeSpan.FromDays(3);
            File.SetLastWriteTimeUtc(Path.Combine(dir, "payload.bin"), old);
            Directory.SetLastWriteTimeUtc(dir, old);

            var summary = new TempFileSweeper(TimeProvider.System, NullLogger<TempFileSweeper>.Instance)
                .Sweep(OptionsFor(root, TimeSpan.FromHours(48)));

            Assert.False(Directory.Exists(dir), "Stale codeybox-* directory should be removed.");
            Assert.Equal(1, summary.Removed);
        }
        finally
        {
            DeleteRootBestEffort(root);
        }
    }

    [Fact]
    public void Sweep_KeepsDirectory_WithRecentlyWrittenContents()
    {
        var root = CreateIsolatedRoot();
        try
        {
            var dir = Path.Combine(root, "codeybox-livedir-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            // The directory entry itself is old, but a file inside was just
            // written — a directory mtime alone must not prove staleness.
            Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow - TimeSpan.FromDays(3));
            File.WriteAllText(Path.Combine(dir, "active.db"), "live");

            var summary = new TempFileSweeper(TimeProvider.System, NullLogger<TempFileSweeper>.Instance)
                .Sweep(OptionsFor(root, TimeSpan.FromHours(48)));

            Assert.True(Directory.Exists(dir), "Directory with fresh contents must survive.");
            Assert.Equal(0, summary.Removed);
            Assert.Equal(1, summary.SkippedFresh);
        }
        finally
        {
            DeleteRootBestEffort(root);
        }
    }

    [Fact]
    public void Sweep_RefusesToTraverseSymlink_OutOfTempPath()
    {
        var root = CreateIsolatedRoot();
        var outside = Path.Combine(
            Path.GetTempPath(), "codeybox-sweep-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "sentinel.txt");
        File.WriteAllText(sentinel, "must survive");
        try
        {
            var linkPath = Path.Combine(root, "codeybox-evil-" + Guid.NewGuid().ToString("N"));
            Directory.CreateSymbolicLink(linkPath, outside);

            var summary = new TempFileSweeper(TimeProvider.System, NullLogger<TempFileSweeper>.Instance)
                .Sweep(OptionsFor(root, TimeSpan.FromHours(48)));

            Assert.True(File.Exists(sentinel), "The sweep must never traverse a symlink into outside content.");
            Assert.True(Directory.Exists(outside), "The link target must survive.");
            Assert.Equal(0, summary.Removed);
            Assert.Equal(1, summary.SkippedSymlink);
        }
        finally
        {
            DeleteRootBestEffort(root);
            DeleteRootBestEffort(outside);
        }
    }

    [Fact]
    public void Sweep_LeavesSharedHooksDirectoryAlone()
    {
        var root = CreateIsolatedRoot();
        try
        {
            var shared = Path.Combine(root, TempFileSweeper.SharedHooksDirectoryName);
            Directory.CreateDirectory(shared);
            Directory.SetLastWriteTimeUtc(shared, DateTime.UtcNow - TimeSpan.FromDays(30));

            var summary = new TempFileSweeper(TimeProvider.System, NullLogger<TempFileSweeper>.Instance)
                .Sweep(OptionsFor(root, TimeSpan.FromHours(48)));

            Assert.True(Directory.Exists(shared), "The live shared hooks directory must survive sweeps.");
            Assert.Equal(0, summary.Removed);
            Assert.Equal(1, summary.SkippedExcluded);
        }
        finally
        {
            DeleteRootBestEffort(root);
        }
    }

    [Fact]
    public void Sweep_ClampsZeroMaxAge_ToConservativeFloor()
    {
        var root = CreateIsolatedRoot();
        try
        {
            var fresh = Path.Combine(root, "codeybox-fresh-" + Guid.NewGuid().ToString("N") + ".db");
            File.WriteAllText(fresh, "fresh");

            // A zero threshold must not wipe a live run's files: the
            // effective threshold is raised to the conservative floor.
            var summary = new TempFileSweeper(TimeProvider.System, NullLogger<TempFileSweeper>.Instance)
                .Sweep(OptionsFor(root, TimeSpan.Zero));

            Assert.True(File.Exists(fresh), "Zero MaxAge must be clamped, not honoured.");
            Assert.Equal(0, summary.Removed);
        }
        finally
        {
            DeleteRootBestEffort(root);
        }
    }

    [Fact]
    public async Task StartupService_RunsSweep_AndNeverFailsStartup()
    {
        var root = CreateIsolatedRoot();
        try
        {
            var stale = Path.Combine(root, "codeybox-old-" + Guid.NewGuid().ToString("N") + ".db");
            File.WriteAllText(stale, "stale");
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - TimeSpan.FromDays(3));

            var service = new TempSweepStartupService(
                () => OptionsFor(root, TimeSpan.FromHours(48)),
                TimeProvider.System,
                NullLogger<TempSweepStartupService>.Instance);
            await service.StartAsync(CancellationToken.None);
            await service.StopAsync(CancellationToken.None);

            Assert.False(File.Exists(stale), "Startup service should reap the stale entry.");
        }
        finally
        {
            DeleteRootBestEffort(root);
        }
    }

    [Fact]
    public async Task StartupService_Disabled_SkipsSweep()
    {
        var root = CreateIsolatedRoot();
        try
        {
            var stale = Path.Combine(root, "codeybox-old-" + Guid.NewGuid().ToString("N") + ".db");
            File.WriteAllText(stale, "stale");
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - TimeSpan.FromDays(3));

            var options = OptionsFor(root, TimeSpan.FromHours(48));
            options.Enabled = false;
            var service = new TempSweepStartupService(
                () => options,
                TimeProvider.System,
                NullLogger<TempSweepStartupService>.Instance);
            await service.StartAsync(CancellationToken.None);

            Assert.True(File.Exists(stale), "Disabled sweep must leave everything alone.");
        }
        finally
        {
            DeleteRootBestEffort(root);
        }
    }
}
