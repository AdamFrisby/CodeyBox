using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests;

/// <summary>
/// Tests state machine transitions managed by ReleaseService:
/// open → closed, open/closed → abandoned, failed → open (reopen).
/// </summary>
public sealed class ReleaseLifecycleTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"cb-rel-{Guid.NewGuid():N}.db");
    private readonly SqliteReleaseStore _releaseStore;
    private readonly SqliteWorkItemStore _workItemStore;
    private readonly CapturingWebhookDispatcher _webhooks = new();
    private readonly ReleaseService _svc;

    public ReleaseLifecycleTests()
    {
        _releaseStore = new SqliteReleaseStore(_dbPath);
        _workItemStore = new SqliteWorkItemStore(_dbPath);
        var projects = new InMemoryProjectRepository(ReleaseTestHelper.EnabledProject());
        _svc = ReleaseTestHelper.BuildService(_releaseStore, _workItemStore, projects, _webhooks);
    }

    public void Dispose()
    {
        _workItemStore.Dispose();
        _releaseStore.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task Close_TransitionsOpenToClosed_EmitsWebhook()
    {
        var rel = await CreateAsync(ReleaseState.Open);

        var (ok, err) = await _svc.CloseAsync(rel.Id, default);

        Assert.True(ok, err);
        var refreshed = await _releaseStore.GetAsync(rel.Id);
        Assert.Equal(ReleaseState.Closed, refreshed!.State);
        Assert.NotNull(refreshed.ClosedAt);
        Assert.Contains(_webhooks.Events, e => e.Event == "release.closed");
    }

    [Fact]
    public async Task Close_WhenAlreadyClosed_Fails()
    {
        var rel = await CreateAsync(ReleaseState.Open);
        await _svc.CloseAsync(rel.Id, default);

        var (ok, err) = await _svc.CloseAsync(rel.Id, default);

        Assert.False(ok);
        Assert.Contains("not Open", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Abandon_FromOpen_SetsAbandonedState_EmitsWebhook()
    {
        var rel = await CreateAsync(ReleaseState.Open);

        var (ok, err) = await _svc.AbandonAsync(rel.Id, default);

        Assert.True(ok, err);
        var refreshed = await _releaseStore.GetAsync(rel.Id);
        Assert.Equal(ReleaseState.Abandoned, refreshed!.State);
        Assert.Contains(_webhooks.Events, e => e.Event == "release.abandoned");
    }

    [Fact]
    public async Task Abandon_FromReleased_Fails()
    {
        var rel = await CreateAsync(ReleaseState.Released);

        var (ok, _) = await _svc.AbandonAsync(rel.Id, default);

        Assert.False(ok);
    }

    [Fact]
    public async Task Reopen_FromFailed_ResetsToOpen_ClearsFailedReason_EmitsWebhook()
    {
        var rel = await CreateAsync(ReleaseState.Failed, failedReason: "audit convergence exceeded");

        var (ok, err) = await _svc.ReopenAsync(rel.Id, "reverting deployment", default);

        Assert.True(ok, err);
        var refreshed = await _releaseStore.GetAsync(rel.Id);
        Assert.Equal(ReleaseState.Open, refreshed!.State);
        Assert.Null(refreshed.FailedReason);
        Assert.Contains(_webhooks.Events, e => e.Event == "release.reopened");
    }

    [Fact]
    public async Task Reopen_FromOpen_Fails()
    {
        var rel = await CreateAsync(ReleaseState.Open);

        var (ok, _) = await _svc.ReopenAsync(rel.Id, "oops", default);

        Assert.False(ok);
    }

    [Fact]
    public async Task GetAsync_UnknownId_ReturnsNull()
    {
        var result = await _releaseStore.GetAsync(ReleaseId.New());
        Assert.Null(result);
    }

    private async Task<Release> CreateAsync(ReleaseState state, string? failedReason = null)
    {
        var rel = ReleaseTestHelper.SeedRelease(state, failedReason: failedReason);
        await _releaseStore.CreateAsync(rel);
        return rel;
    }
}
