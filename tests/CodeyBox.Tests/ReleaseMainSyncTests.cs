using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests;

/// <summary>
/// Tests ReleaseMainSyncService sweep logic.
/// Verifies that the service calls TryMergeUpstreamBranchAsync at the right intervals
/// and that it publishes release.sync_conflict webhooks on merge conflicts.
/// </summary>
public sealed class ReleaseMainSyncTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"cb-sync-{Guid.NewGuid():N}.db");
    private readonly SqliteReleaseStore _releaseStore;
    private readonly SqliteWorkItemStore _workItemStore;
    private readonly CapturingWebhookDispatcher _webhooks = new();

    public ReleaseMainSyncTests()
    {
        _releaseStore = new SqliteReleaseStore(_dbPath);
        _workItemStore = new SqliteWorkItemStore(_dbPath);
    }

    public void Dispose()
    {
        _workItemStore.Dispose();
        _releaseStore.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task Sweep_CallsMerge_ForOpenReleaseWithBranchAndInterval()
    {
        var fakeRemote = new FakeMergeUpstreamRemote { MergeResult = true };
        var project = EnabledProject(autoSyncMinutes: 720);
        var rel = await SeedOpenReleaseWithBranch("release/v1.0");
        var svc = BuildSync(fakeRemote, project);

        await svc.RunSweepForTestAsync(default);

        Assert.Single(fakeRemote.MergeAttempts);
        Assert.Equal(("release/v1.0", project.DefaultBaseBranch ?? "main"), fakeRemote.MergeAttempts[0]);
    }

    [Fact]
    public async Task Sweep_SkipsRelease_WithNoBranch()
    {
        var fakeRemote = new FakeMergeUpstreamRemote { MergeResult = true };
        var project = EnabledProject(autoSyncMinutes: 720);
        // Release has no branch yet
        var rel = ReleaseTestHelper.SeedRelease(ReleaseState.Open);
        await _releaseStore.CreateAsync(rel);
        var svc = BuildSync(fakeRemote, project);

        await svc.RunSweepForTestAsync(default);

        Assert.Empty(fakeRemote.MergeAttempts);
    }

    [Fact]
    public async Task Sweep_SkipsRelease_WhenAutoSyncDisabled()
    {
        var fakeRemote = new FakeMergeUpstreamRemote { MergeResult = true };
        // autoSyncMinutes = 0 means disabled per ProjectRepository.ResolveReleaseConfig
        var project = EnabledProject(autoSyncMinutes: 0);
        await SeedOpenReleaseWithBranch("release/v1.0");
        var svc = BuildSync(fakeRemote, project);

        await svc.RunSweepForTestAsync(default);

        Assert.Empty(fakeRemote.MergeAttempts);
    }

    [Fact]
    public async Task Sweep_OnConflict_PublishesSyncConflictWebhook()
    {
        var fakeRemote = new FakeMergeUpstreamRemote { MergeResult = false };
        var project = EnabledProject(autoSyncMinutes: 720);
        await SeedOpenReleaseWithBranch("release/v1.0");
        var svc = BuildSync(fakeRemote, project);

        await svc.RunSweepForTestAsync(default);

        Assert.Contains(_webhooks.Events, e => e.Event == "release.sync_conflict");
    }

    [Fact]
    public async Task Sweep_SuccessfulMerge_DoesNotPublishConflictWebhook()
    {
        var fakeRemote = new FakeMergeUpstreamRemote { MergeResult = true };
        var project = EnabledProject(autoSyncMinutes: 720);
        await SeedOpenReleaseWithBranch("release/v1.0");
        var svc = BuildSync(fakeRemote, project);

        await svc.RunSweepForTestAsync(default);

        Assert.DoesNotContain(_webhooks.Events, e => e.Event == "release.sync_conflict");
    }

    private ReleaseMainSyncService BuildSync(FakeMergeUpstreamRemote fakeRemote, Project project)
    {
        var projects = new InMemoryProjectRepository(project);
        var factory = new FakeMergeUpstreamFactory(fakeRemote);
        return new ReleaseMainSyncService(
            _releaseStore,
            projects,
            _webhooks,
            factory,
            NullLogger<ReleaseMainSyncService>.Instance);
    }

    private async Task<Release> SeedOpenReleaseWithBranch(string branchName)
    {
        var rel = ReleaseTestHelper.SeedRelease(ReleaseState.Open);
        await _releaseStore.CreateAsync(rel);
        await _releaseStore.TrySetBranchAsync(rel.Id, branchName, "abc123");
        return rel;
    }

    private static Project EnabledProject(int autoSyncMinutes) => new()
    {
        Id = new ProjectId("test-project"),
        DisplayName = "Test",
        RepositoryUrl = "file:///tmp/noop",
        DefaultBaseBranch = "main",
        ReleaseConfig = new ProjectReleaseConfig
        {
            Enabled = true,
            AutoSyncMainInterval = autoSyncMinutes == 0
                ? null
                : TimeSpan.FromMinutes(autoSyncMinutes),
        },
    };
}
