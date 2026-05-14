using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Projects;
using CodeyBox.Tests;
using CodeyBox.Upstream;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests.Uat.Plugins;

/// <summary>
/// UAT coverage for <c>Upstream remote plugin SDK - Allows external upstream providers</c>.
/// Plan anchor: docs/uat/00-plan.md#plugins
/// </summary>
[Collection("Pipeline integration")]
public sealed class UpstreamPluginSdkUatTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-uat-plugin-upstream-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, recursive: true);
    }

    [Fact]
    public async Task PipelineUsesPluginUpstreamRemote_AndTransitionsDone()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var remote = new RecordingPluginUpstreamRemote("gitea");
        using var pipeline = TestSupport.BuildPipeline(
            _workspace,
            seed,
            upstream: new ProjectUpstream
            {
                Kind = "gitea",
                TokenEnvVar = "CODEYBOX_UAT_GITEA_TOKEN",
                AutoMerge = true,
                MergeMethod = "squash",
            },
            upstreamFactory: new SingleRemoteFactory(remote));
        pipeline.Agent.WorkPlan.Enqueue(new FileWrite("upstream.txt", "plugin upstream\n"));
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "plugin upstream UAT",
            Prompt = "exercise plugin upstream",
            Agent = AgentKind.Claude,
            WorkBranch = "feature/plugin-upstream",
        };
        await pipeline.Store.CreateAsync(item);

        await pipeline.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await pipeline.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        var request = Assert.Single(remote.CompletionRequests);
        Assert.Equal(item.Id, request.WorkItemId);
        Assert.Equal("feature/plugin-upstream", request.WorkBranch);
        Assert.Equal("CODEYBOX_UAT_GITEA_TOKEN", request.TokenEnvVar);
        Assert.True(request.AutoMerge);
        Assert.Equal("squash", request.MergeMethod);
    }

    [Fact]
    public void FactoryResolvesPluginKindCaseInsensitively()
    {
        var remote = new RecordingPluginUpstreamRemote("Gitea");
        var factory = PluginsUatHelpers.UpstreamFactory([remote]);
        var project = ProjectWithUpstreamKind("gitea");

        var resolved = factory.Create(project);

        Assert.Same(remote, resolved);
    }

    [Fact]
    public void UpstreamPluginHost_ReturnsPerProjectPluginConfig()
    {
        var projectId = new ProjectId("plugin-upstream-config");
        var project = new Project
        {
            Id = projectId,
            DisplayName = "Plugin Upstream Config",
            RepositoryUrl = "https://example.invalid/repo.git",
            Upstream = new ProjectUpstream
            {
                Kind = "gitea",
                PluginConfig = new Dictionary<string, string>
                {
                    ["BaseUrl"] = "https://gitea.example.invalid/api/v1",
                    ["Owner"] = "team",
                    ["Repository"] = "repo",
                },
            },
        };
        var repo = new InMemoryProjectRepository(project);
        var host = new PluginHost(
            "uat.gitea-upstream",
            NullLoggerFactory.Instance,
            new ConfigurationBuilder().Build(),
            id => repo.GetAsync(id, CancellationToken.None)
                .GetAwaiter()
                .GetResult()
                ?.Upstream.PluginConfig ?? new Dictionary<string, string>());
        var upstreamHost = (IUpstreamPluginHost)host;

        var config = upstreamHost.GetProjectUpstreamConfig(projectId);

        Assert.Equal("https://gitea.example.invalid/api/v1", config["BaseUrl"]);
        Assert.Equal("team", config["Owner"]);
        Assert.Equal("repo", config["Repository"]);
        Assert.Empty(upstreamHost.GetProjectUpstreamConfig(new ProjectId("missing")));
    }

    [Fact]
    public void MissingPluginKind_ThrowsHelpfulErrorListingAvailableKinds()
    {
        var factory = PluginsUatHelpers.UpstreamFactory([new RecordingPluginUpstreamRemote("gitea")]);
        var project = ProjectWithUpstreamKind("forgejo");

        var ex = Assert.Throws<InvalidOperationException>(() => factory.Create(project));

        Assert.Contains("forgejo", ex.Message, StringComparison.Ordinal);
        Assert.Contains("noop", ex.Message, StringComparison.Ordinal);
        Assert.Contains("github", ex.Message, StringComparison.Ordinal);
        Assert.Contains("git-generic", ex.Message, StringComparison.Ordinal);
        Assert.Contains("gitea", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PluginNameCollision_DoesNotShadowBuiltInKindAndLogsWarning()
    {
        var logger = new CapturingLogger<UpstreamRemoteFactory>();
        var factory = PluginsUatHelpers.UpstreamFactory(
            [new RecordingPluginUpstreamRemote("noop")],
            logger);

        var remote = factory.Create(ProjectWithUpstreamKind("noop"));

        Assert.IsType<NoopUpstreamRemote>(remote);
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("conflicts with a built-in kind", StringComparison.Ordinal));
    }

    private static Project ProjectWithUpstreamKind(string kind) => new()
    {
        Id = new ProjectId("plugin-upstream-project"),
        DisplayName = "Plugin Upstream Project",
        RepositoryUrl = "https://example.invalid/repo.git",
        Upstream = new ProjectUpstream { Kind = kind },
    };
}

internal sealed class RecordingPluginUpstreamRemote(string name) : IUpstreamRemote
{
    public string Name { get; } = name;
    public List<UpstreamCompletionRequest> CompletionRequests { get; } = [];

    public Task<UpstreamPushResult> PushAsync(
        string repositoryId,
        string branch,
        CancellationToken ct = default)
        => Task.FromResult(new UpstreamPushResult(true, null));

    public Task<UpstreamCompletionOutcome> CompleteAsync(
        UpstreamCompletionRequest request,
        CancellationToken ct = default)
    {
        CompletionRequests.Add(request);
        return Task.FromResult(new UpstreamCompletionOutcome
        {
            BranchPushed = true,
            PullRequestUrl = "https://gitea.example.invalid/team/repo/pulls/42",
            PullRequestNumber = 42,
            MergedSha = request.MergeSha,
        });
    }

    public Task<bool> TryMergeUpstreamBranchAsync(
        string targetBranch,
        string sourceBranch,
        CancellationToken ct = default)
        => Task.FromResult(true);
}

internal sealed class SingleRemoteFactory(IUpstreamRemote remote) : IUpstreamRemoteFactory
{
    public IUpstreamRemote Create(Project project)
    {
        Assert.Equal(remote.Name, project.Upstream.Kind, ignoreCase: true);
        return remote;
    }
}
