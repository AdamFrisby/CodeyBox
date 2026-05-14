using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Tests;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests.Uat.UpstreamWebhooksAndReleases;

/// <summary>
/// UAT coverage for "Release main sync service - Periodically merges the
/// project main branch into open release branches".
/// Plan anchor: docs/uat/00-plan.md#upstream-webhooks-and-releases
/// </summary>
public sealed class ReleaseMainSyncUatTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-uat-sync-{Guid.NewGuid():N}.db");
    private readonly SqliteReleaseStore _releaseStore;
    private readonly CapturingWebhookDispatcher _webhooks = new();

    public ReleaseMainSyncUatTests() => _releaseStore = new SqliteReleaseStore(_dbPath);

    public void Dispose()
    {
        _releaseStore.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task DueReleaseWithBranch_MergesConfiguredDefaultBranchAndThenSkipsUntilIntervalElapses()
    {
        var release = await CreateOpenReleaseWithBranchAsync("release/v1.0");
        var remote = new RecordingSyncRemote();
        remote.EnqueueMerge(true);
        var service = BuildService(remote, Project(autoSync: TimeSpan.FromHours(12), defaultBaseBranch: "develop"));

        await service.RunSweepForTestAsync(CancellationToken.None);
        await service.RunSweepForTestAsync(CancellationToken.None);

        var attempt = Assert.Single(remote.MergeAttempts);
        Assert.Equal(("release/v1.0", "develop"), attempt);
        Assert.Empty(_webhooks.Events);

        var restarted = BuildService(remote, Project(autoSync: TimeSpan.FromHours(12), defaultBaseBranch: "develop"));
        await restarted.RunSweepForTestAsync(CancellationToken.None);

        Assert.Equal(2, remote.MergeAttempts.Count);
        Assert.Equal(release.Id, (await _releaseStore.GetAsync(release.Id))!.Id);
    }

    [Fact]
    public async Task BranchlessDisabledAndNoIntervalReleasesAreSkipped()
    {
        await _releaseStore.CreateAsync(Release("branchless"));
        await CreateOpenReleaseWithBranchAsync("release/disabled", projectId: "disabled-project");
        await CreateOpenReleaseWithBranchAsync("release/no-interval", projectId: "no-interval-project");
        var remote = new RecordingSyncRemote();
        var service = new ReleaseMainSyncService(
            _releaseStore,
            new InMemoryProjectRepository(
                Project(id: "test-project", autoSync: TimeSpan.FromMinutes(5)),
                Project(id: "disabled-project", enabled: false, autoSync: TimeSpan.FromMinutes(5)),
                Project(id: "no-interval-project", autoSync: null)),
            _webhooks,
            new FixedUpstreamFactory(remote),
            NullLogger<ReleaseMainSyncService>.Instance);

        await service.RunSweepForTestAsync(CancellationToken.None);

        Assert.Empty(remote.MergeAttempts);
        Assert.Empty(_webhooks.Events);
    }

    [Fact]
    public async Task MergeConflictPublishesWebhookPayloadAndDoesNotMutateReleaseState()
    {
        var release = await CreateOpenReleaseWithBranchAsync("release/conflict");
        var remote = new RecordingSyncRemote();
        remote.EnqueueMerge(false);
        var service = BuildService(remote, Project(autoSync: TimeSpan.FromMinutes(5)));

        await service.RunSweepForTestAsync(CancellationToken.None);

        Assert.Equal(ReleaseState.Open, (await _releaseStore.GetAsync(release.Id))!.State);
        var evt = Assert.Single(_webhooks.Events, e => e.Event == "release.sync_conflict");
        Assert.Equal(release.Id, evt.Release!.Id);
        var detailsJson = System.Text.Json.JsonSerializer.Serialize(evt.Details);
        Assert.Contains("release/conflict", detailsJson);
        Assert.Contains("main", detailsJson);
    }

    [Fact]
    public async Task UpstreamExceptionDoesNotRecordLastSyncSoNextSweepRetries()
    {
        await CreateOpenReleaseWithBranchAsync("release/retry");
        var remote = new RecordingSyncRemote();
        remote.EnqueueMergeException(new InvalidOperationException("temporary upstream outage"));
        remote.EnqueueMerge(true);
        var service = BuildService(remote, Project(autoSync: TimeSpan.FromHours(12)));

        await service.RunSweepForTestAsync(CancellationToken.None);
        await service.RunSweepForTestAsync(CancellationToken.None);

        Assert.Equal(2, remote.MergeAttempts.Count);
        Assert.Empty(_webhooks.Events);
    }

    private ReleaseMainSyncService BuildService(RecordingSyncRemote remote, Project project)
        => new(
            _releaseStore,
            new InMemoryProjectRepository(project),
            _webhooks,
            new FixedUpstreamFactory(remote),
            NullLogger<ReleaseMainSyncService>.Instance);

    private async Task<Release> CreateOpenReleaseWithBranchAsync(
        string branchName,
        string projectId = "test-project")
    {
        var release = Release(branchName, projectId);
        await _releaseStore.CreateAsync(release);
        await _releaseStore.TrySetBranchAsync(release.Id, branchName, "base-sha");
        return (await _releaseStore.GetAsync(release.Id))!;
    }

    private static Release Release(string name, string projectId = "test-project") => new()
    {
        Id = ReleaseId.New(),
        ProjectId = new ProjectId(projectId),
        Name = name,
        State = ReleaseState.Open,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static Project Project(
        string id = "test-project",
        bool enabled = true,
        TimeSpan? autoSync = null,
        string defaultBaseBranch = "main") => new()
        {
            Id = new ProjectId(id),
            DisplayName = "Release sync UAT",
            RepositoryUrl = "https://github.com/owner/repo",
            DefaultBaseBranch = defaultBaseBranch,
            ReleaseConfig = new ProjectReleaseConfig
            {
                Enabled = enabled,
                AutoSyncMainInterval = autoSync,
            },
        };
}
