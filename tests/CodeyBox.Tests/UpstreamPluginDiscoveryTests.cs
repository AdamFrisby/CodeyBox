using CodeyBox.Core;
using CodeyBox.Projects;
using CodeyBox.Upstream;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that <see cref="UpstreamRemoteFactory"/> resolves a plugin-registered
/// <see cref="IUpstreamRemote"/> when <c>Upstream.Kind</c> matches the plugin's Name.
/// </summary>
public sealed class UpstreamPluginDiscoveryTests
{
    private static readonly Project GiteaProject = new()
    {
        Id = new ProjectId("gitea-project"),
        DisplayName = "Gitea Project",
        RepositoryUrl = "https://gitea.example.com/team/repo.git",
        Upstream = new ProjectUpstream
        {
            Kind = "gitea",
            PluginConfig = new Dictionary<string, string>
            {
                ["BaseUrl"] = "https://gitea.example.com/api/v1",
                ["Owner"] = "team",
                ["Repository"] = "repo",
            },
        },
    };

    [Fact]
    public void Create_PluginKind_ReturnsPluginRemote()
    {
        var plugin = new FakePluginUpstreamRemote("gitea");
        var factory = BuildFactory([plugin]);

        var remote = factory.Create(GiteaProject);

        Assert.Same(plugin, remote);
    }

    [Fact]
    public void Create_PluginKindCaseInsensitive_ReturnsPluginRemote()
    {
        var plugin = new FakePluginUpstreamRemote("Gitea");
        var factory = BuildFactory([plugin]);

        var remote = factory.Create(GiteaProject with
        {
            Upstream = GiteaProject.Upstream with { Kind = "GITEA" },
        });

        Assert.Same(plugin, remote);
    }

    [Fact]
    public void Create_NoopKind_ReturnsNoopEvenWithPlugins()
    {
        var plugin = new FakePluginUpstreamRemote("gitea");
        var factory = BuildFactory([plugin]);

        var remote = factory.Create(new Project
        {
            Id = new ProjectId("noop-project"),
            DisplayName = "Noop",
            RepositoryUrl = "https://example.com/repo.git",
            Upstream = new ProjectUpstream { Kind = "noop" },
        });

        Assert.IsType<NoopUpstreamRemote>(remote);
    }

    private static UpstreamRemoteFactory BuildFactory(IEnumerable<IUpstreamRemote> plugins)
        => new(
            gitHost: new FakeGitHost(),
            httpClientFactory: new FakeHttpClientFactory(new FakeHttpMessageHandler()),
            githubLog: NullLogger<CodeyBox.Upstream.GitHub.GitHubUpstreamRemote>.Instance,
            sandboxes: null!,
            agents: null!,
            credentials: null!,
            generatorLog: NullLogger<CodeyBox.Upstream.GitHub.LlmPullRequestDescriptionGenerator>.Instance,
            pluginRemotes: plugins,
            factoryLog: NullLogger<UpstreamRemoteFactory>.Instance);
}

internal sealed class FakePluginUpstreamRemote : IUpstreamRemote
{
    public FakePluginUpstreamRemote(string name) => Name = name;
    public string Name { get; }
    public Task<UpstreamPushResult> PushAsync(string repositoryId, string branch, CancellationToken ct = default)
        => Task.FromResult(new UpstreamPushResult(true, null));
    public Task<UpstreamCompletionOutcome> CompleteAsync(UpstreamCompletionRequest request, CancellationToken ct = default)
        => Task.FromResult(new UpstreamCompletionOutcome { Skipped = true });
}
