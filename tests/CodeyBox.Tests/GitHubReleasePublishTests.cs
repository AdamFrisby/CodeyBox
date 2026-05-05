using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Webhooks;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Tests that when CreateGitHubRelease=true, ReleaseService calls the upstream's
/// CreateTagAndReleaseAsync after the release branch merges to main, and that the
/// tag is derived from GitHubTagTemplate. Also verifies that when the flag is false,
/// no tag-creation call is made.
/// </summary>
public sealed class GitHubReleasePublishTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"cb-ghp-{Guid.NewGuid():N}.db");
    private readonly SqliteReleaseStore _releaseStore;
    private readonly SqliteWorkItemStore _workItemStore;
    private readonly CapturingWebhookDispatcher _webhooks = new();
    private readonly CapturingUpstreamRemote _upstream = new();

    public GitHubReleasePublishTests()
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
    public async Task Released_WithCreateGitHubRelease_CreatesTagViaUpstream()
    {
        var project = new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test",
            RepositoryUrl = "file:///tmp/noop",
            ReleaseConfig = new ProjectReleaseConfig
            {
                Enabled = true,
                CreateGitHubRelease = true,
                GitHubTagTemplate = "v{name}",
                DeepAuditors = [],
                AutoSyncMainInterval = null,
            },
        };
        var svc = BuildService(project);

        var rel = new Release
        {
            Id = ReleaseId.New(),
            ProjectId = project.Id,
            Name = "1.0",
            State = ReleaseState.Closed,
            BranchName = "release/1.0",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await _releaseStore.CreateAsync(rel);
        await SeedDoneWorkItemAsync(rel.Id, project.Id);

        await svc.OnWorkItemTerminalAsync(rel.Id, default);
        await WaitForStateAsync(rel.Id, ReleaseState.Released, ReleaseState.Failed);

        var refreshed = await _releaseStore.GetAsync(rel.Id);
        Assert.Equal(ReleaseState.Released, refreshed!.State);
        Assert.Single(_upstream.TagAndReleaseRequests);
        Assert.Equal("v1.0", _upstream.TagAndReleaseRequests[0].Tag);
    }

    [Fact]
    public async Task Released_WithTargetTagOverride_UsesTargetTag()
    {
        var project = new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test",
            RepositoryUrl = "file:///tmp/noop",
            ReleaseConfig = new ProjectReleaseConfig
            {
                Enabled = true,
                CreateGitHubRelease = true,
                GitHubTagTemplate = "v{name}",
                DeepAuditors = [],
                AutoSyncMainInterval = null,
            },
        };
        var svc = BuildService(project);

        var rel = new Release
        {
            Id = ReleaseId.New(),
            ProjectId = project.Id,
            Name = "1.0",
            State = ReleaseState.Closed,
            BranchName = "release/1.0",
            TargetTag = "custom-tag-1.0",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await _releaseStore.CreateAsync(rel);
        await SeedDoneWorkItemAsync(rel.Id, project.Id);

        await svc.OnWorkItemTerminalAsync(rel.Id, default);
        await WaitForStateAsync(rel.Id, ReleaseState.Released, ReleaseState.Failed);

        Assert.Single(_upstream.TagAndReleaseRequests);
        Assert.Equal("custom-tag-1.0", _upstream.TagAndReleaseRequests[0].Tag);
    }

    [Fact]
    public async Task Released_WithoutCreateGitHubRelease_DoesNotCallCreateTag()
    {
        var project = new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test",
            RepositoryUrl = "file:///tmp/noop",
            ReleaseConfig = new ProjectReleaseConfig
            {
                Enabled = true,
                CreateGitHubRelease = false,
                DeepAuditors = [],
                AutoSyncMainInterval = null,
            },
        };
        var svc = BuildService(project);

        var rel = new Release
        {
            Id = ReleaseId.New(),
            ProjectId = project.Id,
            Name = "2.0",
            State = ReleaseState.Closed,
            BranchName = "release/2.0",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await _releaseStore.CreateAsync(rel);
        await SeedDoneWorkItemAsync(rel.Id, project.Id);

        await svc.OnWorkItemTerminalAsync(rel.Id, default);
        await WaitForStateAsync(rel.Id, ReleaseState.Released, ReleaseState.Failed);

        Assert.Empty(_upstream.TagAndReleaseRequests);
    }

    private async Task SeedDoneWorkItemAsync(ReleaseId releaseId, ProjectId projectId)
    {
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = projectId,
            Title = "seed",
            Prompt = "work",
            Agent = AgentKind.Claude,
            ReleaseId = releaseId,
        };
        await _workItemStore.CreateAsync(item);
        await _workItemStore.UpdateAsync(item.With(WorkItemState.Done));
    }

    private ReleaseService BuildService(Project project) =>
        new ReleaseService(
            _releaseStore,
            _workItemStore,
            new InMemoryProjectRepository(project),
            _webhooks,
            new NullSandboxProvider(),
            new StubGitHost(),
            new EmptyAgentRegistry(),
            new StaticCredentialProvider(),
            new FixedUpstreamFactory(_upstream),
            [],
            new PipelineOptions { SandboxImageReference = "none", AgentAllowedHosts = [] },
            new InMemoryTaskQueue(),
            new NullHostApplicationLifetime(),
            NullLogger<ReleaseService>.Instance);

    private async Task WaitForStateAsync(ReleaseId id, params ReleaseState[] terminal)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50);
            var r = await _releaseStore.GetAsync(id);
            if (r is not null && terminal.Contains(r.State)) return;
        }
    }
}
