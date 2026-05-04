using CodeyBox.Core;
using CodeyBox.Projects;
using CodeyBox.Upstream.GitHub;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that a plugin registering a built-in Name (e.g. "github") cannot
/// shadow the built-in remote. Built-ins always win; the collision is logged as
/// a warning, and the plugin remote is unreachable.
/// </summary>
public sealed class UpstreamPluginNameCollisionTests
{
    [Theory]
    [InlineData("github")]
    [InlineData("GitHub")]
    [InlineData("GITHUB")]
    [InlineData("git-generic")]
    [InlineData("noop")]
    public void Create_PluginWithBuiltInName_IsNotReachable(string collisionName)
    {
        var plugin = new FakePluginUpstreamRemote(collisionName);
        var (factory, _) = BuildFactoryWithCapture([plugin]);

        // "noop" project — the built-in noop should win regardless of the plugin
        var project = new Project
        {
            Id = new ProjectId("test"),
            DisplayName = "Test",
            RepositoryUrl = "https://example.com/repo.git",
            Upstream = new ProjectUpstream { Kind = "noop" },
        };

        var remote = factory.Create(project);

        Assert.IsNotType<FakePluginUpstreamRemote>(remote);
    }

    [Fact]
    public void Create_GitHubKind_BuiltInWinsOverPlugin()
    {
        var plugin = new FakePluginUpstreamRemote("github");
        var (factory, _) = BuildFactoryWithCapture([plugin]);

        Environment.SetEnvironmentVariable("COLLISION_TEST_TOKEN", "fake-token");
        try
        {
            var project = new Project
            {
                Id = new ProjectId("gh-project"),
                DisplayName = "GH",
                RepositoryUrl = "https://github.com/org/repo.git",
                Upstream = new ProjectUpstream
                {
                    Kind = "github",
                    GitHubOwner = "org",
                    GitHubRepository = "repo",
                    TokenEnvVar = "COLLISION_TEST_TOKEN",
                },
            };

            var remote = factory.Create(project);

            Assert.IsType<GitHubUpstreamRemote>(remote);
        }
        finally
        {
            Environment.SetEnvironmentVariable("COLLISION_TEST_TOKEN", null);
        }
    }

    [Fact]
    public void Factory_PluginWithBuiltInName_LogsWarning()
    {
        var plugin = new FakePluginUpstreamRemote("github");
        var (_, logger) = BuildFactoryWithCapture([plugin]);

        Assert.True(logger.WarnLogged,
            "Expected a warning to be logged for a plugin name collision with 'github'");
    }

    [Fact]
    public void Factory_PluginWithNonCollisionName_NoWarning()
    {
        var plugin = new FakePluginUpstreamRemote("gitea");
        var (_, logger) = BuildFactoryWithCapture([plugin]);

        Assert.False(logger.WarnLogged,
            "No warning expected for a non-colliding plugin name 'gitea'");
    }

    private static (UpstreamRemoteFactory factory, CaptureLogger log) BuildFactoryWithCapture(
        IEnumerable<IUpstreamRemote> plugins)
    {
        var logger = new CaptureLogger();
        var factory = new UpstreamRemoteFactory(
            gitHost: new FakeGitHost(),
            httpClientFactory: new FakeHttpClientFactory(new FakeHttpMessageHandler()),
            githubLog: NullLogger<GitHubUpstreamRemote>.Instance,
            sandboxes: null!,
            agents: null!,
            credentials: null!,
            generatorLog: NullLogger<LlmPullRequestDescriptionGenerator>.Instance,
            pluginRemotes: plugins,
            factoryLog: logger);
        return (factory, logger);
    }
}

internal sealed class CaptureLogger : ILogger<UpstreamRemoteFactory>
{
    public bool WarnLogged { get; private set; }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel >= LogLevel.Warning)
            WarnLogged = true;
    }
}
