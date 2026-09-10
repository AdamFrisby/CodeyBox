using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Regression tests for the test-suite temp leak observed on the live host
/// ($HOME/codey-test-tmp held 11 GB of <c>codeybox-suspend-*.db-wal</c>,
/// <c>cb-wp-hotreload-*.db-*</c> and <c>codeybox-sandbox-*/work</c> scratch
/// that no test ever removed): scratch directories must disappear on
/// disposal, even when the body throws, and stale leftovers must be swept
/// on setup.
/// </summary>
public sealed class TestScratchDirectoryTests
{
    [Fact]
    public void ScratchDirectory_DoesNotExist_AfterDisposal()
    {
        string path;
        using (var scratch = TestScratchDirectory.Create("codeybox-scratch-"))
        {
            path = scratch.Path;
            Assert.True(Directory.Exists(path));
            File.WriteAllText(Path.Combine(path, "store.db"), "data");
            File.WriteAllText(Path.Combine(path, "store.db-wal"), "wal");
            File.WriteAllText(Path.Combine(path, "store.db-shm"), "shm");
        }

        Assert.False(
            Directory.Exists(path),
            "The fixture temp directory must be removed when the fixture completes.");
    }

    [Fact]
    public void ScratchDirectory_IsStillRemoved_WhenBodyThrows()
    {
        // Mirrors the xUnit fixture lifecycle: the body throws, teardown
        // still runs in finally — the directory must not survive the failure.
        var scratch = TestScratchDirectory.Create("codeybox-scratch-");
        var path = scratch.Path;
        try
        {
            File.WriteAllText(Path.Combine(path, "partial.txt"), "partial");
            throw new InvalidOperationException("simulated test failure");
        }
        catch (InvalidOperationException)
        {
            // Swallowed to emulate the runner recording the failure.
        }
        finally
        {
            scratch.Dispose();
        }

        Assert.False(
            Directory.Exists(path),
            "The fixture temp directory must be removed even when the body throws.");
    }

    [Fact]
    public async Task SqliteStore_InScratchDirectory_LeavesNothingBehind()
    {
        // End-to-end through the real WAL-mode store: the connection is
        // disposed before the directory is removed, so no -wal/-shm pair
        // can outlive the fixture.
        string path;
        string dbPath;
        using (var scratch = TestScratchDirectory.Create("codeybox-suspend-"))
        {
            path = scratch.Path;
            dbPath = scratch.DbPath();
            using (var store = new SqliteWorkItemStore(dbPath))
            {
                await store.CreateAsync(new WorkItem
                {
                    Id = WorkItemId.New(),
                    ProjectId = new ProjectId("scratch-test"),
                    Title = "t",
                    Prompt = "p",
                    State = WorkItemState.Working,
                    StartedAt = DateTimeOffset.UtcNow,
                });
            }
        }

        Assert.False(Directory.Exists(path));
        Assert.False(File.Exists(dbPath));
        Assert.False(File.Exists(dbPath + "-wal"));
        Assert.False(File.Exists(dbPath + "-shm"));
    }

    [Fact]
    public void SweepStale_RemovesOldLeftovers_KeepsFreshAndForeignEntries()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "codeybox-sweep-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var staleFile = Path.Combine(root, "codeybox-suspend-deadbeef.db");
            var staleDir = Path.Combine(root, "cb-wp-hotreload-deadbeef");
            var freshFile = Path.Combine(root, "codeybox-suspend-live.db");
            var foreignFile = Path.Combine(root, "unrelated-tool-output.txt");
            File.WriteAllText(staleFile, "stale");
            Directory.CreateDirectory(staleDir);
            File.WriteAllText(Path.Combine(staleDir, "store.db-wal"), "stale");
            File.WriteAllText(freshFile, "fresh");
            File.WriteAllText(foreignFile, "foreign");
            var old = DateTime.UtcNow - TimeSpan.FromDays(3);
            File.SetLastWriteTimeUtc(staleFile, old);
            Directory.SetLastWriteTimeUtc(staleDir, old);
            File.SetLastWriteTimeUtc(foreignFile, old);

            TestScratchDirectory.SweepStale(root, TimeSpan.FromHours(24));

            Assert.False(File.Exists(staleFile));
            Assert.False(Directory.Exists(staleDir));
            Assert.True(File.Exists(freshFile));
            Assert.True(File.Exists(foreignFile));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ScratchRoot_ComesFromStandardTempPath_WhenUnconfigured()
    {
        // The suite must never hardcode a machine-specific $HOME path: with
        // no override configured the root is the standard temp-path API.
        if (Environment.GetEnvironmentVariable(TestScratchDirectory.ScratchRootVariable) is not null)
            return;

        Assert.Equal(Path.GetTempPath(), TestScratchDirectory.ResolveRoot());
    }
}
