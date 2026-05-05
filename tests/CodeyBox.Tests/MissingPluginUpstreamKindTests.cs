using CodeyBox.Core;
using CodeyBox.Projects;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that <see cref="UpstreamRemoteFactory"/> throws a helpful
/// <see cref="InvalidOperationException"/> when the project references an
/// upstream kind that is neither a built-in nor a registered plugin.
/// </summary>
public sealed class MissingPluginUpstreamKindTests
{
    private static readonly Project UnknownKindProject = new()
    {
        Id = new ProjectId("bad-project"),
        DisplayName = "Bad Project",
        RepositoryUrl = "https://example.com/repo.git",
        Upstream = new ProjectUpstream { Kind = "made-up" },
    };

    [Fact]
    public void Create_UnknownKind_ThrowsInvalidOperationException()
    {
        var factory = BuildFactory([]);

        Assert.Throws<InvalidOperationException>(() => factory.Create(UnknownKindProject));
    }

    [Fact]
    public void Create_UnknownKind_ErrorMessageContainsKindName()
    {
        var factory = BuildFactory([]);

        var ex = Assert.Throws<InvalidOperationException>(() => factory.Create(UnknownKindProject));

        Assert.Contains("made-up", ex.Message);
    }

    [Fact]
    public void Create_UnknownKind_ErrorMessageListsBuiltIns()
    {
        var factory = BuildFactory([]);

        var ex = Assert.Throws<InvalidOperationException>(() => factory.Create(UnknownKindProject));

        Assert.Contains("noop", ex.Message);
        Assert.Contains("github", ex.Message);
        Assert.Contains("git-generic", ex.Message);
    }

    [Fact]
    public void Create_UnknownKind_ErrorMessageListsRegisteredPlugins()
    {
        var plugin = new FakePluginUpstreamRemote("gitea");
        var factory = BuildFactory([plugin]);

        var ex = Assert.Throws<InvalidOperationException>(() => factory.Create(UnknownKindProject));

        Assert.Contains("gitea", ex.Message);
    }

    [Fact]
    public void Create_UnknownKind_WithSeveralPlugins_AllListedInError()
    {
        var plugins = new IUpstreamRemote[]
        {
            new FakePluginUpstreamRemote("gitea"),
            new FakePluginUpstreamRemote("forgejo"),
            new FakePluginUpstreamRemote("sourcehut"),
        };
        var factory = BuildFactory(plugins);

        var ex = Assert.Throws<InvalidOperationException>(() => factory.Create(UnknownKindProject));

        Assert.Contains("gitea", ex.Message);
        Assert.Contains("forgejo", ex.Message);
        Assert.Contains("sourcehut", ex.Message);
    }

    [Fact]
    public void Create_KnownPluginKind_DoesNotThrow()
    {
        var plugin = new FakePluginUpstreamRemote("made-up");
        var factory = BuildFactory([plugin]);

        var remote = factory.Create(UnknownKindProject);

        Assert.Same(plugin, remote);
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
